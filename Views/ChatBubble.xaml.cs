using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace AIChat.Views
{
    /// <summary>
    /// 气泡操作请求参数（携带 AI 文本）。
    /// </summary>
    public class ChatBubbleRequestEventArgs : RoutedEventArgs
    {
        public string Text { get; }
        public ChatBubbleRequestEventArgs(RoutedEvent routedEvent, object source, string text)
            : base(routedEvent, source)
        {
            Text = text;
        }
    }

    /// <summary>
    /// 单条聊天气泡渲染控件：用户右对齐主色块 + 头像，AI 左对齐头像 + 段落 + 操作按钮。
    /// <para>
    /// 渲染数据来自 DataContext（<see cref="ChatBubbleVm"/>）：控件在 DataContext 变化 / Loaded 时
    /// 自动渲染，并订阅 vm 的 PropertyChanged 以支持流式追加。
    /// 不要再从外部用 ContentTemplate.LoadContent() 抓实例——那样每次都会新建一个临时对象，
    /// 设置到临时对象上的内容永远不会出现在视觉树里。
    /// </para>
    /// <para>
    /// 复制 / 插入画布 / 重新生成以「路由事件」冒泡到上层（ChatWindow 在 ItemsControl 上
    /// 用 AddHandler 统一订阅），这样 DataTemplate 内的实例无需显式接线即可生效。
    /// </para>
    /// </summary>
    public partial class ChatBubble : UserControl
    {
        public static readonly RoutedEvent CopyRequestedEvent = EventManager.RegisterRoutedEvent(
            "CopyRequested", RoutingStrategy.Bubble,
            typeof(EventHandler<ChatBubbleRequestEventArgs>), typeof(ChatBubble));

        public static readonly RoutedEvent InsertToCanvasRequestedEvent = EventManager.RegisterRoutedEvent(
            "InsertToCanvasRequested", RoutingStrategy.Bubble,
            typeof(EventHandler<ChatBubbleRequestEventArgs>), typeof(ChatBubble));

        public static readonly RoutedEvent RegenerateRequestedEvent = EventManager.RegisterRoutedEvent(
            "RegenerateRequested", RoutingStrategy.Bubble,
            typeof(EventHandler<ChatBubbleRequestEventArgs>), typeof(ChatBubble));

        /// <summary>复制请求（携带 AI 文本）。</summary>
        public event EventHandler<ChatBubbleRequestEventArgs> CopyRequested
        {
            add { AddHandler(CopyRequestedEvent, value); }
            remove { RemoveHandler(CopyRequestedEvent, value); }
        }

        /// <summary>插入画布请求（携带 AI 文本）。</summary>
        public event EventHandler<ChatBubbleRequestEventArgs> InsertToCanvasRequested
        {
            add { AddHandler(InsertToCanvasRequestedEvent, value); }
            remove { RemoveHandler(InsertToCanvasRequestedEvent, value); }
        }

        /// <summary>重新生成请求。</summary>
        public event EventHandler<ChatBubbleRequestEventArgs> RegenerateRequested
        {
            add { AddHandler(RegenerateRequestedEvent, value); }
            remove { RemoveHandler(RegenerateRequestedEvent, value); }
        }

        private ChatBubbleVm _vm;
        private bool _thinkingExpanded;

        public ChatBubble()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Loaded += (_, __) => Render(DataContext as ChatBubbleVm);
            Unloaded += (_, __) => Detach();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            Detach();
            Render(e.NewValue as ChatBubbleVm);
        }

        private void Detach()
        {
            if (_vm != null)
            {
                _vm.PropertyChanged -= OnVmPropertyChanged;
                _vm = null;
            }
        }

        private void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // vm 的 Text / IsStreaming / IsThinking 变化时刷新（流式追加走这里）
            Dispatcher.Invoke(() => Render(_vm));
        }

        /// <summary>按 vm 渲染整条气泡（用户块 / AI 块互斥）。</summary>
        private void Render(ChatBubbleVm vm)
        {
            if (vm == null) return;
            if (!ReferenceEquals(_vm, vm))
            {
                Detach();
                _vm = vm;
                _vm.PropertyChanged += OnVmPropertyChanged;
            }

            if (vm.IsUser)
            {
                UserBlock.Visibility = Visibility.Visible;
                AiBlock.Visibility = Visibility.Collapsed;
                UserText.Text = vm.Text;
                return;
            }

            UserBlock.Visibility = Visibility.Collapsed;
            AiBlock.Visibility = Visibility.Visible;
            AiText.Text = vm.Text;

            // 思考内容：有文本时显示折叠区（保持用户当前展开/折叠状态，流式追加不重置）
            var hasThinking = !string.IsNullOrEmpty(vm.Thinking);
            ThinkingSection.Visibility = hasThinking ? Visibility.Visible : Visibility.Collapsed;
            ThinkingText.Text = vm.Thinking;
            ThinkingBody.Visibility = (_thinkingExpanded && hasThinking)
                ? Visibility.Visible : Visibility.Collapsed;

            if (vm.IsThinking)
            {
                AiThinking.Visibility = Visibility.Visible;
                AiStreaming.Visibility = Visibility.Collapsed;
                AiActions.Visibility = Visibility.Collapsed;
            }
            else if (vm.IsStreaming)
            {
                AiThinking.Visibility = Visibility.Collapsed;
                AiStreaming.Visibility = Visibility.Visible;
                AiActions.Visibility = Visibility.Collapsed;
            }
            else
            {
                AiThinking.Visibility = Visibility.Collapsed;
                AiStreaming.Visibility = Visibility.Collapsed;
                AiActions.Visibility = Visibility.Visible;
            }
        }

        private void ThinkingToggle_Click(object sender, RoutedEventArgs e)
        {
            _thinkingExpanded = !_thinkingExpanded;
            var hasThinking = _vm != null && !string.IsNullOrEmpty(_vm.Thinking);
            ThinkingBody.Visibility = (_thinkingExpanded && hasThinking)
                ? Visibility.Visible : Visibility.Collapsed;
            ThinkingChevron.Text = _thinkingExpanded ? "▴" : "▾";
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            RaiseEvent(new ChatBubbleRequestEventArgs(CopyRequestedEvent, this, AiText.Text));
        }

        private void BtnInsert_Click(object sender, RoutedEventArgs e)
        {
            RaiseEvent(new ChatBubbleRequestEventArgs(InsertToCanvasRequestedEvent, this, AiText.Text));
        }

        private void BtnRegenerate_Click(object sender, RoutedEventArgs e)
        {
            RaiseEvent(new ChatBubbleRequestEventArgs(RegenerateRequestedEvent, this, AiText.Text));
        }
    }
}
