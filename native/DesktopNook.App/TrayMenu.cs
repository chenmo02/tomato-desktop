using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DesktopNook.App;

// 托盘右键菜单。WinForms 的 ContextMenuStrip 会按系统默认样式画出直角白底 + 纯蓝选中块，
// 与主窗口的 WinUI 圆角卡片完全不搭；这里改用 WPF ContextMenu，直接复用 App.xaml 里
// 已有的菜单模板（圆角卡片、投影、悬停态、分隔线）与 ThemeService 的动态色板，
// 因此浅色 / 深色、原生 / 玻璃拟态四套外观都自动跟随，无需单独维护第二套配色。
internal static class TrayMenu
{
    internal static readonly string[] Labels = ["打开番茄桌面", "打开设置", "退出并结束进程"];

    private const string G_Open = "M2,3 H14 V13 H2 Z M2,6 H14 M4,4.5 H4.7 M6,4.5 H6.7";
    private const string G_Settings = "M8,5.2 A2.8,2.8 0 1 0 8,10.8 A2.8,2.8 0 1 0 8,5.2 M8,1.8 V3.4 M8,12.6 V14.2 M1.8,8 H3.4 M12.6,8 H14.2 M3.6,3.6 L4.7,4.7 M11.3,11.3 L12.4,12.4 M12.4,3.6 L11.3,4.7 M4.7,11.3 L3.6,12.4";
    private const string G_Exit = "M8,2.2 V7.6 M4.4,4.7 A5,5 0 1 0 11.6,4.7";

    // AbsolutePoint 用屏幕 DIP 坐标；Popup 自身还会把菜单夹回工作区，这里只需给出
    // 光标附近、让菜单从托盘图标上方向上展开的锚点，不必精确测量内容高度。
    private const double MenuWidth = 248;
    private const double MenuHeight = 198;

    internal static ContextMenu Create(MainWindow window, bool background = true)
    {
        var menu = new ContextMenu
        {
            FontSize = 12.5,
            MinWidth = MenuWidth,
            StaysOpen = false,
            Placement = PlacementMode.AbsolutePoint,
            PlacementTarget = window
        };
        Ref(menu, ContextMenu.BackgroundProperty, "GlassMenu");
        Ref(menu, ContextMenu.ForegroundProperty, "WinText");
        Ref(menu, ContextMenu.BorderBrushProperty, "WinStroke");

        menu.Items.Add(Header(background));
        menu.Items.Add(new Separator());
        menu.Items.Add(Row(Labels[0], G_Open, HotKeyService.Display, false, window.ShowPanel));
        menu.Items.Add(Row(Labels[1], G_Settings, null, false, window.OpenSettingsFromTray));
        menu.Items.Add(new Separator());
        menu.Items.Add(Row(Labels[2], G_Exit, null, true, window.Quit));
        return menu;
    }

    // 自检 / 退出探针用：按文字定位「退出并结束进程」，不依赖菜单项顺序。
    internal static MenuItem? FindExitItem(ContextMenu menu)=>
        menu.Items.OfType<MenuItem>().FirstOrDefault(i=>
            i.Header is Grid grid&&grid.Children.OfType<TextBlock>().Any(t=>t.Text==Labels[2]));

    // 右键时按当前光标所在显示器的 DPI 定位；菜单每次都重新构建，切换主题后立刻生效。
    internal static ContextMenu Open(MainWindow window, bool background)
    {
        var menu = Create(window, background);
        var cursor = CursorPoint();
        var scale = DpiScale(cursor);
        menu.HorizontalOffset = cursor.X / scale - 18;
        menu.VerticalOffset = cursor.Y / scale - MenuHeight;
        menu.IsOpen = true;
        return menu;
    }

