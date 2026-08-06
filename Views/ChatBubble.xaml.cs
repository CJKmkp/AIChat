using System;
using System.Windows;
using System.Windows.Controls;

namespace AIChat.Views
{
    /// <summary>
    /// 单条聊天气泡渲染控件：无气泡布局，用户右对齐头像+主色文字块，AI 左对齐头像+段落+操作按钮。
    /// 通过 SetBubble(ChatBubbleVm) 注入数据。
    /// </summary>
    public partial class ChatBubble : UserControl
    {
        public event Action<string> CopyRequested;
        public event Action<string> InsertToCanvasRequested;
        public event Action RegenerateRequested;

        public ChatBubble()
        {
            InitializeComponent();
        }

        public void SetBubble(ChatBubbleVm vm)
        {
            if (vm == null) return;
            if (vm.IsUser)
            {
                UserBlock.Visibility = Visibility.Visible;
                AiBlock.Visibility = Visibility.Collapsed;
                UserText.Text = vm.Text;
            }
            else
            {
                UserBlock.Visibility = Visibility.Collapsed;
                AiBlock.Visibility = Visibility.Visible;
                AiText.Text = vm.Text;
                AiStreaming.Visibility = vm.IsStreaming ? Visibility.Visible : Visibility.Collapsed;
                AiActions.Visibility = vm.IsStreaming ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        public void AppendText(string delta)
        {
            AiText.Text = (AiText.Text ?? "") + delta;
            AiStreaming.Visibility = Visibility.Visible;
            AiActions.Visibility = Visibility.Collapsed;
        }

        public void FinishStreaming()
        {
            AiStreaming.Visibility = Visibility.Collapsed;
            AiActions.Visibility = Visibility.Visible;
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            CopyRequested?.Invoke(AiText.Text);
        }

        private void BtnInsert_Click(object sender, RoutedEventArgs e)
        {
            InsertToCanvasRequested?.Invoke(AiText.Text);
        }

        private void BtnRegenerate_Click(object sender, RoutedEventArgs e)
        {
            RegenerateRequested?.Invoke();
        }
    }
}