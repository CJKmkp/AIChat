using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIChat
{
    public enum ProtocolKind
    {
        OpenAiCompatible = 0,
        Anthropic = 1
    }

    /// <summary>
    /// 单个 AI 提供商配置（CCSwitch 标准结构：id/name/type/baseUrl/apiKey/model/models）。
    /// 存在 <see cref="PluginConfig.Providers"/> 列表中，可任意添加/修改/删除。
    /// </summary>
    public class ProviderConfig
    {
        /// <summary>唯一标识（新增时 Guid，配置内稳定）。</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        /// <summary>内置模板 key（deepseek/claude/...）；自定义模板为空串。</summary>
        public string Key { get; set; } = "";
        /// <summary>显示名称。</summary>
        public string Name { get; set; } = "";
        /// <summary>协议类型：openai-compatible | anthropic。</summary>
        public string Type { get; set; } = "openai-compatible";
        /// <summary>API 端点。</summary>
        public string BaseUrl { get; set; } = "";
        /// <summary>DPAPI 加密后的 API Key（Base64）。</summary>
        public string ApiKeyCipher { get; set; } = "";
        /// <summary>该 provider 当前选中的模型。</summary>
        public string Model { get; set; } = "";
        /// <summary>可用模型列表。</summary>
        public List<string> Models { get; set; } = new();

        public bool IsAnthropic =>
            string.Equals(Type, "anthropic", StringComparison.OrdinalIgnoreCase);

        /// <summary>列表 UI 用的协议徽标文字（不序列化）。</summary>
        [JsonIgnore]
        public string TypeLabel => IsAnthropic ? "Claude" : "OpenAI";
    }

    /// <summary>
    /// 插件配置：providers 列表 + 当前选中（CCSwitch 风格）+ 全局设置。
    /// JSON 持久化到 PluginConfigFolder/config.json；API Key 走 DPAPI 加密存密文。
    /// </summary>
    public class PluginConfig
    {
        /// <summary>全部已配置的提供商。</summary>
        public List<ProviderConfig> Providers { get; set; } = new();
        /// <summary>当前 provider 的 <see cref="ProviderConfig.Id"/>。</summary>
        public string CurrentProviderId { get; set; } = "";

        public string SystemPrompt { get; set; } = "你是一名教学助手，回答简洁清晰，使用中文。";
        public double Temperature { get; set; } = 0;
        /// <summary>
        /// 最大输出 tokens。0 = 不发送该字段，由服务端用默认值（推荐，避免超大值导致中转站拒绝）。
        /// 仅当用户显式设置 > 0 时才发给 OpenAI 兼容接口；Anthropic 接口必填，内部用 8192 兜底。
        /// </summary>
        public int MaxTokens { get; set; } = 0;

        public ButtonPositionState ButtonPosition { get; set; } = new ButtonPositionState();
        public PersistedSession History { get; set; } = new PersistedSession();
    }

    /// <summary>
    /// 单个内置 provider 模板（标准结构）。
    /// </summary>
    public class ProviderPreset
    {
        /// <summary>唯一 key（小写英文短码），对应添加 provider 时的模板。</summary>
        public string Key { get; set; } = "";
        /// <summary>显示名称。</summary>
        public string Name { get; set; } = "";
        /// <summary>协议类型：openai-compatible | anthropic</summary>
        public string Type { get; set; } = "openai-compatible";
        /// <summary>API 端点</summary>
        public string BaseUrl { get; set; } = "";
        /// <summary>该 provider 提供的可用模型列表。</summary>
        public List<string> Models { get; set; } = new();
    }

    /// <summary>
    /// 内置 provider 模板集合（CCSwitch 风格）。仅作为「添加 provider」时的模板，
    /// 不再承担「当前 provider」职责。key 为模板标识。
    /// </summary>
    public static class ProviderPresets
    {
        public static readonly List<ProviderPreset> Presets = new()
        {
            new ProviderPreset
            {
                Key = "deepseek",
                Name = "DeepSeek",
                Type = "openai-compatible",
                BaseUrl = "https://api.deepseek.com/v1",
                Models = new() { "deepseek-chat", "deepseek-reasoner" }
            },
            new ProviderPreset
            {
                Key = "openai",
                Name = "OpenAI",
                Type = "openai-compatible",
                BaseUrl = "https://api.openai.com/v1",
                Models = new() { "gpt-4o-mini", "gpt-4o", "gpt-4.1", "gpt-4.1-mini", "o3-mini", "o4-mini" }
            },
            new ProviderPreset
            {
                Key = "zhipu",
                Name = "智谱 GLM (OpenAI 兼容)",
                Type = "openai-compatible",
                BaseUrl = "https://open.bigmodel.cn/api/paas/v4",
                Models = new() { "glm-4-flash", "glm-4-plus", "glm-4-air" }
            },
            new ProviderPreset
            {
                Key = "moonshot",
                Name = "Moonshot Kimi (OpenAI 兼容)",
                Type = "openai-compatible",
                BaseUrl = "https://api.moonshot.cn/v1",
                Models = new() { "moonshot-v1-8k", "moonshot-v1-32k", "moonshot-v1-128k" }
            },
            new ProviderPreset
            {
                Key = "qwen",
                Name = "通义千问 DashScope (兼容模式)",
                Type = "openai-compatible",
                BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
                Models = new() { "qwen-plus", "qwen-turbo", "qwen-max", "qwen-long" }
            },
            new ProviderPreset
            {
                Key = "doubao",
                Name = "豆包 (火山方舟)",
                Type = "openai-compatible",
                BaseUrl = "https://ark.cn-beijing.volces.com/api/v3",
                Models = new() { "doubao-lite-32k", "doubao-pro-32k", "doubao-pro-256k" }
            },
            new ProviderPreset
            {
                Key = "ollama",
                Name = "Ollama (本地)",
                Type = "openai-compatible",
                BaseUrl = "http://localhost:11434/v1",
                Models = new() { "qwen2.5:7b", "llama3.2:3b", "deepseek-r1:8b" }
            },
            new ProviderPreset
            {
                Key = "claude",
                Name = "Anthropic Claude",
                Type = "anthropic",
                BaseUrl = "https://api.anthropic.com",
                Models = new() { "claude-haiku-4-5", "claude-sonnet-5", "claude-opus-5" }
            },
            new ProviderPreset
            {
                Key = "custom",
                Name = "自定义 (Custom)",
                Type = "openai-compatible",
                BaseUrl = "",
                Models = new()
            }
        };

        public static ProviderPreset FindByKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            foreach (var p in Presets)
            {
                if (string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }
    }

    /// <summary>
    /// 把 double.NaN 序列化为 null，反序列化 null 为 double.NaN。
    /// 用于 ButtonPositionState.Left/Top 的「未定位」语义（JSON 不能表示 NaN）。
    /// </summary>
    public class JsonDoubleNaNConverter : JsonConverter<double>
    {
        public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return double.NaN;
            return reader.GetDouble();
        }

        public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        {
            if (double.IsNaN(value)) { writer.WriteNullValue(); return; }
            writer.WriteNumberValue(value);
        }
    }

    /// <summary>
    /// 配置文件读写。设置历史与悬浮按钮位置同在一个 config.json 中（API Key 密文字段分离）。
    /// </summary>
    public class ConfigStore
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        static ConfigStore()
        {
            JsonOpts.Converters.Add(new JsonDoubleNaNConverter());
        }

        private readonly string _configPath;
        private readonly string _historyPath;

        public PluginConfig Current { get; private set; } = new PluginConfig();

        public ConfigStore(string pluginConfigFolder)
        {
            _configPath = Path.Combine(pluginConfigFolder, "config.json");
            _historyPath = Path.Combine(pluginConfigFolder, "history.json");
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    var cfg = JsonSerializer.Deserialize<PluginConfig>(json, JsonOpts);
                    if (cfg != null) Current = cfg;
                }
                // 旧版本（单 provider 顶层字段）迁移：providers 为空时用旧字段构造一个
                EnsureMigrated(_configPath);
                EnsureCurrentProvider();
            }
            catch
            {
                Current = new PluginConfig();
                EnsureCurrentProvider();
            }
        }

        /// <summary>
        /// 旧版本迁移：新结构 providers 为空，但旧配置顶层有 providerKey/baseUrl/model/apiKeyCipher 时，
        /// 用这些字段构造一个 provider（不丢用户已有的 key / 地址）。
        /// </summary>
        private void EnsureMigrated(string configPath)
        {
            if (Current.Providers.Count > 0) return;
            try
            {
                if (!File.Exists(configPath)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
                var root = doc.RootElement;
                if (!root.TryGetProperty("providers", out _) &&
                    (root.TryGetProperty("baseUrl", out var oldBase) || root.TryGetProperty("providerKey", out _)))
                {
                    var key = root.TryGetProperty("providerKey", out var k) ? k.GetString() : "";
                    var type = root.TryGetProperty("protocol", out var proto) && proto.GetInt32() == 1
                        ? "anthropic" : "openai-compatible";
                    var name = "自定义";
                    var preset = ProviderPresets.FindByKey(key);
                    if (preset != null) name = preset.Name;
                    var provider = new ProviderConfig
                    {
                        Key = key,
                        Name = name,
                        Type = type,
                        BaseUrl = root.TryGetProperty("baseUrl", out var b) ? b.GetString() ?? "" : "",
                        Model = root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "",
                        ApiKeyCipher = root.TryGetProperty("apiKeyCipher", out var a) ? a.GetString() ?? "" : ""
                    };
                    if (preset != null) provider.Models = new List<string>(preset.Models);
                    Current.Providers.Add(provider);
                    Current.CurrentProviderId = provider.Id;
                }
            }
            catch { /* 迁移失败则走 EnsureCurrentProvider 兜底 */ }
        }

        /// <summary>确保至少存在一个 provider，且当前 id 指向有效项。</summary>
        private void EnsureCurrentProvider()
        {
            if (Current.Providers == null) Current.Providers = new List<ProviderConfig>();
            if (Current.Providers.Count == 0)
            {
                // 全新配置：默认 DeepSeek
                var preset = ProviderPresets.FindByKey("deepseek");
                Current.Providers.Add(new ProviderConfig
                {
                    Key = "deepseek",
                    Name = preset?.Name ?? "DeepSeek",
                    Type = preset?.Type ?? "openai-compatible",
                    BaseUrl = preset?.BaseUrl ?? "https://api.deepseek.com/v1",
                    Model = preset?.Models.Count > 0 ? preset.Models[0] : "deepseek-chat",
                    Models = preset != null ? new List<string>(preset.Models) : new List<string>()
                });
            }
            // 当前 id 无效 → 指向第一个
            if (FindById(Current.CurrentProviderId) == null)
                Current.CurrentProviderId = Current.Providers[0].Id;
        }

        public void SaveConfig()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_configPath));
                var json = JsonSerializer.Serialize(Current, JsonOpts);
                File.WriteAllText(_configPath, json);
            }
            catch (Exception ex)
            {
                throw new IOException("Save config failed: " + ex.Message, ex);
            }
        }

        public void SaveHistory()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_historyPath));
                var json = JsonSerializer.Serialize(Current.History, JsonOpts);
                File.WriteAllText(_historyPath, json);
            }
            catch { }
        }

        public void LoadHistory()
        {
            try
            {
                if (!File.Exists(_historyPath)) return;
                var json = File.ReadAllText(_historyPath);
                var hist = JsonSerializer.Deserialize<PersistedSession>(json, JsonOpts);
                if (hist != null && hist.Messages != null)
                {
                    Current.History = hist;
                }
            }
            catch { }
        }

        // ---------- Provider 管理 ----------

        public List<ProviderConfig> GetAllProviders()
        {
            EnsureCurrentProvider();
            return Current.Providers;
        }

        public ProviderConfig GetCurrentProvider()
        {
            EnsureCurrentProvider();
            return FindById(Current.CurrentProviderId) ?? Current.Providers[0];
        }

        public ProviderConfig FindById(string id)
        {
            if (Current.Providers == null || string.IsNullOrEmpty(id)) return null;
            foreach (var p in Current.Providers)
            {
                if (string.Equals(p.Id, id, StringComparison.Ordinal)) return p;
            }
            return null;
        }

        public void SetCurrentProvider(string id)
        {
            EnsureCurrentProvider();
            if (FindById(id) != null) Current.CurrentProviderId = id;
        }

        /// <summary>
        /// 新增一个 provider（以内置模板填充 baseUrl/type/models/默认名），自动设为当前。
        /// 空列表时该 provider 即为唯一一个（不额外预创建默认）。
        /// </summary>
        public ProviderConfig AddProvider(string templateKey)
        {
            if (Current.Providers == null) Current.Providers = new List<ProviderConfig>();
            var preset = ProviderPresets.FindByKey(templateKey);
            var provider = new ProviderConfig
            {
                Key = templateKey,
                Name = preset?.Name ?? "自定义",
                Type = preset?.Type ?? "openai-compatible",
                BaseUrl = preset?.BaseUrl ?? "",
                Model = preset != null && preset.Models.Count > 0 ? preset.Models[0] : "",
                Models = preset != null ? new List<string>(preset.Models) : new List<string>()
            };
            Current.Providers.Add(provider);
            Current.CurrentProviderId = provider.Id;
            return provider;
        }

        /// <summary>删除 provider；至少保留一个，删除后当前 id 落到剩余第一个。</summary>
        public void RemoveProvider(string id)
        {
            if (Current.Providers == null) return;
            var idx = Current.Providers.FindIndex(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            if (idx < 0) return;
            Current.Providers.RemoveAt(idx);
            if (Current.Providers.Count == 0)
            {
                EnsureCurrentProvider(); // 补回默认
            }
            else if (string.Equals(Current.CurrentProviderId, id, StringComparison.Ordinal))
            {
                Current.CurrentProviderId = Current.Providers[0].Id;
            }
        }

        // ---------- 便捷 API（读写当前 provider） ----------

        /// <summary>当前 provider 的明文 API Key（解密）。</summary>
        public string GetApiKeyPlain()
        {
            var p = GetCurrentProvider();
            if (p == null || string.IsNullOrEmpty(p.ApiKeyCipher)) return "";
            try
            {
                var bytes = Convert.FromBase64String(p.ApiKeyCipher);
                return SecretStore.TryUnprotect(bytes);
            }
            catch
            {
                return "";
            }
        }

        /// <summary>写入当前 provider 的 API Key（加密）。</summary>
        public void SetApiKeyPlain(string plain)
        {
            var p = GetCurrentProvider();
            if (p == null) return;
            if (string.IsNullOrEmpty(plain))
            {
                p.ApiKeyCipher = "";
                return;
            }
            var cipher = SecretStore.ProtectString(plain);
            p.ApiKeyCipher = Convert.ToBase64String(cipher);
        }

        /// <summary>当前 provider 的可用模型列表。</summary>
        public List<string> GetCurrentModels()
        {
            var p = GetCurrentProvider();
            return p == null ? new List<string>() : new List<string>(p.Models);
        }

        /// <summary>当前 provider 的显示名。找不到时回退「自定义」。</summary>
        public string GetCurrentProviderName()
        {
            var p = GetCurrentProvider();
            return string.IsNullOrWhiteSpace(p?.Name) ? "自定义" : p.Name;
        }

        /// <summary>
        /// 用内置模板填充当前 provider 的 BaseUrl/Type/Models（不清空 API Key 与模型）。
        /// </summary>
        public void ApplyProvider(string key)
        {
            var preset = ProviderPresets.FindByKey(key);
            if (preset == null) return;
            var p = GetCurrentProvider();
            if (p == null) return;
            p.Key = preset.Key;
            p.Name = preset.Name;
            p.Type = preset.Type;
            p.BaseUrl = preset.BaseUrl;
            // 切换模板时，如果当前 Model 不在新列表里则切到第一个
            if (preset.Models.Count > 0)
            {
                p.Models = new List<string>(preset.Models);
                if (!preset.Models.Contains(p.Model)) p.Model = preset.Models[0];
            }
        }
    }
}
