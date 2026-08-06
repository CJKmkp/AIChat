using System.Collections.Generic;

namespace AIChat
{
    /// <summary>
    /// 中英双语字符串表。优先级：中文(zh) → 英文(en) → 键名。
    /// 插件不引入 resx，以减少宿主依赖与构建复杂度。
    /// </summary>
    public static class Strings
    {
        private static readonly Dictionary<string, (string Zh, string En)> Table = new()
        {
            // 悬浮按钮
            ["Floating_ToolTip"]       = ("AI 助手", "AI Assistant"),
            ["Floating_Menu_OpenChat"] = ("打开聊天", "Open Chat"),
            ["Floating_Menu_Settings"] = ("设置", "Settings"),
            ["Floating_Menu_Hide"]     = ("隐藏悬浮按钮", "Hide Floating Button"),
            ["Floating_Menu_Show"]     = ("显示悬浮按钮", "Show Floating Button"),

            // 聊天窗
            ["Chat_Title_Default"]    = ("AI 助手", "AI Assistant"),
            ["Chat_InputPlaceholder"] = ("输入消息，回车发送", "Type a message, press Enter to send"),
            ["Chat_Btn_Send"]         = ("发送", "Send"),
            ["Chat_Btn_Stop"]         = ("停止", "Stop"),
            ["Chat_Btn_Clear"]        = ("清空对话", "Clear"),
            ["Chat_Btn_Close"]        = ("关闭", "Close"),
            ["Chat_Btn_Resize"]       = ("拖动调整大小", "Drag to resize"),
            ["Chat_Bubble_Copy"]      = ("复制", "Copy"),
            ["Chat_Bubble_Insert"]    = ("插入画布", "Insert to Canvas"),
            ["Chat_Bubble_Regenerate"]= ("重新生成", "Regenerate"),
            ["Chat_Streaming"]        = ("正在输入…", "Typing…"),
            ["Chat_ConfirmClear"]     = ("确认清空当前对话？", "Clear current conversation?"),
            ["Chat_Status_Ready"]     = ("就绪", "Ready"),
            ["Chat_Status_Connecting"]= ("连接中…", "Connecting…"),
            ["Chat_Status_Streaming"]  = ("生成中…", "Streaming…"),

            // 错误与提示
            ["Err_Network"]        = ("网络请求失败：{0}", "Network error: {0}"),
            ["Err_HttpStatus"]     = ("接口返回 {0}：{1}", "HTTP {0}: {1}"),
            ["Err_NoApiKey"]       = ("请先在设置中填写 API Key", "Please set the API key in Settings"),
            ["Err_NoBaseUrl"]      = ("请先在设置中填写接口地址", "Please set the Base URL in Settings"),
            ["Err_NoModel"]        = ("请先在设置中填写模型名称", "Please set the model name in Settings"),
            ["Info_Copied"]        = ("已复制", "Copied"),
            ["Info_Inserted"]      = ("已插入画布", "Inserted to Canvas"),
            ["Info_InsertFailed"]  = ("插入画布失败", "Failed to insert into Canvas"),
            ["Info_TestOk"]        = ("连接成功", "Connection OK"),
            ["Info_TestFail"]      = ("连接失败：{0}", "Connection failed: {0}"),

            // 设置页
            ["Settings_Title"]        = ("AI 助手设置", "AI Assistant Settings"),
            ["Settings_Protocol"]     = ("接口协议", "Protocol"),
            ["Settings_Preset"]       = ("预设厂商", "Preset"),
            ["Settings_Preset_Custom"]= ("自定义", "Custom"),
            ["Settings_BaseUrl"]      = ("接口地址 (Base URL)", "Base URL"),
            ["Settings_BaseUrl_Hint"] = ("例如 https://api.deepseek.com/v1", "e.g. https://api.deepseek.com/v1"),
            ["Settings_ApiKey"]       = ("API Key", "API Key"),
            ["Settings_ShowKey"]      = ("显示", "Show"),
            ["Settings_HideKey"]      = ("隐藏", "Hide"),
            ["Settings_Model"]        = ("模型", "Model"),
            ["Settings_Model_Hint"]   = ("例如 deepseek-chat / gpt-4o-mini / claude-haiku-4-5", "e.g. deepseek-chat / gpt-4o-mini / claude-haiku-4-5"),
            ["Settings_SystemPrompt"] = ("系统提示词", "System Prompt"),
            ["Settings_SystemHint"]   = ("定义助手角色、语气、回答格式", "Define persona, tone, output format"),
            ["Settings_Temperature"]  = ("温度", "Temperature"),
            ["Settings_MaxTokens"]    = ("最大 tokens", "Max Tokens"),
            ["Settings_Btn_Save"]     = ("保存", "Save"),
            ["Settings_Btn_Cancel"]   = ("取消", "Cancel"),
            ["Settings_Btn_Test"]     = ("测试连接", "Test Connection"),
            ["Settings_Saved"]        = ("已保存", "Saved"),
            ["Settings_ProtocolNote"] = ("Anthropic Claude 协议：使用 x-api-key 与 anthropic-version 头，系统提示词单独作为 system 字段发送。",
                                          "Anthropic Claude protocol uses x-api-key + anthropic-version headers, system prompt sent as separate system field."),
            ["Settings_OpenAiNote"]   = ("OpenAI 兼容协议：Authorization: Bearer + /chat/completions SSE 流式。可搭配 DeepSeek/Kimi/通义/豆包/智谱/Ollama 等。",
                                          "OpenAI compatible protocol: Authorization Bearer + /chat/completions SSE. Works with DeepSeek/Kimi/Qwen/Doubao/Zhipu/Ollama."),

            // 提供商管理（多 provider）
            ["Provider_ListTitle"]    = ("提供商", "Providers"),
            ["Provider_Add"]          = ("＋ 添加", "＋ Add"),
            ["Provider_Delete"]       = ("删除", "Delete"),
            ["Provider_SelectTemplate"]= ("选择模板", "Choose template"),
            ["Provider_CantDeleteLast"]= ("至少保留一个提供商", "Keep at least one provider"),
            ["Provider_ConfirmDelete"]= ("确认删除提供商「{0}」？此操作不可撤销。", "Delete provider \"{0}\"? This cannot be undone."),
            ["Provider_Added"]        = ("已添加提供商", "Provider added"),
            ["Provider_Deleted"]      = ("已删除提供商", "Provider deleted")
        };

