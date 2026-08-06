using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AIChat.ChatService;
using Ink_Canvas.Plugins;
using ContentDialog = iNKORE.UI.WPF.Modern.Controls.ContentDialog;
using ContentDialogButton = iNKORE.UI.WPF.Modern.Controls.ContentDialogButton;
using ContentDialogResult = iNKORE.UI.WPF.Modern.Controls.ContentDialogResult;

namespace AIChat.Views
{
    /// <summary>
    /// AI 聊天主窗口：Qwen 风格布局——顶部模型下拉、中部无气泡消息列表、底部胶囊输入条。
    /// </summary>
    public partial class ChatWindow : Window
    {
        public AIChatPlugin Plugin { get; set; }

        public ObservableCollection<ChatBubbleVm> Messages { get; } = new ObservableCollection<ChatBubbleVm>();

        private CancellationTokenSource _cts;

        // 右下角缩放柄用的系统消息（与 WPF 内置 ResizeGrip 同款机制）
        private const int WmSysCommand = 0x0112;
        private const int ScSize = 0xF000;
        private const int HtBottomRight = 17;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public ChatWindow()
        {
            InitializeComponent();
            MessagesList.ItemsSource = Messages;
            UpdateModelLabel();
            Messages.CollectionChanged += (_, __) => UpdateEmptyState();
            // 气泡操作按钮以路由事件冒泡到 ItemsControl 统一处理（DataTemplate 内无法用 EventSetter 绑普通 CLR 事件）
            MessagesList.AddHandler(ChatBubble.CopyRequestedEvent,
                new EventHandler<ChatBubbleRequestEventArgs>(OnCopy));
            MessagesList.AddHandler(ChatBubble.InsertToCanvasRequestedEvent,
                new EventHandler<ChatBubbleRequestEventArgs>(OnInsertToCanvas));
            MessagesList.AddHandler(ChatBubble.RegenerateRequestedEvent,
                new EventHandler<ChatBubbleRequestEventArgs>(OnRegenerate));
            InputBox.Focus();
        }

        public void LoadHistory(IEnumerable<ChatMessage> history)
        {
            Messages.Clear();
            if (history == null) return;
            foreach (var m in history)
            {
                if (m.Role != "user" && m.Role != "assistant") continue;
                var vm = new ChatBubbleVm(m.Role, m.Content);
                AddBubble(vm);
            }
            UpdateEmptyState();
            ScrollToEnd();
        }

        private void UpdateEmptyState()
        {
            if (Messages.Count == 0)
            {
                EmptyState.Visibility = Visibility.Visible;
                MessagesScroll.Visibility = Visibility.Collapsed;
            }
            else
            {
                EmptyState.Visibility = Visibility.Collapsed;
                MessagesScroll.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// 刷新左上角模型标签。显示当前 provider 的「模型名」。
        /// 配置为空时才显示「未配置」。
        /// </summary>
        public void UpdateModelLabel()
        {
            var p = Plugin?.ConfigStore?.GetCurrentProvider();
            if (p == null || string.IsNullOrWhiteSpace(p.Model))
            {
                ModelNameText.Text = "未配置";
                return;
            }
            ModelNameText.Text = p.Model;
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) return;
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        /// <summary>
        /// 自绘缩放柄按下：向窗口发 SC_SIZE + HTBOTTOMRIGHT 进入系统缩放循环，
        /// 拖动全程由系统接管，最小/最大尺寸约束依然生效。
        /// </summary>
        private void ResizeGrip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            try
            {
                SendMessage(new WindowInteropHelper(this).Handle,
                    (uint)WmSysCommand, new IntPtr(ScSize + HtBottomRight), IntPtr.Zero);
            }
            catch { }
        }

        private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                DoSend();
            }
        }

        private void BtnSend_Click(object sender, RoutedEventArgs e) => DoSend();

        private void DoSend()
        {
            var text = (InputBox.Text ?? "").Trim();
            if (text.Length == 0) return;
            if (_cts != null) return;

            InputBox.Clear();
            AppendUserMessage(text);
            _ = RunChatAsync(text);
        }

        private void AppendUserMessage(string text)
        {
            var vm = new ChatBubbleVm("user", text);
            AddBubble(vm);
            Plugin?.AppendRuntimeHistory(vm);
            ScrollToEnd();
        }

