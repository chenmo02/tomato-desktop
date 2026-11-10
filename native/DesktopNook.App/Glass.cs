using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DesktopNook.App;

// Windows 11 静态 Mica：读取桌面壁纸的平均色调，以极小权重混入全不透明的浅色底。
// 只提供色调变化，不显示壁纸图案，也不透视背后窗口——窗口是 AllowsTransparency=True
// 的分层窗口，系统级 Mica 需要非 layered HWND，会牵动圆角、贴任务栏与拖动逻辑。
// 读取失败（纯色壁纸、幻灯片、权限受限）时恢复默认中性底色，不会出现黑底或残影。
internal static class GlassBackdrop
{
    private const uint SpiGetDesktopWallpaper=0x0073;

    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern bool SystemParametersInfo(uint action,uint param,StringBuilder value,uint flags);

    // 返回当前壁纸文件路径；没有壁纸或路径失效时返回 null。
    public static string? WallpaperPath()
    {
        var buffer=new StringBuilder(1024);
        if(!SystemParametersInfo(SpiGetDesktopWallpaper,(uint)buffer.Capacity,buffer,0))return null;
        var path=buffer.ToString().Trim();
        return path.Length>0&&File.Exists(path)?path:null;
    }

    // 限制解码宽度：只用于取平均色调，不需要高分辨率，避免 4K/8K 壁纸占满内存。
    // OnLoad 立即释放文件句柄，用户更换壁纸时不会锁住原文件。
    public static BitmapImage? Load(string path)
    {
        try
        {
            var image=new BitmapImage();
            image.BeginInit();
            image.UriSource=new Uri(path,UriKind.Absolute);
            image.CacheOption=BitmapCacheOption.OnLoad;
            image.DecodePixelWidth=960;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch{return null;}
    }

    // 九宫格采样求平均色：统一转成 Bgra32 后逐点读取，只解码需要的像素。
    public static Color? AverageColor(BitmapSource source)
    {
        try
        {
            var bgra=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
            if(bgra.PixelWidth<1||bgra.PixelHeight<1)return null;
            var buffer=new byte[4];long red=0,green=0,blue=0;var count=0;
            for(var gy=1;gy<=3;gy++)
            for(var gx=1;gx<=3;gx++)
            {
                var x=Math.Clamp(bgra.PixelWidth*gx/4,0,bgra.PixelWidth-1);
                var y=Math.Clamp(bgra.PixelHeight*gy/4,0,bgra.PixelHeight-1);
                bgra.CopyPixels(new Int32Rect(x,y,1,1),buffer,4,0);
                blue+=buffer[0];green+=buffer[1];red+=buffer[2];count++;
            }
            return count==0?null:Color.FromRgb((byte)(red/count),(byte)(green/count),(byte)(blue/count));
        }
        catch{return null;}
    }
}

public partial class MainWindow
{
    private string? _glassWallpaper;
    // 玻璃背景层的壁纸图像按路径缓存：窗口移动时只改裁切区域，不反复解码壁纸。
    private string? _skinWallpaperPath;
    private BitmapSource? _skinWallpaperImage;
    private ImageBrush? _skinWallpaperBrush;

    // 背景分层的可见部分在 XAML（单层不透明底）；这里负责 Mica 色调刷新，
    // 并让圆角裁剪跟随 Surface 尺寸（自由调整窗口大小时宽高会实时变化）。
    private void InitializeGlass()
    {
        GlassRoot.SizeChanged+=(_,_)=>{UpdateGlassClip();UpdateSkinBackdropViewbox();};
        // 窗口贴着任务栏、被拖走或跨屏移动时，玻璃后面的壁纸区域必须跟着走。
        // 只改写 ImageBrush.Viewbox，不重建背景层、不重新解码壁纸。
        LocationChanged+=(_,_)=>UpdateSkinBackdropViewbox();
        DpiChanged+=(_,_)=>UpdateSkinBackdropViewbox();
        UpdateGlassClip();
        // 壁纸解码与九点取色放到窗口显示后的后台优先级：不占用启动路径，
        // 避免拖慢首次显示，也不干扰窗口激活与真实鼠标拖动时序。
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(RefreshGlassWallpaper));
        // 壁纸更换、系统透明效果开关或视觉样式变化：重算 DWM 背板状态并刷新背景层。
        // 用户在 Windows 设置里打开「透明效果」后无需重启即可切到真·Acrylic。
        SystemEvents.UserPreferenceChanged+=(_,e)=>
        {
            if(e.Category is UserPreferenceCategory.Desktop or UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
                Dispatcher.BeginInvoke(new Action(()=>
                {
                    ApplyBackdrop();
                    _glassWallpaper=null;
                    RefreshGlassWallpaper();
                    UpdateSkinBackdropViewbox();
                }));
        };
    }

