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
    /// 插件配置：参考 CCSwitch 等业内 AI 客户端标准结构（id/name/type/baseUrl/apiKey/model/models/temperature/maxTokens/systemPrompt）。
    /// JSON 持久化到 PluginConfigFolder/config.json；API Key 走 DPAPI 加密存密文。
    /// </summary>
    public class PluginConfig
    {
        /// <summary>当前 provider 标识（对应 <see cref="ProviderPresets.Presets"/> 的 key，如 "deepseek"/"claude"）。</summary>
        public string ProviderKey { get; set; } = "deepseek";
        public ProtocolKind Protocol { get; set; } = ProtocolKind.OpenAiCompatible;

        public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";
        public string Model { get; set; } = "deepseek-chat";
        public string SystemPrompt { get; set; } = "你是一名教学助手，回答简洁清晰，使用中文。";
        public double Temperature { get; set; } = 0;
        public int MaxTokens { get; set; } = 4096;

        /// <summary>DPAPI 加密后的 API Key 字节（Base64 字符串保存）。</summary>
        public string ApiKeyCipher { get; set; } = "";

        public ButtonPositionState ButtonPosition { get; set; } = new ButtonPositionState();
        public PersistedSession History { get; set; } = new PersistedSession();
    }

    /// <summary>
    /// 单个 AI 提供商预设（标准结构）。
    /// </summary>
    public class ProviderPreset
    {
        /// <summary>唯一 key（小写英文短码），对应 PluginConfig.ProviderKey。</summary>
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
    /// 内置 provider 预设集合（标准 CCSwitch 风格）。key 为 provider 标识。
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
                // 旧版本兼容：迁移旧的 ProtocolKind / Preset 字段
                if (string.IsNullOrEmpty(Current.ProviderKey))
                {
                    Current.ProviderKey = "deepseek";
                }
            }
            catch
            {
                Current = new PluginConfig();
            }
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

        public string GetApiKeyPlain()
        {
            if (string.IsNullOrEmpty(Current.ApiKeyCipher)) return "";
            try
            {
                var bytes = Convert.FromBase64String(Current.ApiKeyCipher);
                return SecretStore.TryUnprotect(bytes);
            }
            catch
            {
                return "";
            }
        }

        public void SetApiKeyPlain(string plain)
        {
            if (string.IsNullOrEmpty(plain))
            {
                Current.ApiKeyCipher = "";
                return;
            }
            var cipher = SecretStore.ProtectString(plain);
            Current.ApiKeyCipher = Convert.ToBase64String(cipher);
        }

        /// <summary>
        /// 应用 provider 预设：填入 BaseUrl、协议、默认模型。不清空 API Key。
        /// </summary>
        public void ApplyProvider(string key)
        {
            var preset = ProviderPresets.FindByKey(key);
            if (preset == null) return;
            Current.ProviderKey = preset.Key;
            Current.BaseUrl = preset.BaseUrl;
            Current.Protocol = string.Equals(preset.Type, "anthropic", StringComparison.OrdinalIgnoreCase)
                ? ProtocolKind.Anthropic
                : ProtocolKind.OpenAiCompatible;
            // 切换 provider 时，如果当前 Model 不在新 provider 的模型列表里，则切到 provider 的第一个模型
            if (preset.Models.Count > 0)
            {
                if (!preset.Models.Contains(Current.Model))
                {
                    Current.Model = preset.Models[0];
                }
            }
        }

        /// <summary>当前 provider 的可用模型列表（用于设置页/聊天窗模型下拉）。</summary>
        public List<string> GetCurrentModels()
        {
            var preset = ProviderPresets.FindByKey(Current.ProviderKey);
            if (preset == null) return new List<string>();
            return new List<string>(preset.Models);
        }

        /// <summary>当前 provider 的显示名。找不到时回退 BaseUrl。</summary>
        public string GetCurrentProviderName()
        {
            var preset = ProviderPresets.FindByKey(Current.ProviderKey);
            return preset?.Name ?? "自定义";
        }
    }
}