using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIChat.ChatService
{
    /// <summary>
    /// 聊天客户端抽象：传入历史消息，回调增量文本（流式），返回完整助手回复。
    /// </summary>
    public interface IChatClient
    {
        Task<string> ChatAsync(
            IReadOnlyList<ChatMessage> history,
            string systemPrompt,
            Action<string> onDelta,
            CancellationToken ct);

        /// <summary>
        /// 拉取该 provider 的可用模型列表（用于设置页/聊天窗模型选择）。
        /// OpenAI 兼容：GET {base}/models；Anthropic：GET {base}/v1/models。
        /// 返回空列表表示该端点不支持或请求失败（调用方应提示）。
        /// </summary>
        Task<List<string>> ListModelsAsync(CancellationToken ct);
    }

    /// <summary>
    /// 端点测速结果（参考 CCSwitch SpeedtestService）。
    /// </summary>
    public class EndpointLatencyResult
    {
        public string Url { get; set; } = "";
        public long? LatencyMs { get; set; }
        public int? Status { get; set; }
        public string Error { get; set; } = "";
    }

    /// <summary>
    /// 带思考状态感知的聊天客户端。Anthropic 流式里有 thinking 事件，
    /// OpenAI 兼容模型通常没有；实现方可在思考期间调用 <see cref="OnThinking"/>。
    /// </summary>
    public interface IThinkingAwareChatClient : IChatClient
    {
        /// <summary>思考状态变化回调：进入思考 / 思考结束。</summary>
        Action<bool> OnThinkingChanged { get; set; }
    }

    /// <summary>
    /// 客户端异常（包含 HTTP 状态码与响应体片段，便于在聊天窗中显示）。
    /// </summary>
    public class ChatHttpException : Exception
    {
        public int StatusCode { get; }
        public string Body { get; }
        public ChatHttpException(int code, string body) : base($"HTTP {code}: {Truncate(body, 200)}")
        {
            StatusCode = code; Body = body;
        }
        private static string Truncate(string s, int max) => s == null ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");
    }

    /// <summary>
    /// 流式响应中读取到的原始增量。
    /// </summary>
    public class SseEvent
    {
        public string Event { get; set; } = "";
        public string Data { get; set; } = "";
        public bool IsDone => Data == "[DONE]";
    }

    /// <summary>
    /// 手动 SSE 解析器：基于 HttpResponseMessage 的响应流，逐行读取。
    /// 兼容两种事件形态：仅 data: 行（OpenAI），以及 event:/data: 行（Anthropic）。
    /// </summary>
    public class SseReader : IDisposable
    {
        private readonly Stream _stream;
        private readonly StreamReader _reader;
        private string _lastEvent = "";
        private bool _disposed;

        public SseReader(Stream stream)
        {
            _stream = stream;
            _reader = new StreamReader(stream, Encoding.UTF8);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _reader.Dispose(); } catch { }
        }

        /// <summary>读取下一个 SSE 事件，null 表示流结束。</summary>
        public async Task<SseEvent> ReadEventAsync(CancellationToken ct)
        {
            string data = null;
            string ev = _lastEvent;
            bool seenAny = false;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var line = await _reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                {
                    if (seenAny && data != null) { _lastEvent = ev; return new SseEvent { Event = ev, Data = data }; }
                    return null;
                }
                seenAny = true;
                if (line.Length == 0)
                {
                    // 空行 = 事件分隔
                    _lastEvent = "";
                    if (data != null) return new SseEvent { Event = ev, Data = data };
                    continue;
                }
                if (line.StartsWith(":"))
                {
                    // 注释行（OpenAI/服务端 keep-alive）
                    continue;
                }
                if (line.StartsWith("data:"))
                {
                    var payload = line.Length > 5 ? line.Substring(5) : "";
                    if (payload.StartsWith(" ")) payload = payload.Substring(1);
                    data = (data == null) ? payload : data + "\n" + payload;
                }
                else if (line.StartsWith("event:"))
                {
                    var name = line.Length > 6 ? line.Substring(6) : "";
                    if (name.StartsWith(" ")) name = name.Substring(1);
                    ev = name;
                    _lastEvent = ev;
                }
                // 忽略 id:/retry: 等字段
            }
        }
    }

    /// <summary>
    /// 共享 HttpClient，配置合理超时与默认请求头（无 Authorization）。
    /// </summary>
    internal static class HttpClientHolder
    {
        public static readonly HttpClient Shared = new HttpClient
        {
            // 流式聊天可能持续较长时间，设上限 120 秒（流建立后由流本身控制）
            Timeout = TimeSpan.FromSeconds(120)
        };
    }
}