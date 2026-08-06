using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using AIChat.Views;
using Ink_Canvas.Plugins;

namespace AIChat.Views
{
    /// <summary>
    /// 悬浮快捷按钮（独立置顶小窗），外观参考宿主 QuickDrawFloatingButtonControl。
    /// 交互：拖动把手（左 22px）+ 单击图标（打开聊天窗）+ 右键菜单。
    /// 拖动手势移植自宿主控件；松手后吸附最近屏幕边缘。
    /// </summary>
    public partial class FloatingButtonWindow : Window
    {
        private const double DragThreshold = 3.0;

        // 无焦点悬浮：窗口不参与激活、点击不抢焦点（WS_EX_NOACTIVATE，与宿主液态玻璃栏同款做法）
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public AIChatPlugin Plugin { get; set; }

        private bool _isDragging;
        private bool _mouseDown;
        private Point _dragStartScreen;
        private Point _windowStart;

        // 真实显示用的屏幕工作区（由 Plugin 调用 SetScreenBounds 注入）
        private Rect _screenBounds = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        public Rect ScreenBounds
        {
            get => _screenBounds;
            set { _screenBounds = value; ApplyDockFromPosition(); }
        }

        public FloatingButtonWindow()
        {
            InitializeComponent();
            BuildContextMenu();
            // 注入主题
            try
            {
                var themeSvc = Plugin?.Services?.GetService(typeof(IThemeService)) as IThemeService;
                themeSvc?.ApplyThemeToElement(this);
            }
            catch { }
        }

        /// <summary>
        /// 悬浮窗无焦点：挂 WS_EX_NOACTIVATE，点击/显示都不激活、不抢键盘焦点，
        /// 不会打断正在进行的板书/演示。
        /// </summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE);
            }
            catch { }
        }

        private void BuildContextMenu()
        {
            var menu = new ContextMenu();
            var miOpen = new MenuItem { Header = Strings.Get("Floating_Menu_OpenChat") };
            miOpen.Click += (_, __) => Plugin?.ToggleChatWindow(show: true);
            var miSettings = new MenuItem { Header = Strings.Get("Floating_Menu_Settings") };
            miSettings.Click += (_, __) => Plugin?.OpenSettingsRequested();
            var miHide = new MenuItem { Header = Strings.Get("Floating_Menu_Hide") };
            miHide.Click += (_, __) => Hide();
            menu.Items.Add(miOpen);
            menu.Items.Add(miSettings);
            menu.Items.Add(new Separator());
            menu.Items.Add(miHide);
            ContextMenuRoot.Items.Clear();
            // 直接挂到 Border 上
            CardBorder.ContextMenu = menu;
        }

        // ----- 拖动（移植自 QuickDrawFloatingButtonControl，独立窗用 Left/Top） -----
        private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _mouseDown = true;
            _isDragging = false;
            _dragStartScreen = PointToScreen(e.GetPosition(this));
            _windowStart = new Point(Left, Top);
            ((UIElement)sender).CaptureMouse();
            e.Handled = true;
        }

        private void DragArea_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_mouseDown) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (!((UIElement)sender).IsMouseCaptured) return;

            var cur = PointToScreen(e.GetPosition(this));
            var dx = cur.X - _dragStartScreen.X;
            var dy = cur.Y - _dragStartScreen.Y;
            if (!_isDragging && (Math.Abs(dx) > DragThreshold || Math.Abs(dy) > DragThreshold))
            {
                _isDragging = true;
            }
            if (_isDragging)
            {
                Left = _windowStart.X + dx;
                Top = _windowStart.Y + dy;
            }
        }

        private void DragArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (((UIElement)sender).IsMouseCaptured)
                ((UIElement)sender).ReleaseMouseCapture();
            var wasDragging = _isDragging;
            _mouseDown = false;
            _isDragging = false;
            if (!wasDragging)
            {
                // 拖动把手不响应单击（单击交给图标区）
            }
            else
            {
                // 松手后吸附
                DockToNearestEdge();
                Plugin?.SaveConfigFromFloatingButton();
            }
            e.Handled = true;
        }

        // ----- 图标区：单击 -----
        private void ClickArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_mouseDown) return; // 拖动中不触发
            Plugin?.ToggleChatWindow(show: true);
            e.Handled = true;
        }

        /// <summary>
        /// 将窗口吸附到最近屏幕边缘。
        /// </summary>
        private void DockToNearestEdge()
        {
            var pos = new Point(Left + Width / 2, Top + Height / 2);
            var sb = _screenBounds;
            double distRight = sb.Right - pos.X;
            double distLeft = pos.X - sb.Left;
            double distBottom = sb.Bottom - pos.Y;
            double distTop = pos.Y - sb.Top;
            // 选择最小距离
            double min = Math.Min(Math.Min(distRight, distLeft), Math.Min(distBottom, distTop));
            const double margin = 0;
            if (min == distRight)
                Left = sb.Right - Width + margin;
            else if (min == distLeft)
                Left = sb.Left - margin;
            else if (min == distBottom)
                Top = sb.Bottom - Height + margin;
            else
                Top = sb.Top - margin;
            // 限制在工作区内
            if (Top < sb.Top) Top = sb.Top;
            if (Top > sb.Bottom - Height) Top = sb.Bottom - Height;
            if (Left < sb.Left) Left = sb.Left;
            if (Left > sb.Right - Width) Left = sb.Right - Width;
        }

        /// <summary>
        /// 根据当前 Left/Top 与 _screenBounds 判断最近边缘（用于持久化位置的重放）。
        /// </summary>
        private void ApplyDockFromPosition()
        {
            // 不主动移动，让位置由调用方控制；仅记录边界
        }

        /// <summary>
        /// 加载持久化位置（首次显示前由 Plugin 调用）。
        /// </summary>
        public void ApplyPosition(ButtonPositionState pos)
        {
            // 使用显式 Width/Height 而不是 ActualWidth/ActualHeight：
            // 此方法在 Show 之前调用，Window 尚未布局，ActualWidth=0 会导致位置跑到屏外。
            var sb = _screenBounds;
            if (sb.Width <= 0 || sb.Height <= 0)
            {
                sb = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
                _screenBounds = sb;
            }
            if (pos == null || double.IsNaN(pos.Left) || double.IsNaN(pos.Top))
            {
                // 默认贴右侧中部
                Left = sb.Right - Width - 8;
                Top = sb.Top + (sb.Height - Height) / 2;
                return;
            }
            Left = pos.Left;
            Top = pos.Top;
            // 夹紧到当前工作区（防止之前保存的坐标在多屏/DPI 改变后落到屏外）
            if (Left < sb.Left - Width) Left = sb.Right - Width - 8;
            if (Left > sb.Right) Left = sb.Right - Width - 8;
            if (Top < sb.Top) Top = sb.Top;
            if (Top > sb.Bottom - Height) Top = sb.Bottom - Height;
        }

        public ButtonPositionState CapturePosition()
        {
            return new ButtonPositionState { Left = Left, Top = Top, Edge = DockedEdge.None };
        }
    }
}