    private void UpdateGlassClip()
    {
        double width=GlassRoot.ActualWidth>0?GlassRoot.ActualWidth:Math.Max(0,Surface.Width-2);
        double height=GlassRoot.ActualHeight>0?GlassRoot.ActualHeight:Math.Max(0,Surface.Height-2);
        GlassClip.Rect=new Rect(0,0,width,height);
    }

    // 10% 壁纸色 + 90% 基准色，并把每通道钳制在 232–250：深色或高饱和壁纸
    // 也不会把面板压暗，深色文字的对比度始终稳定。
    private static byte MixChannel(byte source,int baseline)
        =>(byte)Math.Clamp(Math.Round(baseline*.9+source*.1),232,250);

    private static void ApplyMica(Color? tint)
    {
        // 玻璃皮肤用 SkinBackdrop 的模糊壁纸与光斑，色板由 ThemeService 单独维护。
        // 若继续跑 Mica 取色会把半透明玻璃底覆盖成不透明浅灰，玻璃质感直接消失。
        if(ThemeService.IsGlass)return;
        void Set(string key,int baseline)
        {
            // 与 ThemeService 一致：整条替换资源，避免冻结画刷导致 Mica 底色不更新。
            var value=tint is Color color
                ?Color.FromRgb(MixChannel(color.R,baseline),MixChannel(color.G,baseline),MixChannel(color.B,baseline))
                :Color.FromRgb((byte)baseline,(byte)baseline,(byte)baseline);
            Application.Current.Resources[key]=new SolidColorBrush(value);
        }
        // 深色主题下不掺壁纸色：深色底 + 浅色文字已经由 ThemeService 设定，
        // 混入浅色壁纸会破坏对比度；这里只恢复深色基准色。
        if(ThemeService.IsDark)
        {
            // 深色基准不走 MixChannel 的浅色钳制，直接替换成固定深色。
            Application.Current.Resources["WinWindowFill"]=new SolidColorBrush(Color.FromRgb(0x20,0x20,0x20));
            Application.Current.Resources["WinNavFill"]=new SolidColorBrush(Color.FromRgb(0x1B,0x1B,0x1B));
            return;
        }
        // 只给窗口底与 NavigationView 底染色；图标磁贴、输入框、菜单保持系统标准的纯白/中性灰。
        Set("WinWindowFill",243);
        Set("WinNavFill",240);
    }

    private void RefreshGlassWallpaper()
    {
        var path=GlassBackdrop.WallpaperPath();
        if(ThemeService.IsGlass)
        {
            // 壁纸路径没变且背景层已建好时直接复用；深浅主题切换时 ApplySettings
            // 会清空 _glassWallpaper，强制用新的浅/深光斑重建。
            if(string.Equals(path,_glassWallpaper,StringComparison.OrdinalIgnoreCase)&&SkinBackdrop.Children.Count>0)return;
            RebuildSkinBackdrop(path);
            return;
        }
        if(path is null)
        {
            if(_glassWallpaper is null)return;
            _glassWallpaper=null;ApplyMica(null);return;
        }
        if(string.Equals(path,_glassWallpaper,StringComparison.OrdinalIgnoreCase))return;
        var image=GlassBackdrop.Load(path);
        _glassWallpaper=path;
        ApplyMica(image is null?null:GlassBackdrop.AverageColor(image));
    }

    // 切换 DWM 系统背板：玻璃皮肤用实时 Acrylic，原生皮肤用 Mica。
    // 背板失败时 BackdropService 会把状态置为 None，自绘壁纸层照常接管。
    internal void ApplyBackdrop()
        =>BackdropService.Apply(this,ThemeService.IsGlass?BackdropKind.Acrylic:BackdropKind.Mica);

    // 皮肤几何与背景层：色板由 ThemeService 整表替换，这里只处理画刷无法表达的形态差异。
    // 原生皮肤把圆角、光晕、背景层全部复位，保证原外观 1:1 不变。
    // 供原生自检核对皮肤形态，避免依赖 XAML 生成字段的访问级别。
    internal double SkinCornerRadius => Surface.CornerRadius.TopLeft;
    internal bool SkinBackdropVisible => SkinBackdrop.Visibility == Visibility.Visible && SkinBackdrop.Children.Count > 0;
    internal Rect? SkinWallpaperViewbox => _skinWallpaperBrush?.Viewbox;
    // Acrylic 生效时背景层根节点必须是透明的，否则 DWM 背板被整块挡住。
    internal bool SkinBackdropAllowsAcrylic
        =>SkinBackdrop.Children.Count>0
          &&SkinBackdrop.Children[0] is Grid root
          &&root.Background is SolidColorBrush brush
          &&brush.Color.A==0;

