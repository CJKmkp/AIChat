using System.Collections.Generic;

namespace AIChat
{
    public enum ProtocolKind
    {
        OpenAiCompatible = 0,
        Anthropic = 1
    }

    public enum ProviderPreset
    {
        Custom = 0,
        DeepSeek,
        OpenAI,
        Zhipu,
        Moonshot,
        QwenDashScope,
        Ollama,
        Claude
    }

    /// <summary>
    /// 单条聊天消息。
    /// </summary>
    public class ChatMessage
    {
        public string Role { get; set; } = "";   // "system" | "user" | "assistant"
        public string Content { get; set; } = "";

        public ChatMessage() { }
        public ChatMessage(string role, string content) { Role = role; Content = content; }
    }

    /// <summary>
    /// 聊天消息的本地持久化模型。Content 不再使用（向前兼容，可忽略）。
    /// </summary>
    public class PersistedMessage
    {
        public string Role { get; set; } = "";
        public string Text { get; set; } = "";

        public ChatMessage ToRuntime() => new ChatMessage(Role, Text);
        public static PersistedMessage From(ChatMessage m) => new PersistedMessage { Role = m.Role, Text = m.Content };
    }

    /// <summary>
    /// 持久化的对话记录（含所有气泡，便于重开恢复）。
    /// </summary>
    public class PersistedSession
    {
        public List<PersistedMessage> Messages { get; set; } = new();
    }

    /// <summary>
    /// 运行时视图层消息（带渲染状态）。
    /// </summary>
    public class ChatBubbleVm : System.ComponentModel.INotifyPropertyChanged
    {
        public string Role { get; set; } = "";
        public string Text { get; set; } = "";
        public bool IsUser => Role == "user";
        public bool IsAssistant => Role == "assistant";
        public bool IsError { get; set; }

        private bool _isStreaming;
        public bool IsStreaming
        {
            get => _isStreaming;
            set { _isStreaming = value; Raise(); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        private void Raise() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(""));

        public ChatBubbleVm() { }
        public ChatBubbleVm(string role, string text)
        {
            Role = role; Text = text;
        }
    }

    /// <summary>
    /// 悬浮按钮吸附边枚举。
    /// </summary>
    public enum DockedEdge
    {
        None = 0,
        Right = 1,
        Left = 2,
        Top = 3,
        Bottom = 4
    }

    /// <summary>
    /// 悬浮按钮位置（持久化）。
    /// </summary>
    public class ButtonPositionState
    {
        public double Left { get; set; } = double.NaN;
        public double Top { get; set; } = double.NaN;
        public DockedEdge Edge { get; set; } = DockedEdge.Right;
    }

    /// <summary>
    /// 预设厂商信息。
    /// </summary>
    public class ProviderInfo
    {
        public string DisplayName { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        public string Model { get; set; } = "";
        public ProtocolKind Protocol { get; set; } = ProtocolKind.OpenAiCompatible;
    }
}