        public static string Get(string key, string lang = "zh")
        {
            if (Table.TryGetValue(key, out var v))
            {
                return lang == "en" ? v.En : v.Zh;
            }
            return key;
        }

        public static string Get(string key, params object[] args)
        {
            return string.Format(Get(key), args);
        }
    }

    /// <summary>
    /// XAML 静态绑定入口：<c>{x:Static ai:Strings.Current}</c>。
    /// 由于静态类 Strings 在 XAML 静态绑定里无法直接拿"实例"，所以这里提供
    /// 一个公开属性供 <c>{Binding ..., Source={x:Static ai:Strings.Current}}</c> 使用。
    /// </summary>
    public static class Localized
    {
        public static StringsAccessor Current { get; } = new StringsAccessor();
    }

    public sealed class StringsAccessor
    {
        // 所有 XAML 用到的键都暴露成强类型属性，便于 {Binding Foo, Source={x:Static ai:Localized.Current}}
        public string Floating_ToolTip => Strings.Get("Floating_ToolTip");
        public string BtnCopy => Strings.Get("Chat_Bubble_Copy");
        public string BtnInsert => Strings.Get("Chat_Bubble_Insert");
        public string BtnRegenerate => Strings.Get("Chat_Bubble_Regenerate");
        public string Streaming => Strings.Get("Chat_Streaming");
        public string BtnSend => Strings.Get("Chat_Btn_Send");
        public string BtnStop => Strings.Get("Chat_Btn_Stop");
        public string BtnClear => Strings.Get("Chat_Btn_Clear");
        public string BtnResize => Strings.Get("Chat_Btn_Resize");
        public string Title => Strings.Get("Settings_Title");
        public string ProviderListTitle => Strings.Get("Provider_ListTitle");
        public string Provider_Add => Strings.Get("Provider_Add");
        public string Provider_Delete => Strings.Get("Provider_Delete");
    }
}