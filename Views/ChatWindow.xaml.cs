using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AIChat.ChatService;
using Ink_Canvas.Plugins;

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
        private ChatBubble _streamingBubble;
        private ChatBubbleVm _streamingVm;

        public ChatWindow()
        {
            InitializeComponent();
            MessagesList.ItemsSource = Messages;
            UpdateModelLabel();
            Messages.CollectionChanged += (_, __) => UpdateEmptyState();
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

        private void UpdateModelLabel()
        {
            ModelNameText.Text = Plugin?.ConfigStore?.Current?.Model ?? "未配置";
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) return;
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
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

        private void AddBubble(ChatBubbleVm vm)
        {
            Messages.Add(vm);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (MessagesList.ItemContainerGenerator.ContainerFromItem(vm) is ContentPresenter cp
                    && cp.ContentTemplate?.LoadContent() is ChatBubble bubble)
                {
                    bubble.CopyRequested += OnCopy;
                    bubble.InsertToCanvasRequested += OnInsertToCanvas;
                    bubble.RegenerateRequested += OnRegenerate;
                    bubble.SetBubble(vm);
                }
            }), DispatcherPriority.Background);
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
                _streamingVm = aiVm;
                _streamingBubble = GetLastBubble();
                _streamingBubble?.SetBubble(aiVm);
                ScrollToEnd();

                string finalText = "";
                var onDelta = new Action<string>(delta =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        aiVm.Text += delta;
                        _streamingBubble?.AppendText(delta);
                        MessagesScroll.ScrollToEnd();
                    });
                });

                try
                {
                    finalText = await client.ChatAsync(history, systemPrompt, onDelta, _cts.Token);
                }
                catch (ChatHttpException hex)
                {
                    AddAssistantError(string.Format(Strings.Get("Err_HttpStatus"), hex.StatusCode, hex.Message));
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    AddAssistantError(string.Format(Strings.Get("Err_Network"), ex.Message));
                    return;
                }

                aiVm.Text = finalText;
                aiVm.IsStreaming = false;
                Plugin?.AppendRuntimeHistory(aiVm);
                _streamingBubble?.FinishStreaming();
                ScrollToEnd();
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                _streamingBubble = null;
                _streamingVm = null;
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

        private ChatBubble GetLastBubble()
        {
            if (MessagesList.ItemContainerGenerator.ContainerFromIndex(Messages.Count - 1) is ContentPresenter cp)
            {
                cp.ApplyTemplate();
                return cp.ContentTemplate?.LoadContent() as ChatBubble;
            }
            return null;
        }

        private void ScrollToEnd()
        {
            Dispatcher.BeginInvoke(new Action(() => MessagesScroll.ScrollToEnd()), DispatcherPriority.Background);
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            try { _cts?.Cancel(); } catch { }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            if (Messages.Count == 0) return;
            var r = MessageBox.Show("确认清空当前对话？", "清空", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return;
            Messages.Clear();
            Plugin?.ClearRuntimeHistory();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        // ---------- 模型选择下拉（先选 provider，再列该 provider 的 models） ----------
        private void ModelPicker_Click(object sender, MouseButtonEventArgs e)
        {
            var cfg = Plugin?.ConfigStore?.Current;
            if (cfg == null) return;
            var menu = new ContextMenu();

            // 第一段：provider 子菜单（每个 provider 一个 submenu，列出其模型）
            foreach (var p in ProviderPresets.Presets)
            {
                var providerItem = new MenuItem { Header = p.Name };
                if (p.Models.Count == 0)
                {
                    var custom = new MenuItem { Header = "(自定义 URL 后填入模型)" };
                    custom.IsEnabled = false;
                    providerItem.Items.Add(custom);
                }
                else
                {
                    foreach (var m in p.Models)
                    {
                        var model = m; // capture
                        var mi = new MenuItem
                        {
                            Header = (model == cfg.Model ? "✓ " : "   ") + model,
                        };
                        mi.Click += (_, __) =>
                        {
                            cfg.ProviderKey = p.Key;
                            cfg.BaseUrl = p.BaseUrl;
                            cfg.Model = model;
                            cfg.Protocol = string.Equals(p.Type, "anthropic", StringComparison.OrdinalIgnoreCase)
                                ? ProtocolKind.Anthropic
                                : ProtocolKind.OpenAiCompatible;
                            Plugin?.ConfigStore?.SaveConfig();
                            UpdateModelLabel();
                        };
                        providerItem.Items.Add(mi);
                    }
                }
                menu.Items.Add(providerItem);
            }

            menu.Items.Add(new Separator());
            var itemOpenSettings = new MenuItem { Header = "打开 AI 设置…" };
            itemOpenSettings.Click += (_, __) => Plugin?.OpenSettingsRequested();
            menu.Items.Add(itemOpenSettings);
            menu.PlacementTarget = (FrameworkElement)sender;
            menu.IsOpen = true;
        }

        // ---------- 底部 + 按钮（清空） ----------
        private void BtnMore_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            var itemClear = new MenuItem { Header = Strings.Get("Chat_Btn_Clear") };
            itemClear.Click += (_, __) => BtnClear_Click(sender, e);
            menu.Items.Add(itemClear);
            menu.PlacementTarget = (FrameworkElement)sender;
            menu.IsOpen = true;
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

        // ---------- 气泡操作回调 ----------
        private void OnCopy(string text)
        {
            try
            {
                if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
                Plugin?.NotifyInfo(Strings.Get("Info_Copied"));
            }
            catch { }
        }

        private void OnInsertToCanvas(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            Plugin?.InsertTextToCanvas(text);
        }

        private void OnRegenerate()
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