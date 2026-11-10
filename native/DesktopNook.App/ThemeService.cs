using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DesktopNook.App;

// Win11 浅/深色色板与系统强调色：直接改写 Application.Resources 里的 SolidColorBrush 实例，
// 所有引用 StaticResource 的控件会同步变色，无需重建窗口或替换资源字典。
internal static class ThemeService
{
    // 键 → 浅色 / 深色。强调色相关的四个键由 ApplyAccent 单独计算。
    private static readonly (string Key, string Light, string Dark)[] Palette =
    [
        ("WinWindowFill", "#FFF3F3F3", "#FF202020"),
        ("WinNavFill", "#FFF0F0F0", "#FF1B1B1B"),
        ("WinCardFill", "#FFFFFFFF", "#FF2B2B2B"),
        ("WinCardAlt", "#FFF9F9F9", "#FF323232"),
        ("WinStroke", "#FFE5E5E5", "#FF3B3B3B"),
        ("WinStrokeSoft", "#FFEDEDED", "#FF303030"),
        ("WinText", "#FF1B1B1F", "#FFF5F5F5"),
        ("WinTextSecondary", "#FF616161", "#FFC5C5C5"),
        ("WinTextTertiary", "#FF8A8A8E", "#FF9A9A9A"),
        ("WinHover", "#FFE8E8E8", "#FF3A3A3A"),
        ("WinUnderline", "#FF8A8A8E", "#FF9A9A9E"),
        ("WinClose", "#FFC42B1C", "#FFC42B1C"),
        ("WinSyncGreen", "#FF107C10", "#FF6CCB5F"),
        ("GlassFill", "#FFF3F3F3", "#FF202020"),
        ("GlassStroke", "#FFE5E5E5", "#FF3B3B3B"),
        ("GlassStrokeSoft", "#FFEDEDED", "#FF303030"),
        ("GlassPanelStrong", "#FFFFFFFF", "#FF2B2B2B"),
        ("GlassMenu", "#FFFAFAFA", "#FF262626"),
        ("GlassHover", "#FFE8E8E8", "#FF3A3A3A")
    ];

    private static AppSettings? _settings;
    public static bool IsDark { get; private set; }
    // 当前皮肤是否为玻璃拟态。Glass.cs 的 Mica 取色与 MainWindow 的几何都据此分流。
    public static bool IsGlass { get; private set; }
    public static event Action? ThemeChanged;

