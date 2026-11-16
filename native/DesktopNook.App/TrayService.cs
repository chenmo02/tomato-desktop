using System.Diagnostics;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Wpf = System.Windows.Controls;

namespace DesktopNook.App;

// 通知区（托盘）图标：点 × 后窗口隐藏、进程继续运行，托盘是唯一的可见入口。
// WinForms 只负责 NotifyIcon 本身；右键菜单交给 TrayMenu 用 WPF 绘制，
// 这样菜单外观与主窗口一致，也能跟随主题与皮肤切换。
internal sealed class TrayService : IDisposable
{
    private const string AppTitle = "番茄桌面";
    private readonly MainWindow _window;
    private readonly Forms.NotifyIcon _icon;
    private readonly Drawing.Icon? _appIcon;
    private Wpf.ContextMenu? _menu;
    private DateTime _lastMenuOpen;
    private bool _disposed;
    private bool _tipShown;

    // 全局低级鼠标钩子只在菜单打开期间安装。主窗口隐藏到托盘后，WPF Popup 的
    // StaysOpen=false 拿不到「点到别处」的失焦通知，必须由钩子自己判断点击是否落在
    // 菜单 HWND 之外并主动关闭。委托保存在静态字段里，防止被 GC 回收后回调失效。
    private const int WhMouseLl = 14;
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private static readonly LowLevelMouseProc HookProc = OnLowLevelMouse;
    private static IntPtr _hook = IntPtr.Zero;
    private static TrayService? _hookOwner;

    [StructLayout(LayoutKind.Sequential)]
    private struct HookPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData { public HookPoint Point; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);

    internal bool Visible => !_disposed && _icon.Visible;
    // 自检用：确认取到的是程序内嵌的番茄图标，而不是资源缺失时的系统默认图标。
    internal bool UsesAppIcon => _appIcon is not null;
    internal string Text => _disposed ? "" : _icon.Text ?? "";
    internal bool IsBackground { get; private set; }
    internal IReadOnlyList<string> MenuLabels => TrayMenu.Labels;
    // 自检用：确认系统默认的 WinForms 菜单已彻底移除，当前菜单由 WPF 承载。
    internal bool UsesNativeStrip => _icon.ContextMenuStrip is not null;
    internal Wpf.ContextMenu? Menu => _menu;
    // 自检用：确认钩子确实装上了，且只在菜单打开期间存在。
    internal static bool HookInstalled => _hook != IntPtr.Zero;

    internal TrayService(MainWindow window)
    {
        _window = window;
        _appIcon = LoadAppIcon();
        _icon = new Forms.NotifyIcon
        {
            Icon = _appIcon ?? Drawing.SystemIcons.Application,
            Text = AppTitle + " · 点击显示",
            Visible = true
        };
        // MouseClick 与 MouseUp 都会带右键，去重保证同一次点击只弹一次菜单；
        // 不挂 ContextMenuStrip，否则 WinForms 会抢先画出系统默认菜单。
        _icon.MouseClick += OnMouseClick;
        _icon.MouseUp += OnMouseUp;
        _icon.DoubleClick += (_, _) => _window.ShowPanel();
    }

