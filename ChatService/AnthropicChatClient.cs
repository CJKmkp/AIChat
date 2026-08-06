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
    /// Anthropic Messages API 流式客户端。
    /// 使用 x-api-key + anthropic-version 头，system 提示词作为单独字段，
    /// SSE 事件解析 content_block_delta.text_delta，并感知 thinking 状态。
    /// </summary>
    public class AnthropicChatClient : IChatClient, IThinkingAwareChatClient
    {
        private const string ApiVersion = "2023-06-01";
        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly string _model;
        private readonly int _maxTokens;

        public Action<bool> OnThinkingChanged { get; set; }

        public AnthropicChatClient(string baseUrl, string apiKey, string model, int maxTokens = 8192)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _apiKey = apiKey ?? "";
            _model = model ?? "";
            // Anthropic max_tokens 必填：0（未配置）时兜底 8192
            _maxTokens = maxTokens > 0 ? maxTokens : 8192;
        }

        public async Task<string> ChatAsync(
            IReadOnlyList<ChatMessage> history,
            string systemPrompt,
            Action<string> onDelta,
            CancellationToken ct,
            Action<string> onThinkingDelta = null)
        {
            if (string.IsNullOrEmpty(_baseUrl)) throw new InvalidOperationException("BaseUrl 未设置");
            if (string.IsNullOrEmpty(_apiKey)) throw new InvalidOperationException("API Key 未设置");
            if (string.IsNullOrEmpty(_model)) throw new InvalidOperationException("Model 未设置");

            // 构建消息体（Anthropic 格式：system 单独，messages role 只能 user/assistant）
            var msgs = new List<object>();
            foreach (var m in history)
            {
                var role = m.Role == "assistant" ? "assistant" : "user";
                msgs.Add(new { role = role, content = m.Content });
            }
            var bodyDict = new Dictionary<string, object>
            {
                ["model"] = _model,
                ["max_tokens"] = _maxTokens,
                ["stream"] = true,
                ["messages"] = msgs
            };
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                bodyDict["system"] = systemPrompt;

            var json = JsonSerializer.Serialize(bodyDict);
            using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/v1/messages")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
            req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var resp = await HttpClientHolder.Shared.SendAsync(req,
                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var errText = await SafeReadAsync(resp, ct).ConfigureAwait(false);
                throw new ChatHttpException((int)resp.StatusCode, errText);
            }

            var builder = new StringBuilder();
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var sse = new SseReader(stream);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var ev = await sse.ReadEventAsync(ct).ConfigureAwait(false);
                if (ev == null) break;
                // Anthropic 流式事件：
                //   event: message_start / content_block_start / ping / content_block_delta / content_block_stop / message_delta / message_stop
                // 我们只关心 content_block_delta，data.delta.type=text_delta 时取 text
                if (ev.Event != "content_block_delta") continue;
                if (string.IsNullOrEmpty(ev.Data)) continue;
                // 检测 thinking / thinking_delta
                var isThinkingDelta = ev.Data.Contains("thinking_delta") || ev.Data.Contains("\"thinking\"");
                var isTextDelta = ev.Data.Contains("text_delta") || ev.Data.Contains("\"text\"");
                if (isThinkingDelta && !isTextDelta)
                {
                    // 进入思考中状态，且把思考内容转发出去（由 UI 折叠展示）
                    OnThinkingChanged?.Invoke(true);
                    if (onThinkingDelta != null)
                    {
                        string thinking;
                        try { thinking = ExtractThinkingDelta(ev.Data); }
                        catch (JsonException) { continue; }
                        if (!string.IsNullOrEmpty(thinking)) onThinkingDelta.Invoke(thinking);
                    }
                    continue;
                }
                string text;
                try { text = ExtractTextDelta(ev.Data); }
                catch (JsonException) { continue; }
                if (!string.IsNullOrEmpty(text))
                {
                    // 开始输出正文 = 思考结束
                    OnThinkingChanged?.Invoke(false);
                    builder.Append(text);
                    onDelta?.Invoke(text);
                }
            }
            return builder.ToString();
        }

        /// <summary>
        /// 拉取 Anthropic /v1/models 端点（Models API 为 GA，无需 beta 头）。
        /// </summary>
        public async Task<List<string>> ListModelsAsync(CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_baseUrl)) return new List<string>();
            using var req = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/v1/models?limit=100");
            req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
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

        /// <summary>
        /// 从 content_block_delta 事件 JSON 提取 text_delta.text。
        /// </summary>
        internal static string ExtractTextDelta(string dataJson)
        {
            using var doc = JsonDocument.Parse(dataJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("delta", out var delta)) return "";
            if (delta.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
            {
                var type = typeEl.GetString();
                if (type != "text_delta") return "";
            }
            if (delta.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                return textEl.GetString();
            return "";
        }

        /// <summary>
        /// 从 content_block_delta 事件 JSON 提取 thinking_delta.thinking（思考内容）。
        /// </summary>
        internal static string ExtractThinkingDelta(string dataJson)
        {
            using var doc = JsonDocument.Parse(dataJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("delta", out var delta)) return "";
            if (delta.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String)
            {
                var type = typeEl.GetString();
                if (type != "thinking_delta") return "";
            }
            if (delta.TryGetProperty("thinking", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString();
            return "";
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