    // 顶部品牌卡：番茄图标 + 名称 + 当前状态 + 版本号，把「这是哪个程序、现在处于什么状态」
    // 交代清楚，避免托盘右键只剩三条干巴巴的命令。
    private static MenuItem Header(bool background)
    {
        var head = new MenuItem { Tag = "GroupHeader", IsHitTestVisible = false, Focusable = false, IsTabStop = false };
        var grid = new Grid { MinWidth = 216, Margin = new Thickness(0, 2, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 图标直接落在菜单卡片上，不套任何彩色圆角底块；玻璃皮肤下强调色底会变成
        // 一块显眼的粉红色，与 WinUI 的中性菜单不搭。Tag 供自检确认没有底色。
        var badge = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent, Tag = "TrayBadge",
            VerticalAlignment = VerticalAlignment.Center
        };
        var logo = new Image
        {
            Width = 24, Height = 24, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        try { logo.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/AppIcon.png", UriKind.Absolute)); }
        catch { /* 资源异常时只留图标占位，菜单功能不受影响 */ }
        badge.Child = logo;
        Grid.SetColumn(badge, 0);
        grid.Children.Add(badge);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
        var title = new TextBlock { Text = "番茄桌面", FontSize = 13, FontWeight = FontWeights.SemiBold };
        Ref(title, TextBlock.ForegroundProperty, "WinText");
        var status = new TextBlock
        {
            Text = background ? "后台运行中 · 托盘常驻" : "面板已显示 · 托盘常驻",
            FontSize = 10, Margin = new Thickness(0, 2, 0, 0)
        };
        Ref(status, TextBlock.ForegroundProperty, "WinTextSecondary");
        texts.Children.Add(title);
        texts.Children.Add(status);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var version = new TextBlock
        {
            Text = MainWindow.CurrentVersionLabel, FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        Ref(version, TextBlock.ForegroundProperty, "WinTextTertiary");
        Grid.SetColumn(version, 2);
        grid.Children.Add(version);

        head.Header = grid;
        return head;
    }

    // Header 内容自带图标列与右侧提示，不占用 MenuItem 模板里宽度为 0 的 Icon 列。
    // 悬停高亮、圆角行与键盘导航仍由 App.xaml 的 MenuItem 模板负责。
    private static MenuItem Row(string label, string glyph, string? hint, bool danger, Action action)
    {
        var item = new MenuItem { Padding = new Thickness(7, 8, 9, 8), Cursor = Cursors.Hand };
        var grid = new Grid { MinWidth = 208 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = Glyph(glyph, 16, 1.5);
        Ref(icon, Shape.StrokeProperty, danger ? "WinClose" : "WinTextSecondary");
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var text = new TextBlock
        {
            Text = label, FontSize = 12.5, Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Ref(text, TextBlock.ForegroundProperty, danger ? "WinClose" : "WinText");
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (hint is not null)
        {
            var hintText = new TextBlock
            {
                Text = hint, FontSize = 10, Margin = new Thickness(12, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Ref(hintText, TextBlock.ForegroundProperty, "WinTextTertiary");
            Grid.SetColumn(hintText, 2);
            grid.Children.Add(hintText);
        }

        item.Header = grid;
        item.Click += (_, _) => action();
        return item;
    }

    private static Path Glyph(string data, double size, double thickness) => new()
    {
        Data = Geometry.Parse(data), Width = size, Height = size, Stretch = Stretch.Uniform,
        StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
    };

    private static void Ref(FrameworkElement element, DependencyProperty property, string key) =>
        element.SetResourceReference(property, key);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    private static POINT CursorPoint()
    {
        if (GetCursorPos(out var point)) return point;
        var work = SystemParameters.WorkArea;
        return new POINT { X = (int)work.Right, Y = (int)work.Bottom };
    }

    // 以光标所在显示器的有效 DPI 为准（多显示器混合缩放时菜单不会跑到别处）；
    // 老系统缺少 shcore.dll 时退回主窗口的 DPI 变换，再不行按 96 DPI 处理。
    private static double DpiScale(POINT cursor)
    {
        try
        {
            var monitor = MonitorFromPoint(cursor, 2); // MONITOR_DEFAULTTONEAREST
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 && dpiX >= 96)
                return dpiX / 96d;
        }
        catch { /* 非 Win10 环境忽略 */ }
        try
        {
            var source = PresentationSource.FromVisual(Application.Current.MainWindow);
            if (source?.CompositionTarget is not null) return Math.Max(1d, source.CompositionTarget.TransformToDevice.M11);
        }
        catch { /* 窗口尚未加载 */ }
        return 1d;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rect);

    // 点 × 后主窗口是隐藏的，Popup 的 StaysOpen=false 拿不到「点到别处」的失焦通知，
    // 因此由托盘的全局鼠标钩子自己按 Popup HWND 的屏幕矩形判断点击是否落在菜单内。
    // 返回屏幕物理像素矩形（与 GetCursorPos / 钩子回调里的坐标同一坐标系）。
    internal static bool TryGetMenuRect(ContextMenu menu, out Rect bounds)
    {
        bounds = default;
        try
        {
            if (PresentationSource.FromVisual(menu) is not System.Windows.Interop.HwndSource source || source.Handle == IntPtr.Zero)
                return false;
            if (!GetWindowRect(source.Handle, out var native)) return false;
            bounds = new Rect(native.Left, native.Top, native.Right - native.Left, native.Bottom - native.Top);
            return bounds.Width > 0 && bounds.Height > 0;
        }
        catch { return false; }
    }

    // 拿不到 Popup 矩形时保守地当作「在菜单内」，避免菜单刚弹出就被误关。
    internal static bool ContainsScreenPoint(ContextMenu menu, int x, int y) =>
        !TryGetMenuRect(menu, out var bounds) || bounds.Contains(x, y);
}
