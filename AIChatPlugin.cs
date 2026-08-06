using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIChat.ChatService;
using AIChat.Views;
using Ink_Canvas.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace AIChat
{
    /// <summary>
    /// AIChat 插件入口：管理悬浮按钮、聊天窗口、配置与多协议 AI 客户端，
    /// 全部能力基于现有 PluginSdk 服务（ICanvasElementService / IThemeService / IEventService /
    /// IScreenInfoService / INotificationService / IWindowService），
    /// 不修改宿主主程序与 SDK。
    /// </summary>
    [PluginEntrance]
    public class AIChatPlugin : PluginBase
    {
        public ConfigStore ConfigStore { get; private set; }
        public FloatingButtonWindow FloatingButton { get; private set; }
        public ChatWindow ChatWindow { get; private set; }
        public SettingsView SettingsView { get; private set; }

        private IThemeService _themeSvc;
        private IEventService _eventSvc;
        private IWindowService _windowSvc;
        private IScreenInfoService _screenSvc;
        private ICanvasElementService _canvasSvc;
        private INotificationService _notifySvc;

        /// <summary>
        /// 暴露 Services 以便悬浮按钮等子模块能获取宿主服务（与 PluginBase 的 Host 解耦）。
        /// </summary>
        public IServiceProvider Services => Host?.ServiceProvider;

        public override string Id => "com.icc.ai-chat";
        public override string Name => "AI 助手";
        public override string Version => "1.0.0";
        public override string Author => "ICC-CE";
        public override string Description => "独立悬浮 AI 聊天助手，支持 OpenAI 兼容与 Claude 原生协议，回答可一键插入画布。";

        public override void Initialize(IPluginHost host, IServiceCollection services)
        {
            base.Initialize(host, services);
            Log($"{Name} v{Version} 正在初始化...");

            // 解析宿主服务
            _themeSvc = host.GetService<IThemeService>();
            _eventSvc = host.GetService<IEventService>();
            _windowSvc = host.GetService<IWindowService>();
            _screenSvc = host.GetService<IScreenInfoService>();
            _canvasSvc = host.GetService<ICanvasElementService>();
            _notifySvc = host.GetService<INotificationService>();

            // 配置目录与加载
            ConfigStore = new ConfigStore(PluginConfigFolder);
            ConfigStore.Load();
            ConfigStore.LoadHistory();

            // 屏幕工作区：取主显示器（多显示器时悬浮按钮仅作用于主屏）
            if (_screenSvc != null)
            {
                var primary = _screenSvc.GetPrimaryScreen();
                var bounds = primary.WorkingArea;
                if (bounds.Width > 0 && bounds.Height > 0)
                {
                    FloatingButtonScreen = bounds;
                }
            }
            if (FloatingButtonScreen.Width <= 0)
            {
                FloatingButtonScreen = new Rect(0, 0,
                    SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            }

            // 解析宿主主窗口（用来把悬浮按钮贴在宿主窗口右上角内部，跟随宿主移动/最小化）
            // ——这是独立置顶窗口宿主绘制时容易被宿主主窗口盖住的问题的稳妥做法。
            try { FloatingButtonHostWindow = Application.Current?.MainWindow as Window; }
            catch { FloatingButtonHostWindow = null; }

            // 创建窗口（先 ChatWindow，再悬浮按钮）
            ChatWindow = new ChatWindow
            {
                Plugin = this,
                ShowInTaskbar = true,
                Topmost = _windowSvc?.IsTopMost ?? false
            };
            ChatWindow.LoadHistory(ConfigStore.Current.History.Messages.Select(PersistedMessageExtensions.ToRuntime));
            ChatWindow.Hide();

            FloatingButton = new FloatingButtonWindow
            {
                Plugin = this,
                ScreenBounds = FloatingButtonScreen
            };
            // 无论持久化位置如何，每次启动都重置到宿主主窗口右上角内部——
            // 因为独立 Topmost 透明窗口在多屏/DPI 切换后很容易跑到屏外或被宿主盖住，
            // 用户找不到的体验比"位置不记忆"更糟。后续可改成"按 Shift 启动时复位、否则恢复"。
            FloatingButton.ApplyPosition(ConfigStore.Current.ButtonPosition);
            // 总是尝试复位（无论首启动与否）。若宿主窗口还没 Loaded 则在 ContentRendered 后再试。
            if (FloatingButtonHostWindow != null && FloatingButtonHostWindow.IsLoaded)
            {
                ResetFloatingToHostWindow();
            }
            else if (FloatingButtonHostWindow != null)
            {
                FloatingButtonHostWindow.ContentRendered += (_, __) => ResetFloatingToHostWindow();
            }
            else
            {
                // 拿不到宿主窗口时，在 Loaded 后用主屏工作区摆位（兜底）
                FloatingButton.Loaded += (_, __) => ResetFloatingToHostWindow();
            }
            FloatingButton.Loaded += (_, __) =>
            {
                Log($"悬浮按钮 Loaded: Width={FloatingButton.ActualWidth} Height={FloatingButton.ActualHeight} " +
                    $"Left={FloatingButton.Left} Top={FloatingButton.Top} " +
                    $"IsVisible={FloatingButton.IsVisible} V={FloatingButton.Visibility} " +
                    $"ScreenBounds=({FloatingButtonScreen.X},{FloatingButtonScreen.Y},{FloatingButtonScreen.Width},{FloatingButtonScreen.Height})");
            };
            try
            {
                FloatingButton.Show();
                Log($"悬浮按钮 Show 完成: IsVisible={FloatingButton.IsVisible} V={FloatingButton.Visibility}");
            }
            catch (Exception ex)
            {
                LogError("悬浮按钮 Show 失败: " + ex.Message, ex);
            }

            // 订阅宿主事件
            if (_eventSvc != null)
            {
                _eventSvc.AppExiting += OnAppExiting;
                _eventSvc.TopMostChanged += OnTopMostChanged;
                _eventSvc.WhiteboardModeChanged += OnWhiteboardModeChanged;
            }

            Log("AIChat 插件已初始化完成。");
        }

        public override void Shutdown()
        {
            try
            {
                if (_eventSvc != null)
                {
                    _eventSvc.AppExiting -= OnAppExiting;
                    _eventSvc.TopMostChanged -= OnTopMostChanged;
                    _eventSvc.WhiteboardModeChanged -= OnWhiteboardModeChanged;
                }
                SaveConfig();
                try { ChatWindow?.Close(); } catch { }
                try { FloatingButton?.Close(); } catch { }
            }
            catch (Exception ex)
            {
                LogError("Shutdown failed: " + ex.Message, ex);
            }
            base.Shutdown();
        }

        public override object GetMainView() => null;
        public override object GetSettingsView()
        {
            if (SettingsView == null)
            {
                SettingsView = new SettingsView
                {
                    Config = ConfigStore,
                    Notify = NotifyInfo,
                    TestConnectionAsync = TestConnectionAsync
                };
            }
            return SettingsView;
        }

        // ---------- 公共 API（被窗口调用）----------
        public Rect FloatingButtonScreen { get; private set; }

        /// <summary>
        /// 切换聊天窗口显隐。
        /// </summary>
        public void ToggleChatWindow(bool show)
        {
            if (ChatWindow == null) return;
            if (show)
            {
                if (!ChatWindow.IsVisible)
                {
                    // 恢复历史
                    ChatWindow.LoadHistory(ConfigStore.Current.History.Messages.Select(m => m.ToRuntime()));
                }
                ChatWindow.Show();
                ChatWindow.Activate();
                ChatWindow.Topmost = _windowSvc?.IsTopMost ?? false;
            }
            else
            {
                ChatWindow.Hide();
            }
        }

        /// <summary>
        /// 由设置页请求打开。
        /// </summary>
        public void OpenSettingsRequested()
        {
            // 让宿主设置页打开插件配置（通过 GetSettingsView() 返回 SettingsView）。
            // 主程序会自动包含此视图到插件设置页面
            // 这里只是标记有请求；具体展示由宿主决定
        }

        /// <summary>
        /// 保存配置（由悬浮按钮拖动后调用）。
        /// </summary>
        public void SaveConfigFromFloatingButton()
        {
            try
            {
                ConfigStore.Current.ButtonPosition = FloatingButton.CapturePosition();
                ConfigStore.SaveConfig();
            }
            catch (Exception ex) { LogError("Save floating position: " + ex.Message, ex); }
        }

        /// <summary>
        /// 强制重置/显示悬浮按钮（用于调试或在屏幕上找不到时恢复）。
        /// </summary>
        public void ShowOrResetFloatingButton()
        {
            try
            {
                if (FloatingButton == null) return;
                ResetFloatingToHostWindow();
                SaveConfigFromFloatingButton();
            }
            catch (Exception ex) { LogError("ShowOrResetFloatingButton: " + ex.Message, ex); }
        }

        /// <summary>
        /// 宿主主窗口（用于把悬浮按钮贴在宿主窗口右上角内部，跟随宿主移动/最小化）。
        /// </summary>
        public Window FloatingButtonHostWindow { get; private set; }

        /// <summary>
        /// 把悬浮按钮复位到宿主主窗口右上角内部（不被宿主主窗口盖住，跟随宿主窗口）。
        /// 如果拿不到宿主主窗口，则回退到主屏右边缘中部。
        /// </summary>
        public void ResetFloatingToHostWindow()
        {
            if (FloatingButton == null) return;
            try
            {
                if (FloatingButtonHostWindow != null
                    && FloatingButtonHostWindow.WindowState != WindowState.Minimized
                    && FloatingButtonHostWindow.ActualWidth > 0
                    && FloatingButtonHostWindow.ActualHeight > 0)
                {
                    // 用宿主窗口的屏幕坐标，把悬浮按钮贴在右上角内部
                    var p = FloatingButtonHostWindow.PointToScreen(new Point(0, 0));
                    FloatingButton.Left = p.X + FloatingButtonHostWindow.ActualWidth - FloatingButton.Width - 12;
                    FloatingButton.Top = p.Y + 80;
                }
                else
                {
                    var sb = FloatingButtonScreen;
                    if (sb.Width <= 0)
                    {
                        sb = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
                        FloatingButtonScreen = sb;
                    }
                    FloatingButton.Left = sb.Right - FloatingButton.Width - 8;
                    FloatingButton.Top = sb.Top + (sb.Height - FloatingButton.Height) / 2;
                }
                if (!FloatingButton.IsVisible) FloatingButton.Show();
                FloatingButton.Activate();
                Log($"ResetFloatingToHostWindow: L={FloatingButton.Left} T={FloatingButton.Top} W={FloatingButton.Width} H={FloatingButton.Height}");
            }
            catch (Exception ex) { LogError("ResetFloatingToHostWindow: " + ex.Message, ex); }
        }

        public string GetSystemPrompt() => ConfigStore?.Current?.SystemPrompt ?? "";

        /// <summary>
        /// 创建与当前协议匹配的聊天客户端。
        /// </summary>
        public IChatClient CreateClient()
        {
            var cfg = ConfigStore.Current;
            var key = ConfigStore.GetApiKeyPlain();
            if (string.IsNullOrEmpty(key)) return null;
            if (cfg.Protocol == ProtocolKind.Anthropic)
                return new AnthropicChatClient(cfg.BaseUrl, key, cfg.Model, cfg.MaxTokens);
            return new OpenAiChatClient(cfg.BaseUrl, key, cfg.Model, cfg.Temperature, cfg.MaxTokens);
        }

        /// <summary>
        /// 把一条消息写入运行时历史（内存 + 落盘）。
        /// </summary>
        public void AppendRuntimeHistory(ChatBubbleVm vm)
        {
            if (ConfigStore == null) return;
            ConfigStore.Current.History.Messages.Add(PersistedMessage.From(new ChatMessage(vm.Role, vm.Text)));
            ConfigStore.SaveHistory();
        }

        public void RemoveLastAssistantFromHistory()
        {
            var list = ConfigStore?.Current?.History?.Messages;
            if (list == null) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Role == "assistant") { list.RemoveAt(i); break; }
            }
            ConfigStore.SaveHistory();
        }

        public void RemoveLastUserFromHistory()
        {
            var list = ConfigStore?.Current?.History?.Messages;
            if (list == null) return;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Role == "user") { list.RemoveAt(i); break; }
            }
            ConfigStore.SaveHistory();
        }

        public void ClearRuntimeHistory()
        {
            if (ConfigStore == null) return;
            ConfigStore.Current.History.Messages.Clear();
            ConfigStore.SaveHistory();
        }

        /// <summary>
        /// 把 AI 回答文本作为可拖动文本元素插入画布。
        /// </summary>
        public void InsertTextToCanvas(string text)
        {
            if (_canvasSvc == null || string.IsNullOrWhiteSpace(text)) return;
            try
            {
                // 构造一个最大宽度 ~600 的 Border + TextBlock
                var border = new Border
                {
                    Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(220, 255, 255, 224)),
                    BorderBrush = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6, 10, 6),
                    MaxWidth = 600,
                    MinWidth = 120
                };
                var tb = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 16,
                    Foreground = System.Windows.Media.Brushes.Black
                };
                border.Child = tb;
                if (_canvasSvc.InsertElement(border))
                    NotifyInfo(Strings.Get("Info_Inserted"));
                else
                    NotifyInfo(Strings.Get("Info_InsertFailed"), Ink_Canvas.Plugins.NotificationLevel.Warning);
            }
            catch (Exception ex)
            {
                LogError("InsertTextToCanvas failed: " + ex.Message, ex);
                NotifyInfo(Strings.Get("Info_InsertFailed"), Ink_Canvas.Plugins.NotificationLevel.Error);
            }
        }

        public void NotifyInfo(string msg, Ink_Canvas.Plugins.NotificationLevel level = Ink_Canvas.Plugins.NotificationLevel.Info)
        {
            try { _notifySvc?.Show(Name, msg, level); }
            catch { }
        }

        /// <summary>
        /// 设置页测试连接：发一条最短请求验证。
        /// </summary>
        private async System.Threading.Tasks.Task<bool> TestConnectionAsync(string apiKey, ProtocolKind protocol, string baseUrl)
        {
            if (string.IsNullOrEmpty(apiKey)) return false;
            try
            {
                var cfg = ConfigStore.Current;
                IChatClient client = protocol == ProtocolKind.Anthropic
                    ? new AnthropicChatClient(baseUrl, apiKey, cfg.Model, 32)
                    : new OpenAiChatClient(baseUrl, apiKey, cfg.Model, 0, 32);
                var hist = new List<ChatMessage> { new ChatMessage("user", "hi") };
                var full = await client.ChatAsync(hist, "You are a tester. Reply with 'ok'.", _ => { },
                    System.Threading.CancellationToken.None);
                return !string.IsNullOrWhiteSpace(full);
            }
            catch { return false; }
        }

        // ---------- 内部 ----------
        private void SaveConfig()
        {
            try
            {
                if (FloatingButton != null)
                    ConfigStore.Current.ButtonPosition = FloatingButton.CapturePosition();
                ConfigStore.SaveConfig();
                ConfigStore.SaveHistory();
            }
            catch (Exception ex) { LogError("Save config: " + ex.Message, ex); }
        }

        private void OnAppExiting()
        {
            try
            {
                SaveConfig();
                ChatWindow?.Close();
                FloatingButton?.Close();
            }
            catch { }
        }

        private void OnTopMostChanged(bool topMost)
        {
            // 聊天窗跟随宿主置顶
            if (ChatWindow != null && ChatWindow.IsVisible)
            {
                ChatWindow.Dispatcher.BeginInvoke(new Action(() => ChatWindow.Topmost = topMost));
            }
        }

        private void OnWhiteboardModeChanged(bool inWhiteboard)
        {
            // 白板模式下保持悬浮按钮常驻；聊天窗可独立显隐
        }
    }

    /// <summary>
    /// PersistedMessage 与 ChatMessage 之间的转换扩展，避免在 Models.cs 中引用窗口代码。
    /// </summary>
    internal static class PersistedMessageExtensions
    {
        public static ChatMessage ToRuntime(this PersistedMessage m) => new ChatMessage(m.Role, m.Text);
    }
}