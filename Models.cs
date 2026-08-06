using System.Collections.Generic;

namespace AIChat
{
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
    /// 聊天消息的本地持久化模型。
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
    /// 运行时视图层消息（带渲染状态）。属性变化会通知 ChatBubble 重新渲染（支持流式追加）。
    /// </summary>
    public class ChatBubbleVm : System.ComponentModel.INotifyPropertyChanged
    {
        public string Role { get; set; } = "";

        private string _text = "";
        public string Text
        {
            get => _text;
            set { _text = value; Raise(nameof(Text)); }
        }

        public bool IsUser => Role == "user";
        public bool IsAssistant => Role == "assistant";
        public bool IsError { get; set; }

        private bool _isStreaming;
        public bool IsStreaming
        {
            get => _isStreaming;
            set { _isStreaming = value; Raise(nameof(IsStreaming)); }
        }

        private bool _isThinking;
        /// <summary>AI 正在思考（尚未输出正文）：显示三点动画。</summary>
        public bool IsThinking
        {
            get => _isThinking;
            set { _isThinking = value; Raise(nameof(IsThinking)); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        private void Raise(string name = "")
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

        public ChatBubbleVm() { }
        public ChatBubbleVm(string role, string text)
        {
            Role = role; _text = text ?? "";
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
}