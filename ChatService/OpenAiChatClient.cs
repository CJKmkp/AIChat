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
            CancellationToken ct,
            Action<string> onThinkingDelta = null)
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

            // content 内嵌 think 块（MiniMax-M3 / DeepSeek-R1 等经中转站的格式）拆分：
            // 思考部分转给 onThinkingDelta（UI 折叠区展示），正文部分才进 builder。
            Action<string> emitContent = s => { builder.Append(s); onDelta?.Invoke(s); };
            var splitter = new ThinkBlockSplitter(emitContent, onThinkingDelta);

            // 兼容自定义中转站：请求 stream=true 但服务端可能返回完整 JSON（非流式）
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.Contains("application/json") || contentType.Contains("text/json"))
            {
                // 非流式 JSON 响应：直接读全文解析，同样过一遍 think 块拆分
                var fullJson = await new System.IO.StreamReader(stream, Encoding.UTF8).ReadToEndAsync().ConfigureAwait(false);
                var fullText = ExtractFullContent(fullJson);
                if (!string.IsNullOrEmpty(fullText))
                {
                    splitter.Append(fullText);
                    splitter.Finish();
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
                    splitter.Append(delta);
                }
                // 思考内容（DeepSeek reasoning_content 字段）单独分流，不进正文
                if (onThinkingDelta != null)
                {
                    string thinking;
                    try { thinking = ExtractThinkingDelta(ev.Data); }
                    catch (JsonException) { continue; }
                    if (!string.IsNullOrEmpty(thinking))
                        onThinkingDelta.Invoke(thinking);
                }
            }
            splitter.Finish();
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
        /// 从 OpenAI chunk JSON 提取正文 content 增量。支持多种格式：
        /// - choices[].delta.content            （标准流式）
        /// - choices[].message.content          （某些 chunk 用 message 而非 delta）
        /// - choices[].text                     （老式 Completion 流）
        /// 思考内容 reasoning_content 由 <see cref="ExtractThinkingDelta"/> 单独提取，不进正文；
        /// content 里内嵌的 think 块（```think...``` 等）由调用方的 <see cref="ThinkBlockSplitter"/> 剥离。
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
        /// 从 OpenAI chunk JSON 提取思考增量（DeepSeek 等模型的 reasoning_content）。
        /// 与正文分离，由 UI 折叠展示。
        /// </summary>
        internal static string ExtractThinkingDelta(string dataJson)
        {
            using var doc = JsonDocument.Parse(dataJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array) return "";
            if (choices.GetArrayLength() == 0) return "";
            var first = choices[0];
            if (first.TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
            {
                return rc.GetString() ?? "";
            }
            return "";
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

    /// <summary>
    /// 流式「think 块」拆分器：把 OpenAI 兼容 content 里内嵌的思考标记块
    /// （```think ... ```/think 、[thinking]...[/thinking] 、[think]...[/think] 、
    /// &lt;think&gt;...&lt;/think&gt;）从正文中拆出来，分别增量转发给正文 / 思考回调。
    /// <para>
    /// MiniMax-M3、DeepSeek-R1 等推理模型经部分中转站时，思考内容会直接混进
    /// content 字段而不是 reasoning_content，这里负责把它分流到 UI 可折叠的思考区，
    /// 同时保证返回的正文里不含思考文本。
    /// </para>
    /// <para>
    /// 算法是增量状态机：chunk 边界可能落在标记中间，因此每次只发出「不可能是
    /// 未完成标记前缀」的部分，尾部最多扣住 <see cref="HoldBack"/> 个字符，等下一个
    /// chunk 补齐后再判定。流结束时调用 <see cref="Finish"/> 放出全部残留
    /// （未闭合的 think 块按思考处理到结尾）。
    /// </para>
    /// </summary>
    internal sealed class ThinkBlockSplitter
    {
        private static readonly string[] OpeningMarkers = { "```think", "[thinking]", "[think]", "<think>" };
        private static readonly string[] ClosingMarkers = { "```/think", "[/thinking]", "[/think]", "</think>" };

        // 最长标记 = "[/thinking]" 共 11 字符。尾部最多可能是「最长标记 - 1」的未完成前缀，
        // 必须扣住不发射，否则分块到达的标记会被当成正文吐出去。
        private const int MaxMarkerLen = 11;
        private const int HoldBack = MaxMarkerLen - 1;

        private readonly StringBuilder _buf = new StringBuilder();
        private bool _inside;                                  // 当前是否处于 think 块内
        private readonly StringBuilder _content = new StringBuilder();
        private readonly StringBuilder _thinking = new StringBuilder();

        private readonly Action<string> _onContent;
        private readonly Action<string> _onThinking;

        /// <summary>已拆出的纯正文（不含 think 块）。</summary>
        public string Content => _content.ToString();

        /// <summary>已拆出的思考内容。</summary>
        public string Thinking => _thinking.ToString();

        public ThinkBlockSplitter(Action<string> onContent = null, Action<string> onThinking = null)
        {
            _onContent = onContent;
            _onThinking = onThinking;
        }

        public void Append(string chunk)
        {
            if (string.IsNullOrEmpty(chunk)) return;
            _buf.Append(chunk);
            Process();
        }

        /// <summary>流结束：放出全部残留（未闭合的 think 块按思考处理到结尾）。</summary>
        public void Finish()
        {
            if (_buf.Length > 0)
            {
                Emit(_buf.ToString());
                _buf.Clear();
            }
        }

        private void Process()
        {
            while (_buf.Length > 0)
            {
                var s = _buf.ToString();
                var markers = _inside ? ClosingMarkers : OpeningMarkers;

                // 找缓冲里最早出现的完整标记
                int bestPos = -1, bestLen = 0;
                for (int i = 0; i < markers.Length; i++)
                {
                    var pos = s.IndexOf(markers[i], StringComparison.Ordinal);
                    if (pos >= 0 && (bestPos < 0 || pos < bestPos))
                    {
                        bestPos = pos;
                        bestLen = markers[i].Length;
                    }
                }

                if (bestPos >= 0)
                {
                    if (bestPos > 0)
                    {
                        // 标记之前的文本按当前状态先发出，然后重新扫描（此刻缓冲以标记开头）
                        Emit(s.Substring(0, bestPos));
                        _buf.Remove(0, bestPos);
                        continue;
                    }
                    // 标记就在缓冲开头：切换状态并消费标记
                    _inside = !_inside;
                    _buf.Remove(0, bestLen);
                    if (_inside)
                    {
                        // 吞掉 ```think / <think> 后紧跟的换行，思考内容从正文直接开始
                        if (_buf.Length >= 2 && _buf[0] == '\r' && _buf[1] == '\n') _buf.Remove(0, 2);
                        else if (_buf.Length >= 1 && _buf[0] == '\n') _buf.Remove(0, 1);
                    }
                    else
                    {
                        // 吞掉 </think> 后紧跟的换行（常见 </think>\n\n答案），正文不再以空行开头
                        while (_buf.Length > 0 && (_buf[0] == '\n' || _buf[0] == '\r')) _buf.Remove(0, 1);
                    }
                    continue;
                }

                // 没有完整标记：只发出不可能成为标记前缀的部分，尾部扣住等待补齐
                int emitLen = s.Length - HoldBack;
                if (emitLen > 0)
                {
                    Emit(s.Substring(0, emitLen));
                    _buf.Remove(0, emitLen);
                }
                break;
            }
        }

        private void Emit(string seg)
        {
            if (seg.Length == 0) return;
            if (_inside)
            {
                _thinking.Append(seg);
                _onThinking?.Invoke(seg);
            }
            else
            {
                _content.Append(seg);
                _onContent?.Invoke(seg);
            }
        }
    }
}