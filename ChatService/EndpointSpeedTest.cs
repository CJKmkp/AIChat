using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AIChat.ChatService
{
    /// <summary>
    /// 端点测速（参考 CCSwitch SpeedtestService）：
    /// 对 baseUrl 做 GET 请求，测量延迟与 HTTP 状态，用于连接检测。
    /// </summary>
    public static class EndpointSpeedTest
    {
        private const int DefaultTimeoutSecs = 8;
        private const int MaxTimeoutSecs = 30;
        private const int MinTimeoutSecs = 2;

        /// <summary>测试一组端点延迟（并行）。返回与输入一一对应的结果。</summary>
        public static async Task<List<EndpointLatencyResult>> TestEndpointsAsync(
            IEnumerable<string> urls,
            int? timeoutSecs = null,
            CancellationToken ct = default)
        {
            var list = urls.ToList();
            var results = new List<EndpointLatencyResult>();
            if (list.Count == 0) return results;

            var timeout = Clamp(timeoutSecs ?? DefaultTimeoutSecs);
            var tasks = list.Select(raw => TestOneAsync(raw, timeout, ct));
            var all = await Task.WhenAll(tasks).ConfigureAwait(false);
            return all.ToList();
        }

        private static async Task<EndpointLatencyResult> TestOneAsync(
            string rawUrl, int timeoutSecs, CancellationToken ct)
        {
            var url = (rawUrl ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(url))
                return new EndpointLatencyResult { Url = rawUrl, Error = "URL 不能为空" };

            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
                (parsed.Scheme != "http" && parsed.Scheme != "https"))
            {
                return new EndpointLatencyResult { Url = url, Error = "URL 无效（仅支持 http/https）" };
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSecs));

                // 热身请求（复用连接，绕过首包惩罚）
                try
                {
                    using var warmReq = new HttpRequestMessage(HttpMethod.Get, parsed);
                    using var warmResp = await HttpClientHolder.Shared.SendAsync(warmReq, cts.Token).ConfigureAwait(false);
                }
                catch { /* 忽略热身失败 */ }

                // 正式计时请求
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                using var req = new HttpRequestMessage(HttpMethod.Get, parsed);
                using var resp = await HttpClientHolder.Shared.SendAsync(req, cts.Token).ConfigureAwait(false);
                var elapsedMs = (long)((System.Diagnostics.Stopwatch.GetTimestamp() - start)
                    * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                return new EndpointLatencyResult
                {
                    Url = url,
                    LatencyMs = elapsedMs,
                    Status = (int)resp.StatusCode,
                    Error = ""
                };
            }
            catch (OperationCanceledException)
            {
                return new EndpointLatencyResult { Url = url, Error = "请求超时" };
            }
            catch (HttpRequestException ex)
            {
                return new EndpointLatencyResult { Url = url, Error = ex.Message };
            }
            catch (Exception ex)
            {
                return new EndpointLatencyResult { Url = url, Error = ex.Message };
            }
        }

        private static int Clamp(int secs)
            => Math.Max(MinTimeoutSecs, Math.Min(MaxTimeoutSecs, secs));
    }
}