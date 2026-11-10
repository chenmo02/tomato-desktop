using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DesktopNook.App;

// 皮肤：native = 原有 Win11 系统外观（默认，行为完全不变）；
// glass = 玻璃拟态：半透明色板 + 模糊壁纸与彩色光斑背景层 + 更大圆角与柔和光晕。
//
// 两条硬约束：
// 1. 对外暴露的颜色一律是 #AARRGGBB 字符串，由 ThemeService 统一创建 SolidColorBrush。
//    DialogPalette.Get 与原生自检都按 SolidColorBrush 取值，换成渐变画刷会直接抛异常。
// 2. 玻璃层不透视背后应用窗口。窗口是 AllowsTransparency=True 的分层窗口，
//    系统级 backdrop 需要非 layered HWND，会牵动圆角、贴任务栏与拖动逻辑。
//    因此背景层用「当前壁纸模糊图 + 光斑」自行绘制，与静态 Mica 的不透视策略一致。
internal static class SkinService
{
    public const string Native = "native";
    public const string Glass = "glass";

    // 只读入两个合法值，注册表里的脏值一律回落到原生皮肤。
    public static string Normalize(string? skin) => skin == Glass ? Glass : Native;
    public static bool IsGlass(AppSettings settings) => Normalize(settings.Skin) == Glass;

    // 键集合与 ThemeService.Palette 一一对应：切皮肤时整表覆盖，不会残留上一套底色。
    // 强调色四键与 WinPrimaryFill / WinTileFill / WinTileHover / WinTilePressed / WinFieldFocus
    // 由 ThemeService 单独计算；图标磁贴在两套皮肤下都保持完全透明。
    internal static readonly (string Key, string Light, string Dark)[] GlassPalette =
    [
        // 窗口底被 Surface / backplate / 内容区 / 状态栏四层叠加引用，单层 alpha 必须很低，
        // 否则真·Acrylic 会被白色层叠糊成一块纯色面板（回退态由背景层的不透明渐变打底，
        // 同样不吃亏）。输入框、卡片、菜单另用高不透明度画刷保证文字对比度。
        ("WinWindowFill", "#1EFFFFFF", "#2E101A38"),
        ("WinNavFill", "#14FFFFFF", "#24121E40"),
        ("WinCardFill", "#D9FFFFFF", "#D92A3558"),
        ("WinCardAlt", "#B8FFFFFF", "#B3243152"),
        // 细描边压到 50% / 30%：圆角收小后，过亮的白弧线会在玻璃边缘形成塑料感。
        ("WinStroke", "#80FFFFFF", "#669AAEFF"),
        ("WinStrokeSoft", "#4DFFFFFF", "#409AAEFF"),
        ("WinText", "#FF171C33", "#FFF1F4FF"),
        ("WinTextSecondary", "#EF4D5779", "#EFBFC7EA"),
        ("WinTextTertiary", "#CC78829F", "#CC8D98C4"),
        ("WinHover", "#A6FFFFFF", "#66A0B4FF"),
        ("WinUnderline", "#B37A86A8", "#B39AA8DC"),
        ("WinClose", "#FFE14B58", "#FFFF6B7F"),
        ("WinSyncGreen", "#FF17A98C", "#FF5BE0BE"),
        // 弹窗是独立窗口、没有背景层，底色必须足够实，否则正文会压在桌面壁纸上。
        ("GlassFill", "#E6F7F9FF", "#E61A2340"),
        ("GlassStroke", "#B3FFFFFF", "#809AAEFF"),
        ("GlassStrokeSoft", "#73FFFFFF", "#5C9AAEFF"),
        ("GlassPanelStrong", "#D9FFFFFF", "#D92A3558"),
        ("GlassMenu", "#E6FBFCFF", "#E21E2946"),
        ("GlassHover", "#B8FFFFFF", "#66A0B4FF")
    ];

    // 玻璃皮肤固定使用番茄红，不随系统强调色变化；设置页会把「强调色跟随系统」置灰。
    internal static string AccentLight => "#FFE8465A";
    internal static string AccentDark => "#FFFF7286";

    // 逻辑像素 1040×680 固定构图；整个 Surface 由 Viewbox 统一缩放，自由调整尺寸时无需重排。
    private static readonly (Color Color, double X, double Y, double W, double H)[] LightBlobs =
    [
        (Color.FromArgb(0xB0, 0xFF, 0x8F, 0xB1), -90, -130, 430, 430),
        (Color.FromArgb(0xB0, 0x8C, 0x9B, 0xFF), 730, -150, 470, 470),
        (Color.FromArgb(0x8C, 0x6F, 0xE0, 0xCF), 210, 440, 420, 420),
        (Color.FromArgb(0x8C, 0xFF, 0xC2, 0x7A), 740, 410, 340, 340),
        (Color.FromArgb(0x80, 0xB5, 0x8C, 0xFF), -40, 280, 300, 300)
    ];
    private static readonly (Color Color, double X, double Y, double W, double H)[] DarkBlobs =
    [
        (Color.FromArgb(0x9E, 0x7B, 0x4B, 0xFF), -90, -130, 430, 430),
        (Color.FromArgb(0x9E, 0xFF, 0x4E, 0x8A), 730, -150, 470, 470),
        (Color.FromArgb(0x86, 0x12, 0xB5, 0xC9), 210, 440, 420, 420),
        (Color.FromArgb(0x86, 0xFF, 0x9A, 0x3C), 740, 410, 340, 340),
        (Color.FromArgb(0x80, 0x6C, 0x4B, 0xFF), -40, 280, 300, 300)
    ];

