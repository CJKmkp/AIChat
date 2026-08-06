using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AIChat.ChatService
{
    /// <summary>
    /// OpenAI 兼容 Chat Completions 流式客户端。
    /// 适用于 OpenAI / DeepSeek / Moonshot / 通义 DashScope 兼容模式 / 智谱 OpenAI 端点 / Ollama 等。
    /// </summary>
    public class OpenAiChatClient : IChatClient, IThinkingAwareChatClient
    {
        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly string _model;
        private readonly double _temperature;
        private readonly int _maxTokens;

        public Action<bool> OnThinkingChanged { get; set; }

        public OpenAiChatClient(string baseUrl, string apiKey, string model,
            double temperature = 0, int maxTokens = 4096)
        {
            _baseUrl = NormalizeBaseUrl(baseUrl);
            _apiKey = apiKey ?? "";
            _model = model ?? "";
            _temperature = temperature;
            _maxTokens = maxTokens;
        }

        /// <summary>
        /// 规范 OpenAI 兼容 baseUrl：
        /// - 纯域名/主机（scheme:// 后无路径）→ 自动补 /v1，如 https://api.deepseek.com → https://api.deepseek.com/v1
        /// - 已含 /v1 或自定义路径 → 原样保留（不强行补版本）
        /// </summary>
        internal static string NormalizeBaseUrl(string baseUrl)
        {
            var trimmed = (baseUrl ?? "").Trim().TrimEnd('/');
            if (trimmed.Length == 0) return trimmed;
            // 无 scheme 时补 https://（本地 Ollama 用 http://localhost 需显式）
            if (!trimmed.Contains("://"))
            {
                trimmed = "https://" + trimmed;
            }
            // 判断是否为纯 origin（scheme:// 后不含 '/'）
            bool originOnly;
            var rest = trimmed.Substring(trimmed.IndexOf("://") + 3);
            originOnly = !rest.Contains('/');
            if (trimmed.EndsWith("/v1", System.StringComparison.OrdinalIgnoreCase))
                return trimmed;
            if (originOnly)
                return trimmed + "/v1";
            return trimmed;
        }

        public async Task<string> ChatAsync(
            IReadOnlyList<ChatMessage> history,
            string systemPrompt,
            Action<string> onDelta,
            CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_baseUrl)) throw new InvalidOperationException("BaseUrl 未设置");
            if (string.IsNullOrEmpty(_apiKey)) throw new InvalidOperationException("API Key 未设置");
            if (string.IsNullOrEmpty(_model)) throw new InvalidOperationException("Model 未设置");

            // 构建消息体（OpenAI 风格）
            var messages = new List<object>();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                messages.Add(new { role = "system", content = systemPrompt });
            foreach (var m in history)
                messages.Add(new { role = m.Role, content = m.Content });

            // 构造请求体：max_tokens 为 0 时不发送该字段（让服务端用默认值）。
            // 默认 maxTokens 配置为 0，避免超大的 max_tokens 导致部分中转站/上游拒绝或空回复。
            var bodyDict = new Dictionary<string, object>
            {
                ["model"] = _model,
                ["messages"] = messages,
                ["stream"] = true
            };
            if (_maxTokens > 0)
                bodyDict["max_tokens"] = _maxTokens;
            if (_temperature > 0)
                bodyDict["temperature"] = _temperature;

            var json = JsonSerializer.Serialize(bodyDict);
            using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var resp = await HttpClientHolder.Shared.SendAsync(req,
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var errText = await SafeReadAsync(resp, ct).ConfigureAwait(false);
                throw new ChatHttpException((int)resp.StatusCode, errText);
            }

            var builder = new StringBuilder();
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            // 兼容自定义中转站：请求 stream=true 但服务端可能返回完整 JSON（非流式）
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.Contains("application/json") || contentType.Contains("text/json"))
            {
                // 非流式 JSON 响应：直接读全文解析
                var fullJson = await new System.IO.StreamReader(stream, Encoding.UTF8).ReadToEndAsync().ConfigureAwait(false);
                var fullText = ExtractFullContent(fullJson);
                if (!string.IsNullOrEmpty(fullText))
                {
                    builder.Append(fullText);
                    onDelta?.Invoke(fullText);
                }
                return builder.ToString();
            }

            // 流式 SSE
            using var sse = new SseReader(stream);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var ev = await sse.ReadEventAsync(ct).ConfigureAwait(false);
                if (ev == null) break; // 流结束
                if (ev.IsDone) break;
                if (string.IsNullOrEmpty(ev.Data)) continue;
                string delta;
                try
                {
                    delta = ExtractDelta(ev.Data);
                }
                catch (JsonException)
                {
                    // 某些服务在流中间插入非 JSON 的 keep-alive/注释
                    continue;
                }
                if (!string.IsNullOrEmpty(delta))
                {
                    builder.Append(delta);
                    onDelta?.Invoke(delta);
                }
            }
            return builder.ToString();
        }

        /// <summary>
        /// 非流式 JSON 响应解析：OpenAI 兼容标准 choices[0].message.content。
        /// </summary>
        internal static string ExtractFullContent(string fullJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(fullJson);
                var root = doc.RootElement;
                // 标准 OpenAI /chat/completions 非流式
                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
                    && choices.GetArrayLength() > 0)
                {
                    var first = choices[0];
                    if (first.TryGetProperty("message", out var message)
                        && message.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.String)
                    {
                        return content.GetString() ?? "";
                    }
                }
                return "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// 从 OpenAI chunk JSON 提取 content 增量。支持多种格式：
        /// - choices[].delta.content            （标准流式）
        /// - choices[].delta.reasoning_content  （DeepSeek 等思考内容）
        /// - choices[].message.content          （某些 chunk 用 message 而非 delta）
        /// - choices[].text                     （老式 Completion 流）
        /// </summary>
        internal static string ExtractDelta(string dataJson)
        {
            using var doc = JsonDocument.Parse(dataJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) return "";
            if (choices.GetArrayLength() == 0) return "";
            var first = choices[0];
            string content = null;
            // 标准：delta.content
            if (first.TryGetProperty("delta", out var delta))
            {
                if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    content = c.GetString();
                // DeepSeek reasoning_content 也是正文的一部分（思考过程），加到正文
                else if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                    content = rc.GetString();
            }
            // 某些 chunk 直接用 message.content
            else if (first.TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var mc) && mc.ValueKind == JsonValueKind.String)
            {
                content = mc.GetString();
            }
            // 老式 Completion 流 text
            else if (first.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
            {
                content = t.GetString();
            }
            return content ?? "";
        }

        /// <summary>
        /// 拉取 OpenAI 兼容 /models 端点。
        /// </summary>
        public async Task<List<string>> ListModelsAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_baseUrl)) return new List<string>();
            using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/models");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            using var resp = await HttpClientHolder.Shared.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var errText = await SafeReadAsync(resp, ct).ConfigureAwait(false);
                throw new ChatHttpException((int)resp.StatusCode, errText);
            }
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var result = new List<string>();
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in data.EnumerateArray())
                    {
                        if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                        {
                            var s = id.GetString();
                            if (!string.IsNullOrEmpty(s)) result.Add(s);
                        }
                    }
                }
            }
            return result;
        }

        private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
        {
            try
            {
                return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                return "";
            }
        }
    }
}