        /// <summary>
        /// 添加一条气泡。渲染完全由 ChatBubble 通过 DataContext + PropertyChanged 驱动，
        /// 这里只负责把 vm 加进集合，不再手动抓控件实例（LoadContent 会新建临时对象，设置无效）。
        /// </summary>
        private void AddBubble(ChatBubbleVm vm)
        {
            Messages.Add(vm);
        }

        private async Task RunChatAsync(string userText)
        {
            _cts = new CancellationTokenSource();
            BtnSend.Visibility = Visibility.Collapsed;
            BtnStop.Visibility = Visibility.Visible;

            try
            {
                var client = Plugin?.CreateClient();
                if (client == null)
                {
                    AddAssistantError(Strings.Get("Err_NoApiKey"));
                    return;
                }

                var history = new List<ChatMessage>();
                foreach (var m in Messages)
                {
                    if (m.IsUser || m.IsAssistant)
                        history.Add(new ChatMessage(m.Role, m.Text));
                }
                var systemPrompt = Plugin?.GetSystemPrompt() ?? "";

                var aiVm = new ChatBubbleVm("assistant", "") { IsStreaming = true };
                AddBubble(aiVm);
                ScrollToEnd();

                string finalText = "";
                var onDelta = new Action<string>(delta =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        aiVm.Text += delta;
                        MessagesScroll.ScrollToEnd();
                    });
                });

                // 思考内容回调：DeepSeek reasoning_content / Claude thinking /
                // MiniMax 等 content 内嵌 think 块（OpenAiChatClient 已拆分）单独累积，
                // 由气泡在「思考过程」折叠区展示，不混进正文。
                var onThinkingDelta = new Action<string>(thinking =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        aiVm.Thinking += thinking;
                        aiVm.IsThinking = false; // 已有思考文本，不再是纯三点等待态
                        MessagesScroll.ScrollToEnd();
                    });
                });

                // 思考状态回调：AI 在生成正文前若进入思考，则显示「思考中」动画
                if (client is IThinkingAwareChatClient thinkingClient)
                {
                    thinkingClient.OnThinkingChanged = thinking =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            aiVm.IsThinking = thinking;
                            if (thinking) MessagesScroll.ScrollToEnd();
                        });
                    };
                }

                try
                {
                    finalText = await client.ChatAsync(history, systemPrompt, onDelta, _cts.Token, onThinkingDelta);
                }
                catch (ChatHttpException hex)
                {
                    Plugin?.LogChatFailure("ChatAsync", hex);
                    aiVm.IsStreaming = false;
                    aiVm.IsThinking = false;
                    AddAssistantError(string.Format(Strings.Get("Err_HttpStatus"), hex.StatusCode, hex.Message));
                    return;
                }
                catch (OperationCanceledException)
                {
                    aiVm.IsStreaming = false;
                    aiVm.IsThinking = false;
                    return;
                }
                catch (Exception ex)
                {
                    aiVm.IsStreaming = false;
                    aiVm.IsThinking = false;
                    AddAssistantError(string.Format(Strings.Get("Err_Network"), ex.Message));
                    return;
                }

                aiVm.Text = finalText;
                aiVm.IsStreaming = false;
                aiVm.IsThinking = false;
                Plugin?.AppendRuntimeHistory(aiVm);
                ScrollToEnd();
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                BtnSend.Visibility = Visibility.Visible;
                BtnStop.Visibility = Visibility.Collapsed;
            }
        }

        private void AddAssistantError(string errText)
        {
            var vm = new ChatBubbleVm("assistant", errText) { IsError = true };
            AddBubble(vm);
            ScrollToEnd();
        }

        private void ScrollToEnd()
        {
            Dispatcher.BeginInvoke(new Action(() => MessagesScroll.ScrollToEnd()), DispatcherPriority.Background);
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            try { _cts?.Cancel(); } catch { }
        }

        private async void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            if (Messages.Count == 0) return;
            var dialog = new ContentDialog
            {
                Title = Strings.Get("Chat_Btn_Clear"),
                Content = Strings.Get("Chat_ConfirmClear"),
                PrimaryButtonText = Strings.Get("Chat_Btn_Clear"),
                SecondaryButtonText = Strings.Get("Settings_Btn_Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            Messages.Clear();
            Plugin?.ClearRuntimeHistory();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        // ---------- 模型选择下拉（只列「已配置」providers 及其模型） ----------
        // 规则：遍历 config 的 Providers（用户添加/保留的才叫已配置），每个 provider 一个分组，
        // 子菜单列其 Models；点模型即切 provider + 换模型。内置模板不直接列出。
        private void ModelPicker_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 下拉在标题栏拖动 Grid 内：吞掉按下事件，避免冒泡触发 DragMove()
            e.Handled = true;
        }

        private void ModelPicker_Click(object sender, MouseButtonEventArgs e)
        {
            var store = Plugin?.ConfigStore;
            if (store == null) return;
            var menu = new ContextMenu();
            var current = store.GetCurrentProvider();

            foreach (var p in store.GetAllProviders())
            {
                if (string.IsNullOrWhiteSpace(p.Name)) continue;
                var providerItem = new MenuItem { Header = p.Name };
                if (p.Models == null || p.Models.Count == 0)
                {
                    var noModel = new MenuItem { Header = "（未设置模型）", IsEnabled = false };
                    providerItem.Items.Add(noModel);
                }
                else
                {
                    foreach (var m in p.Models)
                    {
                        var model = m; // capture
                        var isCurrent = current != null && string.Equals(current.Id, p.Id, StringComparison.Ordinal)
                            && string.Equals(current.Model, model, StringComparison.Ordinal);
                        var mi = new MenuItem { Header = (isCurrent ? "✓ " : "   ") + model };
                        mi.Click += (_, __) =>
                        {
                            store.SetCurrentProvider(p.Id);
                            p.Model = model;
                            try { store.SaveConfig(); } catch { }
                            UpdateModelLabel();
                        };
                        providerItem.Items.Add(mi);
                    }
                }
                menu.Items.Add(providerItem);
            }

            if (store.GetAllProviders().Count == 0)
            {
                var empty = new MenuItem { Header = "尚未配置 AI 服务，请先到设置页添加" };
                empty.IsEnabled = false;
                menu.Items.Add(empty);
            }

            menu.PlacementTarget = (FrameworkElement)sender;
            menu.IsOpen = true;
            // 关键：吞掉本次 MouseUp，否则它会被新打开的菜单当成「点外面」而立即关闭，
            // 导致要点好几下才能打开。
            e.Handled = true;
        }

        // ---------- 直接将最后一次 AI 回答插入画布 ----------
        private void BtnCanvas_Click(object sender, RoutedEventArgs e)
        {
            for (int i = Messages.Count - 1; i >= 0; i--)
            {
                if (Messages[i].IsAssistant && !string.IsNullOrWhiteSpace(Messages[i].Text))
                {
                    Plugin?.InsertTextToCanvas(Messages[i].Text);
                    return;
                }
            }
            Plugin?.NotifyInfo("还没有可插入的 AI 回答", Ink_Canvas.Plugins.NotificationLevel.Info);
        }

        // ---------- 气泡操作回调（由 ChatBubble 路由事件触发） ----------
        private void OnCopy(object sender, ChatBubbleRequestEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(e.Text)) Clipboard.SetText(e.Text);
                Plugin?.NotifyInfo(Strings.Get("Info_Copied"));
            }
            catch { }
        }

        private void OnInsertToCanvas(object sender, ChatBubbleRequestEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e.Text)) return;
            Plugin?.InsertTextToCanvas(e.Text);
        }

        private void OnRegenerate(object sender, ChatBubbleRequestEventArgs e)
        {
            for (int i = Messages.Count - 1; i >= 0; i--)
            {
                if (Messages[i].IsAssistant)
                {
                    Messages.RemoveAt(i);
                    Plugin?.RemoveLastAssistantFromHistory();
                    break;
                }
            }
            for (int i = Messages.Count - 1; i >= 0; i--)
            {
                if (Messages[i].IsUser)
                {
                    var lastUserText = Messages[i].Text;
                    Messages.RemoveAt(i);
                    Plugin?.RemoveLastUserFromHistory();
                    InputBox.Text = lastUserText;
                    DoSend();
                    return;
                }
            }
        }

        public void AppendUserExternal(string text) => AppendUserMessage(text);
    }
}