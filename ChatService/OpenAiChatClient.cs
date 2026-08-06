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
    public class OpenAiChatClient : IChatClient
    {
        private readonly string _baseUrl;
        private readonly string _apiKey;
        private readonly string _model;
        private readonly double _temperature;
        private readonly int _maxTokens;

        public OpenAiChatClient(string baseUrl, string apiKey, string model,
            double temperature = 0, int maxTokens = 4096)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _apiKey = apiKey ?? "";
            _model = model ?? "";
            _temperature = temperature;
            _maxTokens = maxTokens;
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

            var body = (object)new
            {
                model = _model,
                messages = messages,
                stream = true,
                max_tokens = _maxTokens
            };
            // 仅当用户显式设了 temperature 才发送
            if (_temperature > 0)
                body = new { model = _model, messages = messages, stream = true, temperature = _temperature, max_tokens = _maxTokens };

            var json = JsonSerializer.Serialize(body);
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
        /// 从 OpenAI chunk JSON 提取 content 增量。
        /// </summary>
        private static string ExtractDelta(string dataJson)
        {
            using var doc = JsonDocument.Parse(dataJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) return "";
            if (choices.GetArrayLength() == 0) return "";
            var first = choices[0];
            if (!first.TryGetProperty("delta", out var delta)) return "";
            if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                return c.GetString();
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