    // 图标取自程序内嵌的多尺寸 ICO；资源异常时退回系统默认图标，保证托盘入口始终存在。
    private static Drawing.Icon? LoadAppIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/AppIcon.ico", UriKind.Absolute));
            if (resource is null) return null;
            using var stream = resource.Stream;
            return new Drawing.Icon(stream);
        }
        catch { return null; }
    }

    private void OnMouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left) _window.ShowPanel();
        else if (e.Button == Forms.MouseButtons.Right) OpenMenu();
    }

    private void OnMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        // 某些 Shell 组合下 NotifyIcon 只投递 MouseUp；与 MouseClick 的重复由 OpenMenu 去重。
        if (e.Button == Forms.MouseButtons.Right) OpenMenu();
    }

    // 右键打开 WPF 菜单。菜单每次重建，主题切换后无需额外刷新；点击外部由
    // 全局鼠标钩子负责，Esc / 键盘关闭仍交给 Popup 自己。
    internal Wpf.ContextMenu? OpenMenu()
    {
        if (_disposed) return null;
        var now = DateTime.UtcNow;
        if (_menu?.IsOpen == true && now - _lastMenuOpen < TimeSpan.FromMilliseconds(350)) return _menu;
        CloseMenu();
        try
        {
            var menu = TrayMenu.Open(_window, IsBackground);
            _lastMenuOpen = now;
            menu.Closed += (_, _) => { if (ReferenceEquals(_menu, menu)) _menu = null; ReleaseHook(); };
            _menu = menu;
            InstallHook();
            return menu;
        }
        catch { return null; } // 无 Shell / 远程会话下 Popup 创建失败时托盘图标仍可左键唤起
    }

    internal void CloseMenu()
    {
        ReleaseHook();
        var menu = _menu;
        _menu = null;
        if (menu is null) return;
        try { menu.IsOpen = false; }
        catch { /* 退出过程中窗口已释放 */ }
    }

    private void InstallHook()
    {
        if (_hook != IntPtr.Zero) { _hookOwner = this; return; }
        try
        {
            string? moduleName = null;
            try { using var process = Process.GetCurrentProcess(); moduleName = process.MainModule?.ModuleName; }
            catch { /* 取不到模块名时退回 NULL 句柄 */ }
            var module = moduleName is null ? IntPtr.Zero : GetModuleHandle(moduleName);
            var hook = SetWindowsHookEx(WhMouseLl, HookProc, module, 0);
            if (hook == IntPtr.Zero) return;
            _hook = hook;
            _hookOwner = this;
        }
        catch { /* 钩子不可用时仍有 Esc 与菜单项自身的关闭行为 */ }
    }

    private static void ReleaseHook()
    {
        if (_hook == IntPtr.Zero) return;
        var hook = _hook;
        _hook = IntPtr.Zero;
        _hookOwner = null;
        try { UnhookWindowsHookEx(hook); }
        catch { /* 进程退出时系统会自动回收钩子 */ }
    }

    // 低级钩子回调运行在 UI 线程，同步关闭即可：右键按下时先关掉旧菜单，
    // 随后的 MouseUp 才会打开新菜单，不会出现「关掉刚打开的菜单」的竞态。
    private static IntPtr OnLowLevelMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var hook = _hook; // 回调里可能卸载钩子，先缓存句柄再传给 CallNextHookEx
        if (nCode >= 0 && _hookOwner is { } owner && IsButtonDown(wParam))
        {
            try
            {
                var data = Marshal.PtrToStructure<MouseHookData>(lParam);
                owner.HandleOutsideClick(data.Point.X, data.Point.Y);
            }
            catch { /* 钩子回调绝不能抛异常，否则会被系统摘掉 */ }
        }
        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    private static bool IsButtonDown(IntPtr wParam) => wParam.ToInt32() switch
    {
        0x0201 or 0x0204 or 0x0207 or 0x020B or 0x00A1 or 0x00A4 or 0x00A7 => true,
        _ => false
    };

    // 点击落在菜单矩形外就立即收起；菜单内（含分隔线与留白）不处理，交给菜单自己响应。
    internal void HandleOutsideClick(int x, int y)
    {
        var menu = _menu;
        if (menu is null || !menu.IsOpen) return;
        if (TrayMenu.ContainsScreenPoint(menu, x, y)) return;
        CloseMenu();
    }

    // 自检用：取一个明确位于菜单矩形外的屏幕点，走与真实钩子完全相同的关闭路径。
    internal bool TryOutsideTestPoint(out int x, out int y)
    {
        x = y = 0;
        if (_menu is null || !TrayMenu.TryGetMenuRect(_menu, out var bounds)) return false;
        x = bounds.Left > 60 ? (int)bounds.Left - 40 : (int)bounds.Right + 40;
        y = (int)bounds.Top + 20;
        return true;
    }

    // 窗口隐藏/恢复时同步提示文案，用户在通知区也能看出程序是否处于后台驻留状态。
    internal void SetBackgroundState(bool background)
    {
        if (_disposed) return;
        IsBackground = background;
        _icon.Text = background ? AppTitle + " · 后台运行（点击显示）" : AppTitle + " · 点击显示";
    }

    // 首次收进托盘时提示一次，避免用户以为程序「关掉了就找不回来」。沙盒自检不弹系统气泡。
    internal void NotifyBackgroundTip()
    {
        if (_disposed || _tipShown || App.IsSandbox) return;
        _tipShown = true;
        try { _icon.ShowBalloonTip(3000, AppTitle, "已在后台运行，点击托盘图标可重新打开；右键图标可打开菜单或退出。", Forms.ToolTipIcon.Info); }
        catch { /* 系统关闭通知气泡时静默降级，托盘图标与菜单不受影响 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _icon.MouseClick -= OnMouseClick;
            _icon.MouseUp -= OnMouseUp;
            CloseMenu();
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch { /* 进程正在退出时 Win32 通知区可能已回收，忽略即可 */ }
        _appIcon?.Dispose();
    }
}