    // 把窗口在屏幕上的物理矩形换算成壁纸上的归一化裁切区域。
    // Windows 壁纸「填充」(10) 按等比 cover 居中，「拉伸」(2) 按非等比铺满；
    // 其它样式（适应 / 居中 / 跨区 / 平铺）一律按填充近似，避免引入逐屏枚举的复杂度。
    // 关键：取的是窗口所在位置对应的那一块壁纸，而不是把整张壁纸重新缩放贴进面板，
    // 否则玻璃内外图案不连续，看起来像贴了一张模糊图片而不是一块玻璃。
    public static Rect? ComputeWallpaperViewbox(BitmapSource image, Rect windowOnScreen, Size screen, bool stretch)
    {
        if (image.PixelWidth < 1 || image.PixelHeight < 1 || screen.Width < 1 || screen.Height < 1
            || windowOnScreen.Width < 1 || windowOnScreen.Height < 1) return null;

        double scaleX, scaleY, offX, offY;
        if (stretch)
        {
            scaleX = screen.Width / image.PixelWidth;
            scaleY = screen.Height / image.PixelHeight;
            offX = offY = 0;
        }
        else
        {
            var scale = Math.Max(screen.Width / image.PixelWidth, screen.Height / image.PixelHeight);
            scaleX = scaleY = scale;
            offX = (screen.Width - image.PixelWidth * scale) / 2;
            offY = (screen.Height - image.PixelHeight * scale) / 2;
        }
        var drawnW = image.PixelWidth * scaleX;
        var drawnH = image.PixelHeight * scaleY;
        var u = (windowOnScreen.X - offX) / drawnW;
        var v = (windowOnScreen.Y - offY) / drawnH;
        var uw = Math.Clamp(windowOnScreen.Width / drawnW, 0.001, 1);
        var vh = Math.Clamp(windowOnScreen.Height / drawnH, 0.001, 1);
        // 多屏负坐标、窗口贴边或跨屏时钳制到壁纸范围内，避免 Viewbox 越界出现空白。
        u = Math.Clamp(u, 0, Math.Max(0, 1 - uw));
        v = Math.Clamp(v, 0, Math.Max(0, 1 - vh));
        return new Rect(u, v, Math.Min(uw, 1 - u), Math.Min(vh, 1 - v));
    }

    // 窗口背景层：模糊壁纸提供「被抹开的背景内容」，彩色光斑提供玻璃后面的可辨识色块。
    // 返回可写的 ImageBrush，窗口移动时只需改 Viewbox，无需解码壁纸或重建整层。
    public static FrameworkElement BuildBackdrop(bool dark, BitmapSource? wallpaper, Rect? viewbox, bool acrylicVisible, out ImageBrush? wallpaperBrush)
    {
        var root = new Grid { IsHitTestVisible = false, ClipToBounds = true };
        // Acrylic 可见时绝对不能铺不透明底：DWM 背板在窗口之后，任何一层 FF alpha 的
        // 渐变都会把它完全挡住，窗口退化成一块浅色面板。回退态才需要这层渐变打底。
        root.Background = acrylicVisible
            ? Brushes.Transparent
            : new LinearGradientBrush(
                dark ? Color.FromRgb(0x14, 0x18, 0x3C) : Color.FromRgb(0xE2, 0xE8, 0xFB),
                dark ? Color.FromRgb(0x25, 0x14, 0x42) : Color.FromRgb(0xF5, 0xE7, 0xF6),
                new Point(0, 0), new Point(1, 1));

        wallpaperBrush = null;
        if (wallpaper is not null)
        {
            var brush = new ImageBrush(wallpaper)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                TileMode = TileMode.None
            };
            // Viewbox 取壁纸中窗口当前覆盖的那块区域，随窗口移动实时改写。
            if (viewbox is Rect region) brush.Viewbox = region;
            wallpaperBrush = brush;
            var layer = new Rectangle { Fill = brush, Opacity = dark ? .30 : .50 };
            RenderOptions.SetBitmapScalingMode(layer, BitmapScalingMode.LowQuality);
            // 模糊只作用在壁纸这一层，不叠加到文字与图标上，避免整窗发虚。
            layer.Effect = new BlurEffect { Radius = 30, RenderingBias = RenderingBias.Performance };
            root.Children.Add(layer);
        }

        // Acrylic 自带模糊与着色，光斑只作极淡的质感叠加，不再主导窗口观感。
        var canvas = new Canvas { IsHitTestVisible = false, ClipToBounds = true, Opacity = acrylicVisible ? .30 : 1 };
        foreach (var (color, x, y, w, h) in dark ? DarkBlobs : LightBlobs)
        {
            var brush = new RadialGradientBrush
            {
                Center = new Point(.5, .5), RadiusX = .5, RadiusY = .5
            };
            brush.GradientStops.Add(new GradientStop(color, 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1));
            var ellipse = new Ellipse { Width = w, Height = h, Fill = brush };
            Canvas.SetLeft(ellipse, x);
            Canvas.SetTop(ellipse, y);
            canvas.Children.Add(ellipse);
        }
        root.Children.Add(canvas);

        // 玻璃断面的顶部高光：让 1px 亮描边有厚度感。
        var sheen = new Border
        {
            IsHitTestVisible = false,
            Background = new LinearGradientBrush(
                Color.FromArgb((byte)(dark ? 0x28 : 0x70), 0xFF, 0xFF, 0xFF),
                Colors.Transparent, new Point(0, 0), new Point(0, 1))
        };
        root.Children.Add(sheen);
        return root;
    }
}
