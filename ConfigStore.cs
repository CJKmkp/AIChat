using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIChat
{
    /// <summary>
    /// 插件配置（API 设置 + 聊天历史 + 悬浮按钮位置），JSON 持久化到 PluginConfigFolder/config.json。
    /// 配置 JSON 含有 ApiKey 密文字段，密钥本体存密文（DPAPI 加密），不暴露明文。
    /// </summary>
    public class PluginConfig
    {
        public ProtocolKind Protocol { get; set; } = ProtocolKind.OpenAiCompatible;
        public ProviderPreset Preset { get; set; } = ProviderPreset.DeepSeek;
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
    /// 提供商预设定义。
    /// </summary>
    internal static class ProviderPresets
    {
        public static readonly Dictionary<ProviderPreset, ProviderInfo> Map = new()
        {
            [ProviderPreset.DeepSeek] = new ProviderInfo
            {
                DisplayName = "DeepSeek",
                BaseUrl = "https://api.deepseek.com/v1",
                Model = "deepseek-chat",
                Protocol = ProtocolKind.OpenAiCompatible
            },
            [ProviderPreset.OpenAI] = new ProviderInfo
            {
                DisplayName = "OpenAI",
                BaseUrl = "https://api.openai.com/v1",
                Model = "gpt-4o-mini",
                Protocol = ProtocolKind.OpenAiCompatible
            },
            [ProviderPreset.Zhipu] = new ProviderInfo
            {
                DisplayName = "智谱 GLM（OpenAI 兼容）",
                BaseUrl = "https://open.bigmodel.cn/api/paas/v4",
                Model = "glm-4-flash",
                Protocol = ProtocolKind.OpenAiCompatible
            },
            [ProviderPreset.Moonshot] = new ProviderInfo
            {
                DisplayName = "Moonshot Kimi（OpenAI 兼容）",
                BaseUrl = "https://api.moonshot.cn/v1",
                Model = "moonshot-v1-8k",
                Protocol = ProtocolKind.OpenAiCompatible
            },
            [ProviderPreset.QwenDashScope] = new ProviderInfo
            {
                DisplayName = "通义千问（DashScope 兼容模式）",
                BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1",
                Model = "qwen-plus",
                Protocol = ProtocolKind.OpenAiCompatible
            },
            [ProviderPreset.Ollama] = new ProviderInfo
            {
                DisplayName = "Ollama（本地）",
                BaseUrl = "http://localhost:11434/v1",
                Model = "qwen2.5:7b",
                Protocol = ProtocolKind.OpenAiCompatible
            },
            [ProviderPreset.Claude] = new ProviderInfo
            {
                DisplayName = "Anthropic Claude",
                BaseUrl = "https://api.anthropic.com",
                Model = "claude-haiku-4-5",
                Protocol = ProtocolKind.Anthropic
            },
            [ProviderPreset.Custom] = new ProviderInfo
            {
                DisplayName = "自定义",
                BaseUrl = "",
                Model = "",
                Protocol = ProtocolKind.OpenAiCompatible
            }
        };
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
            }
            catch
            {
                // 加载失败时回退到默认配置；保留旧文件不覆盖
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
            catch
            {
                // 历史保存失败不阻塞
            }
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
            catch
            {
                // 历史损坏时静默忽略
            }
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

        public void ApplyPreset(ProviderPreset preset, bool keepKeyAndOverride = true)
        {
            Current.Preset = preset;
            if (!ProviderPresets.Map.TryGetValue(preset, out var info)) return;
            Current.BaseUrl = info.BaseUrl;
            Current.Model = info.Model;
            // 协议跟随预设（Claude 走 Anthropic 协议）
            Current.Protocol = info.Protocol;
            // 自定义时保持用户现有值；其他预设应用模板
            if (preset == ProviderPreset.Custom) return;
        }
    }
}