    // rebuildBackdrop=false 用于启动与设置切换：几何立即生效，壁纸解码仍交给
    // RefreshGlassWallpaper 的后台优先级路径，不占用首次显示。
    internal void ApplySkinVisual(bool rebuildBackdrop=true)
    {
        var glass=ThemeService.IsGlass;
        // 窗口 HWND 已按同一半径裁成圆角，四角会直接露出桌面。半径过大时高饱和
        // 壁纸被「挖」进窗口，形成生硬缺口；4px 把缺口压到几乎不可见。
        var radius=glass?4d:12d;
        Surface.CornerRadius=new CornerRadius(radius);
        GlassClip.RadiusX=GlassClip.RadiusY=radius;
        StatusBar.CornerRadius=new CornerRadius(0,0,radius,radius);
        InfoBar.CornerRadius=new CornerRadius(glass?16:6);
        SkinBackdrop.Visibility=glass?Visibility.Visible:Visibility.Collapsed;
        Surface.Effect=glass
            ?new DropShadowEffect{BlurRadius=30,ShadowDepth=5,Opacity=.26,Color=Color.FromRgb(0x3A,0x3A,0x72),RenderingBias=RenderingBias.Performance}
            :null;
        if(glass){if(rebuildBackdrop)RebuildSkinBackdrop();}
        else
        {
            SkinBackdrop.Children.Clear();
            // 回到原生皮肤时强制让静态 Mica 重新取色，恢复原来的壁纸色调行为。
            _glassWallpaper=null;
        }
        UpdateGlassClip();
        ApplyWindowRegion();
    }

    // 窗口在屏幕上的物理像素矩形：PointToScreen 返回设备坐标，
    // ActualWidth/Height 是逻辑像素，需要按当前 DPI 换算成同一单位。
    private Rect WindowScreenRect()
    {
        if(!IsLoaded||WindowState!=WindowState.Normal)return default;
        try
        {
            var origin=PointToScreen(new Point(0,0));
            var dpi=VisualTreeHelper.GetDpi(this);
            return new Rect(origin.X,origin.Y,ActualWidth*dpi.DpiScaleX,ActualHeight*dpi.DpiScaleY);
        }
        catch{return default;}
    }

    private Size ScreenPixelSize()
    {
        var dpi=VisualTreeHelper.GetDpi(this);
        return new Size(SystemParameters.PrimaryScreenWidth*dpi.DpiScaleX,SystemParameters.PrimaryScreenHeight*dpi.DpiScaleY);
    }

    // 壁纸样式：0 居中 / 2 拉伸 / 6 适应 / 10 填充 / 22 跨区；平铺由 TileWallpaper 另判。
    // 只区分「拉伸」与其余等比模式，其余统一按填充近似，不做逐屏枚举。
    private static bool WallpaperIsStretch()
    {
        try
        {
            using var key=Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            var style=key?.GetValue("WallpaperStyle") as string??"10";
            var tile=key?.GetValue("TileWallpaper") as string??"0";
            return tile=="1"||style=="2";
        }
        catch{return false;}
    }

    private Rect? CurrentWallpaperViewbox()
        =>_skinWallpaperImage is null?null
          :SkinService.ComputeWallpaperViewbox(_skinWallpaperImage,WindowScreenRect(),ScreenPixelSize(),WallpaperIsStretch());

    // 窗口位置、尺寸或 DPI 变化时的轻量刷新：只改裁切区域，不进入壁纸解码与整层重建。
    internal void UpdateSkinBackdropViewbox()
    {
        if(!ThemeService.IsGlass||_skinWallpaperBrush is null)return;
        if(CurrentWallpaperViewbox() is Rect region)_skinWallpaperBrush.Viewbox=region;
    }

    // 玻璃背景层只在切换皮肤、深浅主题或壁纸时重建，不进入每帧渲染路径。
    private void RebuildSkinBackdrop(string? path=null)
    {
        path??=GlassBackdrop.WallpaperPath();
        if(!string.Equals(path,_skinWallpaperPath,StringComparison.OrdinalIgnoreCase)||_skinWallpaperImage is null)
        {
            _skinWallpaperImage=path is null?null:GlassBackdrop.Load(path);
            _skinWallpaperPath=path;
        }
        _glassWallpaper=path;
        // Acrylic 真正可见时不再铺自绘壁纸图，让 DWM 的实时模糊从透明客户区透出来；
        // 只保留彩色光斑与顶部高光作为玻璃质感叠加。回退状态则完整使用壁纸对齐层。
        var acrylic=BackdropService.AcrylicVisible;
        var useWallpaper=!acrylic;
        var image=useWallpaper?_skinWallpaperImage:null;
        var viewbox=useWallpaper?CurrentWallpaperViewbox():null;
        SkinBackdrop.Children.Clear();
        SkinBackdrop.Children.Add(SkinService.BuildBackdrop(ThemeService.IsDark,image,viewbox,acrylic,out _skinWallpaperBrush));
    }
}
