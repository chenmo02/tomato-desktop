using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace DesktopNook.App;

internal enum BackdropKind { None, Mica, Acrylic }

// Windows 11 系统背板（DWMWA_SYSTEMBACKDROP_TYPE）。与自绘壁纸层不同，
// Mica / Acrylic 由 DWM 实时合成：Acrylic 会把窗口背后的桌面与其它窗口模糊后透出来。
//
// 生效前提（缺一不可）：
//   1. AllowsTransparency=False —— layered 窗口不参与 DWM 背板；
//   2. WPF 合成目标背景色设为透明，否则不透明清屏色会盖住背板；
//   3. DWM 帧扩展到整个客户区（WindowChrome.GlassFrameThickness=-1 或本类显式调用）；
//   4. Windows 11 22621+、桌面合成开启、未启用高对比度。
// 系统「透明效果」关闭时 Acrylic 会退化成纯色，本类把该状态暴露出来供 UI 回退。
internal static class BackdropService
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_NONE = 1, DWMSBT_MAINWINDOW = 2, DWMSBT_TRANSIENTWINDOW = 3;
    private const int DWMWCP_ROUND = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled(out bool enabled);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RectNative rect);
    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative { public int Left, Top, Right, Bottom; }

    public static BackdropKind Current { get; private set; } = BackdropKind.None;
    public static string Status { get; private set; } = "尚未初始化系统背板";
    public static bool TransparencyEnabled { get; private set; }
    // 窗口形状裁剪实际使用的圆角半径（DIP）：0 表示仍是系统默认的直角矩形。
    public static double RegionRadius { get; private set; }

    // Acrylic 是否真正可见：API 调用成功但系统透明效果关闭时，DWM 只给纯色，
    // 此时必须继续显示自绘壁纸层，否则玻璃皮肤会退化成一块纯色面板。
    public static bool AcrylicVisible => Current == BackdropKind.Acrylic && TransparencyEnabled;

    public static bool SystemTransparencyEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is int value && value != 0;
        }
        catch { return false; }
    }

    internal static bool PlatformSupported()
    {
        try
        {
            // 应用清单只声明到 Win8.1，Environment.OSVersion 会报 10.0；直接读 build 号。
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = key?.GetValue("CurrentBuildNumber") as string;
            if (build is null || !int.TryParse(build, out var number) || number < 22621) return false;
            if (DwmIsCompositionEnabled(out var composition) != 0 || !composition) return false;
            if (SystemParameters.HighContrast) return false;
            return true;
        }
        catch { return false; }
    }

    // DWM 背板按整个窗口矩形合成，而 WPF 内容按 22px / 12px 圆角裁剪。
    // 若 HWND 仍是直角，圆角外的四个角会露出没有玻璃覆盖的纯背板色，形成明显方块。
    // 这里把窗口区域本身裁成与内容一致的圆角，让背板与阴影都沿同一条轮廓收口。
    // radiusDip 是已经按 Viewbox 缩放换算过的、窗口坐标系中的半径。
    public static void ApplyRoundedRegion(Window window, double radiusDip)
    {
        // 诊断/应急回退：置为 1 时窗口恢复系统默认直角区域。
        if (Environment.GetEnvironmentVariable("DESKTOPNOOK_NO_REGION") == "1") { RegionRadius = 0; return; }
        if (window.WindowState != WindowState.Normal) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        // 用 Win32 真实窗口矩形而不是 ActualWidth×DPI：WindowChrome 扩展帧与
        // per-monitor DPI 下两者可能不一致，region 偏小会把窗口内部也裁掉，
        // 导致图标区不接受鼠标/OLE 拖放。
        if (!GetWindowRect(hwnd, out var rect)) return;
        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;
        var logicalWidth = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        if (w <= 0 || h <= 0 || logicalWidth <= 0) return;
        var scale = w / logicalWidth;
        var r = (int)Math.Clamp(Math.Round(radiusDip * scale), 1, Math.Min(w, h) / 2);
        var region = CreateRoundRectRgn(0, 0, w + 1, h + 1, r * 2, r * 2);
        if (region == IntPtr.Zero) return;
        // SetWindowRgn 成功后 HRGN 归系统所有，不能再 DeleteObject。
        if (SetWindowRgn(hwnd, region, true) == 0) { DeleteObject(region); return; }
        RegionRadius = radiusDip;
    }

    // 必须在 SourceInitialized 之后调用（此时 HWND 才存在）。返回实际生效的材质。
    public static BackdropKind Apply(Window window, BackdropKind requested)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) { Status = "窗口句柄尚未就绪"; return BackdropKind.None; }

        TransparencyEnabled = SystemTransparencyEnabled();
        if (!PlatformSupported())
        {
            Status = "系统不支持 DWM 背板（需 Windows 11 22621+ 且开启桌面合成）";
            SetNone(hwnd);
            return BackdropKind.None;
        }

        var source = HwndSource.FromHwnd(hwnd);
        if (source?.CompositionTarget is null)
        {
            Status = "无法取得 HwndSource 合成目标";
            SetNone(hwnd);
            return BackdropKind.None;
        }
        // 这一句是关键：WPF 默认用不透明色清屏，不清成透明就永远看不到 DWM 背板。
        source.CompositionTarget.BackgroundColor = Colors.Transparent;

        // WindowChrome 已设 GlassFrameThickness=-1；重复扩展帧可确保无 chrome 时也成立。
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        var frameHr = DwmExtendFrameIntoClientArea(hwnd, ref margins);

        var value = requested switch
        {
            BackdropKind.Acrylic => DWMSBT_TRANSIENTWINDOW,
            BackdropKind.Mica => DWMSBT_MAINWINDOW,
            _ => DWMSBT_NONE
        };
        var hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int));
        if (hr != 0 || frameHr != 0)
        {
            Status = $"DWM 调用失败（背板 {hr}，帧扩展 {frameHr}）";
            SetNone(hwnd);
            return BackdropKind.None;
        }

        var corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        Current = requested;
        Status = requested switch
        {
            BackdropKind.Acrylic when !TransparencyEnabled => "已请求 Acrylic，但系统透明效果已关闭，实际显示为纯色",
            BackdropKind.Acrylic => "系统 Acrylic 实时背板已启用",
            BackdropKind.Mica => "系统 Mica 背板已启用",
            _ => "系统背板已关闭"
        };
        return requested;
    }

    private static void SetNone(IntPtr hwnd)
    {
        var none = DWMSBT_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
        Current = BackdropKind.None;
    }
}