    static ThemeService()
    {
        // 用户在 Windows 设置里切换深浅主题或强调色时，跟随系统模式自动刷新。
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (_settings is null || e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)) return;
            if (_settings.ThemeMode != "system" && !_settings.AccentFollow) return;
            Apply(_settings);
        };
    }

    // WPF 会把 Application.Resources 里的纯色画刷冻结以节省资源；冻结后无法改 Color，
    // StaticResource 又只在窗口加载时解析一次。必须在任何窗口 InitializeComponent 之前
    // 把冻结实例换成同色的可写实例，后续主题切换才会作用于同一批对象引用。
    public static void EnsureMutableResources()
    {
        var resources = Application.Current.Resources;
        foreach (var key in resources.Keys.OfType<string>().ToList())
            if (resources[key] is SolidColorBrush brush && brush.IsFrozen)
                resources[key] = new SolidColorBrush(brush.Color);
    }

    public static string Apply(AppSettings settings)
    {
        _settings = settings;
        IsGlass = SkinService.IsGlass(settings);
        IsDark = settings.ThemeMode switch
        {
            "dark" => true,
            "light" => false,
            _ => SystemPrefersDark()
        };
        // 两张色板的键集合完全一致：无论从哪个皮肤切入，公共键都被整表覆盖，不会残留。
        var palette = IsGlass ? SkinService.GlassPalette : Palette;
        foreach (var (key, light, dark) in palette) Set(key, Parse(IsDark ? dark : light));
        // 图标磁贴在两套皮肤下都完全透明，图标与名称直接落在窗口底上；
        // 悬停/按下只叠一层极淡底色，不再把每个图标包成一张白色卡片。
        Set("WinTileFill", Parse("#00FFFFFF"));
        if (IsGlass)
        {
            // 玻璃皮肤有固定的番茄红强调色，忽略「强调色跟随系统」（设置页会置灰该开关）。
            // 输入框聚焦底线不能用番茄红：那会被当成校验失败，改用低对比的中性灰蓝。
            ApplySkinAccent();
            Set("WinFieldFocus", Parse(IsDark ? "#99B4C6EE" : "#806B7392"));
            Set("WinTileHover", Parse(IsDark ? "#30A0B4FF" : "#28FFFFFF"));
            Set("WinTilePressed", Parse(IsDark ? "#50A0B4FF" : "#48FFFFFF"));
        }
        else
        {
            var accent = settings.AccentFollow ? SystemAccent() : (IsDark ? Color.FromRgb(0x4C, 0xC2, 0xFF) : Color.FromRgb(0x00, 0x67, 0xC0));
            ApplyAccent(accent);
            Set("WinFieldFocus", accent);
            Set("WinTileHover", Parse(IsDark ? "#33FFFFFF" : "#1F000000"));
            Set("WinTilePressed", Parse(IsDark ? "#55FFFFFF" : "#33000000"));
        }
        ThemeChanged?.Invoke();
        return IsDark ? "深色" : "浅色";
    }

    private static void ApplyAccent(Color accent)
    {
        var baseFill = IsDark ? Color.FromRgb(0x20, 0x20, 0x20) : Colors.White;
        Set("WinAccent", accent);
        Set("WinAccentHover", IsDark ? Lighten(accent, 24) : Darken(accent, 18));
        Set("WinAccentSoft", IsDark ? Mix(accent, baseFill, .16) : Mix(accent, baseFill, .10));
        Set("WinAccentLine", IsDark ? Mix(accent, baseFill, .38) : Mix(accent, baseFill, .26));
        // 主按钮底色单独留一个键：玻璃皮肤下可换成渐变/番茄红而不影响 WinAccent 的文字用途。
        Set("WinPrimaryFill", accent);
    }

    private static void ApplySkinAccent()
    {
        var accent = Parse(IsDark ? SkinService.AccentDark : SkinService.AccentLight);
        Set("WinAccent", accent);
        Set("WinAccentHover", Parse(IsDark ? "#FFFF9AA8" : "#FFD2364A"));
        Set("WinAccentSoft", Parse(IsDark ? "#40FF7286" : "#26E8465A"));
        Set("WinAccentLine", Parse(IsDark ? "#70FF7286" : "#55E8465A"));
        Set("WinPrimaryFill", accent);
    }

    private static void Set(string key, Color color)
    {
        // 资源画刷会在窗口加载后被 WPF 冻结，改 Color 不再生效。
        // 整条替换成新实例，配合 XAML 里的 DynamicResource 让所有控件立即收到新色板。
        Application.Current.Resources[key] = new SolidColorBrush(color);
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static Color Mix(Color a, Color b, double ratio)
        => Color.FromRgb((byte)(a.R * ratio + b.R * (1 - ratio)), (byte)(a.G * ratio + b.G * (1 - ratio)), (byte)(a.B * ratio + b.B * (1 - ratio)));
    private static Color Lighten(Color c, int amount)
        => Color.FromRgb((byte)Math.Min(255, c.R + amount), (byte)Math.Min(255, c.G + amount), (byte)Math.Min(255, c.B + amount));
    private static Color Darken(Color c, int amount)
        => Color.FromRgb((byte)Math.Max(0, c.R - amount), (byte)Math.Max(0, c.G - amount), (byte)Math.Max(0, c.B - amount));

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch { return false; }
    }

    public static Color SystemAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            // AccentColor 是 0xAABBGGRR：低字节是红，高字节是蓝。
            if (key?.GetValue("AccentColor") is int abgr)
            {
                var color = Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
                if (IsDark) return Lighten(color, 40);
                return color;
            }
        }
        catch { /* 读取失败时回退到默认强调色 */ }
        return IsDark ? Color.FromRgb(0x4C, 0xC2, 0xFF) : Color.FromRgb(0x00, 0x67, 0xC0);
    }
}
