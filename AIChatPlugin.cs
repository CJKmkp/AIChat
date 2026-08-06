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
                    TestConnectionAsync = TestConnectionAsync,
                    ListModelsAsync = ListModelsAsync
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
                // 每次显示都刷新模型标签（配置可能在设置页被修改过）
                ChatWindow.UpdateModelLabel();
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
        /// 把悬浮按钮复位到默认停放位置：贴主屏工作区右边缘、垂直居中。
        /// 高度 200 与宿主「快抽」窗口（QuickDrawWindow, 200）相近，贴右边停放可避开
        /// 右下角快抽悬浮按钮与居中弹出的快抽窗口，互不遮挡。
        /// 注意：用 WPF 原生 SystemParameters.WorkArea（DIP，与 Window.Left/Top 同一坐标系）定位；
        /// 不要用宿主注入的 FloatingButtonScreen——它来自 WinForms 物理像素坐标，DPI≠100% 时
        /// 会把窗口推出屏外导致按钮“消失”。
        /// </summary>
        public void ResetFloatingToHostWindow()
        {
            if (FloatingButton == null) return;
            try
            {
                var wa = SystemParameters.WorkArea;
                var w = double.IsNaN(FloatingButton.Width) ? 65 : FloatingButton.Width;
                var h = double.IsNaN(FloatingButton.Height) ? 200 : FloatingButton.Height;
                FloatingButton.Left = wa.Right - w - 8;
                FloatingButton.Top = wa.Top + (wa.Height - h) / 2;
                if (!FloatingButton.IsVisible) FloatingButton.Show();
                FloatingButton.Activate();
                Log($"ResetFloatingToHostWindow: L={FloatingButton.Left} T={FloatingButton.Top} W={w} H={h} WorkArea=({wa.X},{wa.Y},{wa.Width},{wa.Height})");
            }
            catch (Exception ex) { LogError("ResetFloatingToHostWindow: " + ex.Message, ex); }
        }

        public string GetSystemPrompt() => ConfigStore?.Current?.SystemPrompt ?? "";

        /// <summary>
        /// 创建与当前 provider 匹配的聊天客户端。返回 null 表示未配置 API Key（调用方须提示）。
        /// </summary>
        public IChatClient CreateClient()
        {
            var p = ConfigStore.GetCurrentProvider();
            if (p == null)
            {
                LogError("CreateClient 失败：没有已配置的提供商");
                return null;
            }
            var key = ConfigStore.GetApiKeyPlain();
            Log($"CreateClient: name={p.Name} type={p.Type} baseUrl={p.BaseUrl} " +
                $"model={p.Model} keyLen={key?.Length ?? 0} maxTokens={ConfigStore.Current.MaxTokens} temp={ConfigStore.Current.Temperature}");
            if (string.IsNullOrEmpty(key))
            {
                LogError("CreateClient 失败：API Key 为空（请在设置页填写并保存）");
                return null;
            }
            if (string.IsNullOrWhiteSpace(p.BaseUrl))
            {
                LogError("CreateClient 失败：BaseUrl 为空");
                return null;
            }
            if (string.IsNullOrWhiteSpace(p.Model))
            {
                LogError("CreateClient 失败：Model 为空");
                return null;
            }
            if (p.IsAnthropic)
                return new AnthropicChatClient(p.BaseUrl, key, p.Model, ConfigStore.Current.MaxTokens);
            return new OpenAiChatClient(p.BaseUrl, key, p.Model, ConfigStore.Current.Temperature, ConfigStore.Current.MaxTokens);
        }

        /// <summary>
        /// 拉取当前 provider 的可用模型列表（参考 cc-switch：不依赖当前 model，只需 baseUrl + key）。
        /// 失败抛异常由调用方展示。
        /// </summary>
        public async System.Threading.Tasks.Task<List<string>> ListModelsAsync()
        {
            var p = ConfigStore.GetCurrentProvider();
            if (p == null)
            {
                LogError("ListModelsAsync 失败：没有已配置的提供商");
                throw new InvalidOperationException("没有已配置的提供商");
            }
            var key = ConfigStore.GetApiKeyPlain();
            Log($"ListModelsAsync: name={p.Name} type={p.Type} baseUrl={p.BaseUrl}");
            if (string.IsNullOrEmpty(key))
            {
                LogError("ListModelsAsync 失败：API Key 为空");
                throw new InvalidOperationException(Strings.Get("Err_NoApiKey"));
            }
            if (string.IsNullOrWhiteSpace(p.BaseUrl))
            {
                LogError("ListModelsAsync 失败：Base URL 为空");
                throw new InvalidOperationException("接口地址为空");
            }
            try
            {
                // 拉取模型列表不依赖 model，直接按协议构造客户端
                IChatClient client = p.IsAnthropic
                    ? new AnthropicChatClient(p.BaseUrl, key, p.Model, ConfigStore.Current.MaxTokens)
                    : new OpenAiChatClient(p.BaseUrl, key, p.Model, ConfigStore.Current.Temperature, ConfigStore.Current.MaxTokens);
                var models = await client.ListModelsAsync(System.Threading.CancellationToken.None).ConfigureAwait(false);
                Log($"ListModelsAsync 成功：{models?.Count ?? 0} 个模型");
                return models;
            }
            catch (ChatHttpException hex)
            {
                LogError($"ListModelsAsync HTTP {hex.StatusCode}: {hex.Body}", hex);
                throw;
            }
            catch (Exception ex)
            {
                LogError("ListModelsAsync 失败: " + ex.Message, ex);
                throw;
            }
        }

        /// <summary>
        /// 记录一次聊天请求失败（供聊天窗调用，确保错误进入插件日志）。
        /// </summary>
        public void LogChatFailure(string context, Exception ex)
        {
            if (ex is ChatHttpException hex)
                LogError($"{context}: HTTP {hex.StatusCode} — {hex.Body}", hex);
            else
                LogError($"{context}: {ex?.GetType().Name} — {ex?.Message}", ex);
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
        /// <para>
        /// 盒子固定宽度 + 高度上限，文本放 ScrollViewer 内滚动显示：
        /// 宿主 CenterAndScaleElement 按画布 70% 缩放并强制 Width/Height 时，
        /// 长内容在框内滚动而非被截断（纯 Border+TextBlock 会在缩放后底部溢出被切掉）。
        /// </para>
        /// </summary>
        public void InsertTextToCanvas(string text)
        {
            if (_canvasSvc == null || string.IsNullOrWhiteSpace(text)) return;
            try
            {
                var tb = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 16,
                    Foreground = System.Windows.Media.Brushes.Black
                };
                // 高度上限 320：短文本按内容自适应（ScrollViewer 收缩到内容高），
                // 长文本固定在 320 高并出现纵向滚动条——任何长度都不截断。
                var scroll = new ScrollViewer
                {
                    Content = tb,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    MaxHeight = 320
                };
                var border = new Border
                {
                    Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(220, 255, 255, 224)),
                    BorderBrush = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6, 10, 6),
                    // 固定宽度保证 InkCanvas 无限测量下 TextBlock 稳定换行
                    Width = 480,
                    MinWidth = 120
                };
                border.Child = scroll;
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
        /// 设置页测试连接：模拟发送一条最短消息（"你是什么模型"），
        /// 成功/失败都写日志，失败时把原因返回给 UI。
        /// 入参为当前编辑的 provider（表单先写回对象再调用）。
        /// </summary>
        private async System.Threading.Tasks.Task<(bool Ok, string Message)> TestConnectionAsync(ProviderConfig provider)
        {
            if (provider == null)
            {
                LogError("测试连接失败：未选择提供商");
                return (false, "未选择提供商");
            }
            var apiKey = "";
            if (!string.IsNullOrEmpty(provider.ApiKeyCipher))
            {
                try { apiKey = SecretStore.TryUnprotect(Convert.FromBase64String(provider.ApiKeyCipher)); }
                catch { }
            }
            if (string.IsNullOrEmpty(apiKey))
            {
                LogError("测试连接失败：API Key 为空");
                return (false, "API Key 为空，请在设置中填写");
            }
            if (string.IsNullOrWhiteSpace(provider.BaseUrl))
            {
                LogError("测试连接失败：Base URL 为空");
                return (false, "接口地址为空");
            }
            if (string.IsNullOrWhiteSpace(provider.Model))
            {
                LogError("测试连接失败：Model 为空");
                return (false, "模型名为空，请填写或拉取");
            }
            Log($"测试连接开始: name={provider.Name} type={provider.Type} baseUrl={provider.BaseUrl} model={provider.Model}");
            try
            {
                IChatClient client = provider.IsAnthropic
                    ? new AnthropicChatClient(provider.BaseUrl, apiKey, provider.Model, 32)
                    : new OpenAiChatClient(provider.BaseUrl, apiKey, provider.Model, 0, 32);
                var hist = new List<ChatMessage> { new ChatMessage("user", "你是什么模型") };
                var full = await client.ChatAsync(hist, "直接回答你是什么模型，一句话即可。", _ => { },
                    System.Threading.CancellationToken.None);
                bool ok = !string.IsNullOrWhiteSpace(full);
                if (ok)
                {
                    Log($"测试连接成功：收到回复 {full.Length} 字符（{Truncate(full, 80)}）");
                    // 只返回"测试成功"，不把完整回复塞进设置页
                    return (true, Strings.Get("Info_TestOk"));
                }
                // 空回复：发一次原始请求，把响应头 + body 打日志，定位是格式问题还是空响应
                Log("测试连接：收到空回复，发原始诊断请求…");
                var diag = await RawDiagnosticAsync(provider.BaseUrl, apiKey, provider.Model,
                    provider.IsAnthropic ? ProtocolKind.Anthropic : ProtocolKind.OpenAiCompatible).ConfigureAwait(false);
                Log("测试连接空回复诊断: " + diag);
                return (true, $"{Strings.Get("Info_TestOk")}（空回复）");
            }
            catch (ChatHttpException hex)
            {
                LogError($"测试连接失败：HTTP {hex.StatusCode} — {hex.Body}", hex);
                return (false, $"HTTP {hex.StatusCode}：{Truncate(ExtractErrorBody(hex.Body), 120)}");
            }
            catch (OperationCanceledException)
            {
                LogError("测试连接失败：请求超时");
                return (false, "请求超时");
            }
            catch (Exception ex)
            {
                LogError($"测试连接失败：{ex.GetType().Name} — {ex.Message}", ex);
                return (false, ex.Message);
            }
        }

        /// <summary>从 API 错误 JSON 提取人类可读的 message 字段。</summary>
        private static string ExtractErrorBody(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return body ?? "";
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("error", out var err) && err.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (err.TryGetProperty("message", out var msg) && msg.ValueKind == System.Text.Json.JsonValueKind.String)
                        return msg.GetString() ?? body;
                }
                return body;
            }
            catch
            {
                return body;
            }
        }

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");

        /// <summary>
        /// 原始诊断请求：直接发 HTTP，返回「响应头 + 响应体前 500 字符」，
        /// 用于定位"空回复"（协议/格式/字段名不匹配）问题。
        /// </summary>
        private static async System.Threading.Tasks.Task<string> RawDiagnosticAsync(
            string baseUrl, string apiKey, string model, ProtocolKind protocol)
        {
            try
            {
                var url = protocol == ProtocolKind.Anthropic
                    ? baseUrl.TrimEnd('/') + "/v1/messages"
                    : baseUrl.TrimEnd('/') + "/chat/completions";
                var bodyObj = protocol == ProtocolKind.Anthropic
                    ? (object)new { model = model, max_tokens = 32, stream = true,
                        messages = new[] { new { role = "user", content = "你是什么模型" } } }
                    : (object)new { model = model, stream = true,
                        messages = new[] { new { role = "user", content = "你是什么模型" } } };
                using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, url)
                {
                    Content = new System.Net.Http.StringContent(
                        System.Text.Json.JsonSerializer.Serialize(bodyObj),
                        System.Text.Encoding.UTF8, "application/json")
                };
                if (protocol == ProtocolKind.Anthropic)
                {
                    req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                    req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                }
                else
                {
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                }
                using var resp = await ChatService.HttpClientHolder.Shared.SendAsync(
                    req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                var headers = resp.Content.Headers.ContentType?.MediaType ?? "?";
                var status = (int)resp.StatusCode;
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                return $"status={status} contentType={headers} body={Truncate(body, 500)}";
            }
            catch (Exception ex)
            {
                return "诊断失败: " + ex.Message;
            }
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