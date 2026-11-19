using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using DesktopNook.Core;

namespace DesktopNook.App;

public partial class MainWindow : Window
{
    private const string ItemFormat = "DesktopNook.ManagedItem.v1";
    // NavigationView 行右侧的项目计数：附加属性让计数进入样式模板，
    // 按钮 Content 仍是分类名称，分类筛选与自动化名称不受影响。
    public static readonly DependencyProperty NavCountProperty=DependencyProperty.RegisterAttached(
        "NavCount",typeof(string),typeof(MainWindow),new PropertyMetadata(""));
    public static void SetNavCount(DependencyObject element,string value)=>element.SetValue(NavCountProperty,value);
    public static string GetNavCount(DependencyObject element)=>(string)element.GetValue(NavCountProperty);
    private string? _activeCategory;
    // 右侧工具条搜索框的关键词，只过滤显示，不改变收纳数据。
    private string _iconFilter="";
    private string _categoryFilter="";
    // 侧栏分类行拖动排序：与「把图标拖进分类」共用 DragOver/Drop，用独立数据格式区分。
    private const string CategoryDragFormat="DesktopNook.CategoryRow";
    private Point? _categoryDragStart;
    private DateTime _categoryDragEnd;
    private int _placementVersion;
    private bool _onlineBusy;
    private OnlineRelease? _latestRelease;
    // 版本号唯一来源是 csproj 的 <Version>：两段版本显示「v主.次」，带修订号时保留三段。
    internal static string ProductVersion
    {
        get
        {
            var v=System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!;
            return v.Build==0?$"{v.Major}.{v.Minor}":v.ToString(3);
        }
    }
    internal static string CurrentVersionLabel=>"v"+ProductVersion;
    internal string UpdateMenuText=>!OnlineUpdater.Enabled?$"在线更新未启用（当前 {CurrentVersionLabel}）":_latestRelease is null?$"检查在线更新（当前 {CurrentVersionLabel}）…":$"更新到 v{_latestRelease.Version.ToString(3)}（当前 {CurrentVersionLabel}）…";
    // 侧栏左下角版本号：发现新版本时追加「有更新」并改用强调色；颜色走动态资源，仍跟随主题与系统强调色。
    internal string VersionBadgeText=>VersionLabel.Text;
    internal bool VersionBadgeAccent=>VersionLabel.Foreground is SolidColorBrush ink
        &&Application.Current.FindResource("WinAccent") is SolidColorBrush accent&&ink.Color==accent.Color;
    private void ApplyVersionBadge(Version? update)
    {
        bool has=update is not null;
        VersionLabel.Text="番茄桌面 · 版本 "+ProductVersion+(has?" · 有更新":"");
        VersionLabel.FontWeight=has?FontWeights.SemiBold:FontWeights.Normal;
        VersionLabel.SetResourceReference(TextBlock.ForegroundProperty,has?"WinAccent":"WinTextTertiary");
        VersionLabel.ToolTip=has?$"发现新版本 v{update!.ToString(3)}，可在「更多」菜单中更新":null;
    }
    internal void RefreshVersionBadge()=>ApplyVersionBadge(_latestRelease?.Version);
    // 尺寸自检用：不访问网络，直接预览「有更新 / 无更新」两种状态。
    internal void PreviewVersionBadge(Version? update)=>ApplyVersionBadge(update);
    private bool _dragging; private int _interactions; private int _menus; private bool _allowExit; private bool _exitRequested; private DateTime _suppressUntil;
    // 自检用：确认用户点「退出并结束进程」后确实进入了进程级退出链路。
    internal bool ExitRequested=>_exitRequested;
    // 托盘图标随主窗口创建：× 隐藏窗口后进程继续运行，托盘成为唯一的可见入口。
    private TrayService? _tray;
    internal TrayService? Tray => _tray;
    // 双击会产生两次 Up/Click，用同一项的短节流保证只启动一次。
    private string? _lastLaunchId; private DateTime _lastLaunchAt;
    // 网格内拖动：鼠标捕获期间记录按钮与起点；松手后按落点重排顺序。
    private Button? _dragButton; private NookItem? _dragItem;
    private Point _dragPointer; private bool _dragMoved;
    // 拖放期间/刚结束时禁止「失活自动收起」，避免拖图标时整个面板消失。
    private DateTime _hideGuardUntil;
    private bool _manualPlacement; private double _manualLeft; private double _manualTop;
    // 跨屏 DPI 刷新是高频消息（WM_WINDOWPOSCHANGED 每次移动都发），合并成一次延后操作。
    private bool _regionRefreshQueued;
    private bool _dpiLayoutRefreshQueued;
    // 自检用：记录 DPI 布局刷新真正执行的次数，验证 Win32 消息钩子确实接上了。
    internal static int DpiLayoutRefreshCount { get; private set; }
    // 自检用：捕获 SourceInitialized 阶段背板声明的结果，用于定位启动态与
    // 窗口渲染完成后的状态差异（合成目标 / DWM 可能尚未就绪）。
    internal static string? BackdropStatusAtSourceInitialized { get; private set; }
    internal static BackdropKind BackdropKindAtSourceInitialized { get; private set; } = BackdropKind.None;
    // 自检用：首帧后的背板收敛只应执行一次。
    internal static int BackdropSettleCount { get; private set; }
    private bool _backdropSettled;
    // 侧边栏分类行的选中态只体现在颜色上：字重、内边距、边框厚度恒定，行宽不会变化。
    // 当前分类同时由右侧内容标题与底部状态栏（如「全部 · 1 个图标」）提示。
    public bool KeepOpenForVerification { get; set; }
    public NookStore Store => App.Store;
    public string? ActiveCategory => _activeCategory;
    public int VisibleCount => Icons.Items.Count;
    internal ContextMenu? LastMenu { get; private set; }
    internal Button IconButton(string id) => FindVisualChildren<Button>(Icons).Single(b => b.Tag is NookItem item && item.Id == id);
    internal Point DropPoint => DropSurface.PointToScreen(new Point(DropSurface.ActualWidth/2,DropSurface.ActualHeight/2));
    internal List<Point> IconPositions() => FindVisualChildren<Button>(Icons).Select(b=>b.TranslatePoint(new Point(0,0),this)).ToList();
    public MainWindow()
    {
        // 先把可能被 WPF 冻结的色板换成可写实例，再解析 XAML 的 StaticResource。
        ThemeService.EnsureMutableResources();
        // 非 layered 窗口 + 完整玻璃帧：DWM 系统背板可以透到客户区，
        // 同时保留 WPF 圆角裁剪、柔和阴影与自定义拖动/缩放逻辑。
        // ResizeBorderThickness/CaptionHeight 归零，避免 WindowChrome 抢走自定义的
        // 标题栏拖动与 ResizeHandles 命中区域。
        WindowChrome.SetWindowChrome(this,new WindowChrome
        {
            GlassFrameThickness=new Thickness(-1),ResizeBorderThickness=new Thickness(0),
            CaptionHeight=0,CornerRadius=new CornerRadius(0),UseAeroCaptionButtons=false
        });
        SourceInitialized+=(_,_)=>
        {
            ApplyBackdrop();
            BackdropStatusAtSourceInitialized=BackdropService.Status;
            BackdropKindAtSourceInitialized=BackdropService.Current;
            // WPF 的 SizeChanged 只在 DIP 尺寸变化时触发，而跨屏 DPI 变化往往
            // 恰好保持 DIP 尺寸不变、只把 HWND 物理尺寸放大；必须在 Win32
            // 消息层兜底，否则 SetWindowRgn 的旧 HRGN 会一直裁着窗口。
            var hwnd=new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WindowMessageHook);
        };
        // 窗口形状跟随皮肤圆角与 Viewbox 缩放；尺寸/DPI 变化后必须重裁，
        // 否则 DWM 背板会在四角画出未经玻璃覆盖的直角色块。
        SizeChanged+=(_,_)=>QueueRegionRefresh();
        StateChanged+=(_,_)=>{QueueRegionRefresh();SettleBackdropAfterFirstFrame();};
        DpiChanged+=(_,_)=>{QueueRegionRefresh();QueueDpiLayoutRefresh();};
        ContentRendered+=(_,_)=>SettleBackdropAfterFirstFrame();
        InitializeComponent();
        // 通知区图标常驻；无 Shell / 无权限时静默降级，窗口与收纳功能不受影响。
        try
        {
            _tray = new TrayService(this);
            Closed += (_,_) => { _tray?.Dispose(); _tray = null; };
        }
        catch { _tray = null; }
        // 侧栏版本文字跟随程序集版本与在线检查结果，避免 XAML 里的占位值与 csproj 脱节。
        RefreshVersionBadge();
        // 主题必须在窗口布局前应用：App.xaml 里的画刷是共享实例，改色后所有控件同步生效。
        Settings=App.Options.ReadSettings();
        ThemeService.Apply(Settings);
        // XAML 的 Topmost="True" 只是设计期默认值，实际层级由「设置 → 窗口调整」决定。
        ApplyTopmost();
        ApplyWindowIcon();InitializeGlass();ApplySkinVisual(false);SyncZoomButton();InitializeSizing();
        _undo=new UndoService(App.Store.DataDirectory);
        _autoCollect=new AutoCollectService(()=>App.Store,count=>
            Dispatcher.BeginInvoke(new Action(()=>{Render();SetStatus("已自动收纳 "+count+" 个新项目到未分类");})));
        var position=App.Options.ReadWindowPosition();
        _manualPlacement=position.Manual;_manualLeft=position.Left;_manualTop=position.Top;
        PreviewMouseLeftButtonDown+=OnWindowPreviewMouseDown;
        Activated+=(_,_)=>Place();
        // 系统深浅主题/强调色变化时，跟随系统模式要重算 Mica 底色并刷新代码里写死的行颜色。
        ThemeService.ThemeChanged+=()=>Dispatcher.BeginInvoke(new Action(()=>
        {
            _glassWallpaper=null;ApplyBackdrop();ApplySkinVisual(false);RefreshGlassWallpaper();UpdateTabSelection();Render();
            // 设置页的卡片与文字已绑定动态资源，色板整条替换后自动生效；
            // 不再重建，避免用户点开关时控件被替换、ToggleSwitch 动画被打断。
        }));
        InfoBar.Visibility=Settings.InfoBarDismissed?Visibility.Collapsed:Visibility.Visible;
        // 默认打开第一个自定义分类（「未分类」固定末尾，不作为默认视图）；
        // 无自定义分类时回退到「全部」。用户手动切回「全部」后不会被强制改回。
        _activeCategory=Store.State.Categories.FirstOrDefault(c=>c.Id!="uncategorized")?.Id;
        Render();
    }
    // 置顶开关唯一的应用点：启动、设置变更、导入配置包与自检都走这里，
    // 避免 XAML 静态值与用户偏好各自维护一份状态。
    internal void ApplyTopmost()=>Topmost=Settings.AlwaysOnTop;
    // 任务栏按钮图标取自窗口 Icon。XAML 用 PNG 作保底，避免图标资源异常时任务栏
    // 退回默认的空白文档图标；这里优先换成标准 DIB 多尺寸 ICO，保证不同 DPI 下清晰。
    private void ApplyWindowIcon()
    {
        try
        {
            var frame=BitmapFrame.Create(new Uri("pack://application:,,,/Assets/AppIcon.ico",UriKind.Absolute),BitmapCreateOptions.None,BitmapCacheOption.OnLoad);
            if(frame.PixelWidth>0)Icon=frame;
        }
        catch{/* ICO 资源不可用时沿用 XAML 指定的 PNG 图标 */}
    }
    private const int WmSize=0x0005,WmWindowPosChanged=0x0047,WmDpiChanged=0x02E0;
    // WM_DPICHANGED 由 WPF 转成 DpiChanged 事件，但 WPF 是在同一条消息里稍后才
    // 把 HWND 缩放到新的物理矩形。同步在事件里重裁区域读到的还是旧矩形，
    // 所以这里只排队、把真正的工作延后到本轮消息处理结束之后。
    private IntPtr WindowMessageHook(IntPtr hwnd,int msg,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if(msg is WmSize or WmWindowPosChanged or WmDpiChanged)QueueRegionRefresh();
        if(msg==WmDpiChanged)QueueDpiLayoutRefresh();
        return IntPtr.Zero;
    }
    private void QueueRegionRefresh()
    {
        if(_regionRefreshQueued)return;
        _regionRefreshQueued=true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>
        {
            _regionRefreshQueued=false;
            ApplyWindowRegion();
        }));
    }
    // 让冷启动等价于「进设置切换一次 UI」后的状态。
    // WindowChrome 会在窗口显示时重新设置 DWM 帧边距，可能覆盖 SourceInitialized
    // 阶段的背板声明；DWM 也常要到首帧完成后才为 HWND 建立系统背板合成。
    // 因此首帧后先撤销再重设材质，强制 DWM 重新评估，再按最终状态重建自绘层。
    private void SettleBackdropAfterFirstFrame()
    {
        if(_backdropSettled)return;
        _backdropSettled=true;
        BackdropSettleCount++;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>
        {
            // 两次 API 调用在同一个 dispatcher 回调内完成，DWM 只呈现最终状态，
            // 不会出现可见的中间帧或闪烁。
            var expected=ThemeService.IsGlass?BackdropKind.Acrylic:BackdropKind.Mica;
            if(BackdropService.Current==expected)BackdropService.Apply(this,BackdropKind.None);
            ApplyBackdrop();
            // 开机自启时窗口一开始就最小化：只收敛 DWM 材质声明，不把壁纸层加载回来。
            if(_resourcesSuspended)return;
            ResyncSkinBackdrop();
            UpdateGlassClip();
            ApplyWindowRegion();
        }));
    }
    private void QueueDpiLayoutRefresh()
    {
        if(_dpiLayoutRefreshQueued)return;
        _dpiLayoutRefreshQueued=true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>
        {
            _dpiLayoutRefreshQueued=false;
            DpiLayoutRefreshCount++;
            // DIP 尺寸可能没变，SizeChanged 不会来：显式重算内容层尺寸、圆角裁剪
            // 与壁纸对齐，并重新声明透明清屏色 + DWM 背板，避免合成层停留在旧 DPI。
            UpdateSizingLayout();
            UpdateGlassClip();
            UpdateSkinBackdropViewbox();
            ApplyBackdrop();
            ApplyWindowRegion();
            Surface.InvalidateVisual();
            InvalidateVisual();
        }));
    }

    // Surface 位于 Viewbox 内，圆角半径要按实际缩放换算成窗口坐标再裁 HWND。
    internal void ApplyWindowRegion()
    {
        if(!IsLoaded||WindowState!=WindowState.Normal)return;
        try
        {
            var transform=Surface.TransformToAncestor(this);
            var a=transform.Transform(new Point(0,0));
            var b=transform.Transform(new Point(1,0));
            var scale=Math.Abs(b.X-a.X);
            if(scale<=0)scale=1;
            BackdropService.ApplyRoundedRegion(this,Surface.CornerRadius.TopLeft*scale);
        }
        catch(InvalidOperationException){/* 视觉树尚未完成布局，Loaded 后会再调用 */}
    }
    private void OnLoaded(object sender,RoutedEventArgs e)
    {
        TaskbarPlacement.EnableToggle(this);Place();
        ApplyWindowRegion();
        // 窗口首次贴到任务栏后，玻璃背景要按最终屏幕位置重取壁纸裁切区域。
        UpdateSkinBackdropViewbox();
        ApplyHotKey();
        _autoCollect?.SetEnabled(Settings.AutoCollect);
        // 开机自启时窗口初始就是最小化，StateChanged 未必在 Show() 前触发；
        // 显式同步一次挂起状态，保证「启动即驻留后台」这条路径同样不保留大对象。
        if(WindowState==WindowState.Minimized)SuspendIdleResources();
        // 失效检测放到后台优先级，不占用首次显示；解析快捷方式需要 UI 线程 STA。
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(ScanBroken));
        // 启动与数据变更只同步展示目录里的快捷方式；「固定到快速访问」由安装流程
        // --maintenance=shortcut 完成，避免每次启动都改写用户的资源管理器设置。
        // 安装时 Shell 固定最多等 2.5 秒，超时会留报告；这里用独立 STA 后台线程补做一次，
        // 既不阻塞 UI，也不让安装器一直等资源管理器。
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(()=>
        {
            try{ExplorerNavService.Sync(Store.DataDirectory,Store);}catch{}
            if(App.IsSandbox)return;
            var retry=new Thread(()=>
            {
                try
                {
                    var report=Path.Combine(Store.DataDirectory,"nav-pin-report.json");
                    if(!File.Exists(report))return;
                    var text=File.ReadAllText(report);
                    if(!text.Contains("\"status\":\"timeout\"")&&!text.Contains("\"status\":\"pending\""))return;
                    var pinned=ExplorerNavService.Register(Store.DataDirectory,Store);
                    File.WriteAllText(report,ExplorerNavService.Diagnose(Store.DataDirectory,pinned));
                }
                catch{}
            }){IsBackground=true};
            retry.SetApartmentState(System.Threading.ApartmentState.STA);
            retry.Start();
        }));
        if(Store.RecoveryMessages.Count>0) SetStatus(Store.RecoveryMessages.Last());
        if(!App.IsSandbox&&OnlineUpdater.Enabled)_=CheckOnlineUpdateAsync(false);
        // TEMP layout probe (removed after diagnosis)
        var probe=Environment.GetCommandLineArgs();var probeIndex=Array.IndexOf(probe,"--dump-layout");
        if(probeIndex>=0&&probeIndex+1<probe.Length)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(async()=>
            {
                await System.Threading.Tasks.Task.Delay(800);
                var zoomIndex=Array.IndexOf(probe,"--probe-zoom");
                if(zoomIndex>=0&&zoomIndex+1<probe.Length&&double.TryParse(probe[zoomIndex+1],out var zoom)&&zoom>1)
                {
                    Width=WindowSizePreference.BaseWidth*zoom;Height=WindowSizePreference.BaseHeight*zoom;
                    UpdateSizingLayout();
                    await System.Threading.Tasks.Task.Delay(600);
                }
                ShowSettingsView();
                await System.Threading.Tasks.Task.Delay(400);
                var shot=new RenderTargetBitmap((int)Math.Ceiling(ActualWidth),(int)Math.Ceiling(ActualHeight),96,96,PixelFormats.Pbgra32);
                shot.Render(this);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(shot));
                using(var image=File.Create(Path.ChangeExtension(probe[probeIndex+1],".png")))encoder.Save(image);
                try{ScreenCapture.Save(Path.GetDirectoryName(probe[probeIndex+1])!,"real-settings-glass.png");}catch{}
                var dpi=VisualTreeHelper.GetDpi(this);
                var transform=Surface.TransformToAncestor(this);
                var origin=transform.Transform(new Point(0,0));var unit=transform.Transform(new Point(1,0));
                var scale=Math.Abs(unit.X-origin.X);
                File.WriteAllLines(probe[probeIndex+1],new[]{
                    $"Window Width={Width} Height={Height} Actual={ActualWidth}x{ActualHeight}",
                    $"DPI={dpi.DpiScaleX}",
                    $"Surface Width={Surface.Width} Height={Surface.Height} Actual={Surface.ActualWidth}x{Surface.ActualHeight} ViewboxScale={scale}",
                    $"GlassRoot Actual={GlassRoot.ActualWidth}x{GlassRoot.ActualHeight}",
                    $"Workspace Actual={Workspace.ActualWidth}x{Workspace.ActualHeight}",
                    $"SettingsSurface Actual={SettingsSurface.ActualWidth}x{SettingsSurface.ActualHeight}",
                    $"SettingsGroups Actual={SettingsGroups.ActualWidth}x{SettingsGroups.ActualHeight}",
                    $"HwndBounds(logical)={TaskbarPlacement.Bounds(this)}",
                    $"SizePref={SizePreference}"
                });
                Application.Current.Shutdown();
            }));
    }
    private async void Place()
    {
        if(_resizing || KeepOpenForVerification || !IsLoaded || WindowState!=WindowState.Normal)return;
        // 用户手动拖动过面板后不再贴回任务栏，直到在「设置 → 窗口调整」里选择「贴回任务栏」。
        if(_manualPlacement){ApplyManualPosition();return;}
        var version=++_placementVersion;
        TaskbarPlacement.Apply(this,TaskbarPlacement.Fallback(),PreferredWindowSize);
        var anchor=await TaskbarPlacement.FindAsync();
        if(anchor is not null && version==_placementVersion && !_resizing && !KeepOpenForVerification && WindowState==WindowState.Normal)TaskbarPlacement.Apply(this,anchor,PreferredWindowSize);
    }
    private void ApplyManualPosition()
    {
        var work=TaskbarPlacement.WorkArea(this);
        var width=ActualWidth>0?ActualWidth:Width;var height=ActualHeight>0?ActualHeight:Height;
        var left=Math.Clamp(_manualLeft,work.Left,Math.Max(work.Left,work.Right-width));
        var top=Math.Clamp(_manualTop,work.Top,Math.Max(work.Top,work.Bottom-height));
        TaskbarPlacement.Move(this,left,top);
    }
    internal void SaveWindowPosition()
    {
        try{App.Options.WriteWindowPosition(_manualPlacement,_manualLeft,_manualTop);}
        catch{SetStatus("窗口位置已调整，但未能保存设置。");}
    }
    // WindowStyle=None 没有系统标题栏，必须自己实现拖动；只在顶部标题区域生效，
    // 按钮与图标区域保持原有点击、右键菜单和 OLE 拖放行为。
    private void OnWindowPreviewMouseDown(object sender,MouseButtonEventArgs e)
    {
        if(e.ChangedButton!=MouseButton.Left || e.ButtonState!=MouseButtonState.Pressed || e.ClickCount>1)return;
        if(_resizing || _dragging || WindowState!=WindowState.Normal || KeepOpenForVerification)return;
        if(e.OriginalSource is DependencyObject source && IsInteractive(source))return;
        if(!TitleBarHit(e.GetPosition(this)))return;
        _interactions++;
        try
        {
            _placementVersion++; // 取消尚未返回的任务栏定位结果
            DragMove();
            var bounds=TaskbarPlacement.Bounds(this);
            _manualLeft=bounds.Left;_manualTop=bounds.Top;_manualPlacement=true;
            SaveWindowPosition();
        }
        catch(Exception){/* 左键已松开或系统拒绝本次拖动时忽略，不影响点击与收纳功能 */}
        finally{_interactions--;}
    }
    private bool TitleBarHit(Point point)
    {
        if(TitleBar is null || TitleBar.ActualWidth<=0 || TitleBar.ActualHeight<=0)return false;
        return TitleBar.TransformToVisual(this).TransformBounds(new Rect(0,0,TitleBar.ActualWidth,TitleBar.ActualHeight)).Contains(point);
    }
    private static bool IsInteractive(DependencyObject? source)
    {
        while(source is not null && source is not Window)
        {
            if(source is Control)return true;
            source=source is Visual?VisualTreeHelper.GetParent(source):null;
        }
        return false;
    }
    // 标题栏按钮状态复位：① 清掉点击后残留的键盘焦点（窗口最小化/恢复时 WPF 会
    // 还原上次焦点，让按钮长期显示 WinHover 整块底色）；② 让 WPF 按当前鼠标位置重新
    // 做命中测试，解决窗口移动、SetWindowPos 贴任务栏或最小化恢复后收不到
    // WM_MOUSELEAVE，IsMouseOver 与 ToolTip 一直挂在按钮上（关闭按钮常驻红底）的问题。
    private void ResetCaptionState()
        =>Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>
        {
            Keyboard.ClearFocus();
            Mouse.Synchronize();
        }));
    public void ShowPanel()
    {
        WindowState=WindowState.Normal; Show(); Place(); Activate(); ResetCaptionState();
        _tray?.SetBackgroundState(false);
        // 从托盘隐藏态恢复时 WindowState 没有变化，OnStateChanged 不会触发，需显式重建后台资源。
        ResumeIdleResources();
    }
    public void TogglePanel(){if(WindowState!=WindowState.Minimized&&IsVisible)WindowState=WindowState.Minimized;else ShowPanel();}
    // 点 × / 「隐藏到托盘」：隐藏窗口而不是关闭窗口，任务栏按钮随之消失，进程继续在托盘驻留。
    // 隐藏态下 WindowState 不变，所以资源挂起必须在这里显式触发，恢复由 ShowPanel 负责。
    internal void HideToTray()
    {
        ResetCaptionState();
        Hide();
        _tray?.SetBackgroundState(true);
        SuspendIdleResources();
        SetStatus("已在托盘后台运行，点击托盘图标或按 "+HotKeyService.Display+" 可重新打开");
        _tray?.NotifyBackgroundTip();
    }
    // 托盘右键「打开设置」：先唤起面板，再保证设置分组已构建。
    internal void OpenSettingsFromTray()
    {
        ShowPanel();
        if(!_settingsLoaded){BuildSettings();_settingsLoaded=true;}
        ShowSettingsView();
    }
    // 面板长期驻留后台时，只保留「随时能被唤起」所需的状态：释放玻璃壁纸位图、
    // 模糊背景层、「全部图标」页上百个卡片控件和图标缓存里的冷门尺寸。收纳数据、
    // 分类、窗口位置与设置全部保留；恢复显示时按原路径重建，不改变交互语义。
    private bool _resourcesSuspended;
    internal bool ResourcesSuspended=>_resourcesSuspended;
    private void SuspendIdleResources()
    {
        // UI 自检需要观察完整视觉树，验证模式下不挂起。
        if(_resourcesSuspended||KeepOpenForVerification)return;
        _resourcesSuspended=true;
        ReleaseBackdropVisuals();
        ReleaseCatalogVisuals();
        ShellIntegration.TrimForIdle();
        _autoCollect?.SetBackgroundMode(true);
    }
    private void ResumeIdleResources()
    {
        if(!_resourcesSuspended)return;
        _resourcesSuspended=false;
        _autoCollect?.SetBackgroundMode(false);
        // 背景层同步恢复：面板重新可见的第一帧就应该是正确的皮肤，不留空白帧。
        RestoreBackdropVisuals();
        // 图标库卡片可能有上百个，放到后台优先级重建，避免挡住面板打开。
        Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(RestoreCatalogVisuals));
    }
    private void OnStateChanged(object? sender,EventArgs e)
    {
        ResetCaptionState();
        if(WindowState==WindowState.Minimized)SuspendIdleResources();else ResumeIdleResources();
        // 全屏时 HWND 形状交给系统：旧 HRGN 是 Normal 尺寸的圆角，不撤会把窗口裁掉一大块。
        if(WindowState==WindowState.Maximized)BackdropService.ClearRoundedRegion(this);
        else ApplyWindowRegion();
        UpdateCaptionCornerRadius();
        SyncZoomButton();
        UpdateSizingLayout();
        if(WindowState==WindowState.Normal)Place();
    }
    // 最大化窗口是直角，标题栏最右侧按钮的圆角必须归零；还原后跟随皮肤圆角。
    internal void UpdateCaptionCornerRadius()
    {
        var radius=WindowState==WindowState.Maximized?0d:Surface.CornerRadius.TopLeft;
        // Surface 位于 Viewbox 内：ApplyWindowRegion 裁 HWND 用的是 radius*scale，
        // 关闭按钮红色悬停块的右上角必须取同一个值，否则缩放窗口后会被 HRGN 裁出缺角。
        var scale=1d;
        try
        {
            var transform=Surface.TransformToAncestor(this);
            var a=transform.Transform(new Point(0,0));
            var b=transform.Transform(new Point(1,0));
            scale=Math.Abs(b.X-a.X);
            if(scale<=0)scale=1;
        }
        catch{/* 视觉树尚未完成布局时用 1，与构造阶段行为一致 */}
        Application.Current.Resources["WinCaptionCornerRadius"]=new CornerRadius(0,radius*scale,0,0);
    }
    // 方框按钮在「最大化」与「向下还原」之间切换图标、提示与自动化名称。
    private void SyncZoomButton()
    {
        if(ZoomButton is null)return;
        var maximized=WindowState==WindowState.Maximized;
        ZoomGlyph.Style=(Style)FindResource(maximized?"WinGlyphRestore":"WinGlyphMaximize");
        ZoomButton.ToolTip=maximized?"向下还原":"最大化";
        AutomationProperties.SetName(ZoomButton,maximized?"向下还原":"最大化");
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    private static bool PointerPressed()=>(GetAsyncKeyState(0x01)&0x8000)!=0;
    private void GuardHide(int milliseconds)
    {
        var until=DateTime.UtcNow.AddMilliseconds(milliseconds);
        if(until>_hideGuardUntil)_hideGuardUntil=until;
    }
    private void OnDeactivated(object? sender,EventArgs e)
    {
        ResetCaptionState();
        // 偏好默认关闭：失焦不自动收起，面板保持显示，只能由 × / 热键 / 任务栏按钮手动收起。
        if(!Settings.AutoHideOnDeactivate)return;
        if(TaskbarPlacement.PointerOverButton())return;
        Dispatcher.BeginInvoke(new Action(TryAutoHide),System.Windows.Threading.DispatcherPriority.Background);
    }
    private void TryAutoHide()
    {
        // 已收进托盘时窗口不可见，不能再把 WindowState 改成 Minimized（恢复时会出现空白帧）。
        if(KeepOpenForVerification||!IsVisible||IsActive||WindowState==WindowState.Minimized)return;
        // 拖出图标时 _dragging 会在 DoDragDrop 返回后立刻置回 false，但失活检查是排队执行的；
        // 保护期 + 拖放事件持续刷新，覆盖拖放全过程与结束后的空档。
        if(_interactions>0||_dragging||_menus>0||DateTime.UtcNow<_hideGuardUntil)return;
        // 左键按住通常表示正从桌面/资源管理器拖图标过来，先别收起面板，稍后复查；
        // 拖进窗口后 DragOver 会不断刷新保护期，面板就一直保持显示。
        if(PointerPressed())
        {
            GuardHide(600);
            var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(700)};
            timer.Tick+=(_,_)=>{timer.Stop();TryAutoHide();};
            timer.Start();
            return;
        }
        WindowState=WindowState.Minimized;
    }
    private void OnClosing(object? sender,CancelEventArgs e)
    {
        if(_allowExit){HotKeyService.Unregister();return;}e.Cancel=true;
        if(_dragging||_interactions>0)return;
        // 默认行为：× 只隐藏窗口并在托盘后台驻留，进程不退出；只有用户显式打开
        // 「设置 → 行为 → 关闭时结束进程」才走真正退出。两条退出入口都必须经
        // 过 Quit() 的进程级硬退出，不能只用 Application.Shutdown()。
        if(App.Options.CloseEndsProcess){_allowExit=true;e.Cancel=false;Dispatcher.BeginInvoke(new Action(Quit));}
        else HideToTray();
    }
    // 关闭按钮：调用 Close() 让 OnClosing 统一裁决（拖拽/交互中则完全不响应）。
    private void OnCloseButton(object sender,RoutedEventArgs e){ResetCaptionState();Close();}
    private void OnMinimizeButton(object sender,RoutedEventArgs e){ResetCaptionState();WindowState=WindowState.Minimized;}
    // 标题栏方框按钮：点击直接最大化，再点还原，不再弹出尺寸选择菜单。
    // 尺寸预设、锁定宽高比与「贴回任务栏」已迁入「设置 → 窗口调整」。
    internal void OnZoomButton(object sender,RoutedEventArgs e)
    {
        ResetCaptionState();
        WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;
    }
    // 真正的退出：托盘右键「退出并结束进程」、设置页「关于 → 退出并结束进程」、
    // 「更多 → 退出并结束进程」、关闭偏好、安装器维护命令与系统注销都汇聚到这里。
    // 之前只调 Application.Shutdown()，在托盘常驻状态下会出现「窗口没了、进程还在」
    // 的僵尸实例（无顶层窗口但继续锁住程序集），因此改成清理后直接 Environment.Exit。
    public void Quit()
    {
        if(_exitRequested)return;
        _exitRequested=true;_allowExit=true;
        // 收起可能正在显示的托盘菜单并卸载全局鼠标钩子。
        try{_tray?.CloseMenu();}catch{}
        HotKeyService.Unregister();
        // 停掉轮询计时器，避免退出瞬间还有一轮扫描在写数据。
        try{_autoCollect?.SetEnabled(false);}catch{}
        // 托盘图标必须先显式释放，否则硬退出后通知区会残留幽灵图标；
        // 数据目录里的收纳状态每次操作都已同步落盘（WriteThrough + Flush），无需再存盘。
        var tray=_tray;_tray=null;
        try{tray?.Dispose();}catch{}
        // 注销 IPC 等待、释放单实例互斥体、删除 instance.pid，让后续启动/安装立刻可用。
        try{(Application.Current as App)?.CleanupIpc();}catch{}
        AppExit.Exit(0);
    }
    // 安装器维护命令要求程序一定结束，不能再因拖拽中而静默忽略停止信号。
    internal void QuitForMaintenance()=>Quit();
    public void SetStatus(string message) { Status.Text=message;Status.ToolTip=message; }
    public void Render()
    {
        if(_activeCategory is not null && !Store.State.Categories.Any(c=>c.Id==_activeCategory)) _activeCategory=null;
        Dispatcher.BeginInvoke(UpdateSizingLayout,System.Windows.Threading.DispatcherPriority.Loaded);
        SyncTabs();
        UpdateTabSelection();
        RefreshIcons();
        ScheduleNavSync();
    }
    // 数据变更后合并成一次资源管理器入口同步，收纳 / 移回 / 改名 / 分类变化都能跟上。
    private DispatcherTimer? _navSyncTimer;
    private void ScheduleNavSync()
    {
        if(_navSyncTimer is null)
        {
            _navSyncTimer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(400)};
            _navSyncTimer.Tick+=(_,_)=>
            {
                _navSyncTimer.Stop();
                try{ExplorerNavService.Sync(Store.DataDirectory,Store);}catch{}
            };
        }
        _navSyncTimer.Stop();_navSyncTimer.Start();
    }
    // 侧边栏选中的分类始终作为右侧内容标题；搜索只过滤显示，不改变收纳数据。
    // 图标一律按 ItemsSource 顺序交给 UniformGrid 排成紧凑网格，不再保存自由坐标。
    private void RefreshIcons()
    {
        var name=_activeCategory is null?"全部":Store.State.Categories.FirstOrDefault(c=>c.Id==_activeCategory)?.Name??"全部";
        var query=_iconFilter;
        var visible=Store.State.Items
            .Where(i=>_activeCategory is null||i.CategoryId==_activeCategory)
            .Where(i=>query.Length==0||i.Name.Contains(query,StringComparison.OrdinalIgnoreCase))
            .Select(item=>new IconEntry(item,item.Kind=="recycle-bin"?"":Store.ManagedPath(item),(Store.State.Categories.FirstOrDefault(c=>c.Id==item.CategoryId)?.Name??"未分类")+" · 快捷方式",_brokenIds.Contains(item.Id))).ToList();
        Icons.ItemsSource=visible;
        EmptyState.Visibility=visible.Count==0?Visibility.Visible:Visibility.Collapsed;
        EmptyText.Text=query.Length>0?"没有匹配的图标":_activeCategory is null?"把桌面图标，喂给番茄":"此分类还没有图标";
        CategoryTitle.Text=name=="全部"?"全部图标":name;
        // 开始菜单「已固定」分组：当前分类由页头大标题说明，这里保持统一的分组名。
        SectionTitle.Text="已固定";
        CategoryBadge.Text=visible.Count+" 个";
        InfoText.Text="已收纳 "+visible.Count+" 个图标。拖到左侧分类行可直接归类，松手后自动吸附回网格。";
        if(!_catalogVisible&&!_settingsVisible)Summary.Text=visible.Count+" 个项目";
        RemoveAllButton.IsEnabled=visible.Count>0;
    }
    // 「全部移除」：一个按钮把当前显示的图标（当前分类 + 搜索结果）全部移回桌面。
    // 自检可用 confirm:false 走同一条数据路径，不弹确认框。
    private void OnRemoveAll(object sender,RoutedEventArgs e)=>RemoveVisible(true);
    internal int RemoveVisible(bool confirm)
    {
        var ids=(Icons.ItemsSource as IEnumerable<IconEntry>)?.Select(x=>x.Item.Id).ToList()??[];
        if(ids.Count==0)return 0;
        if(confirm&&UiDialogs.Show(this,"将当前显示的 "+ids.Count+" 个图标全部移回桌面？\n原桌面入口会恢复，可按 Ctrl+Z 撤销。","全部移除",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return 0;
        _interactions++;
        try
        {
            // 先取 Id 快照：RestoreItem 每次都会 Render，ItemsSource 会在循环中被重建。
            var failed=new List<string>();int done=0;
            foreach(var id in ids)
            {
                var name=Store.State.Items.FirstOrDefault(i=>i.Id==id)?.Name??"未命名";
                if(RestoreItem(id,silent:true))done++;else failed.Add(name);
            }
            Render();
            SetStatus("已移回桌面 "+done+" 个"+(failed.Count>0?" · 失败 "+failed.Count+" 个":""));
            if(failed.Count>0)UiDialogs.Show(this,"这些图标仍留在番茄桌面：\n"+string.Join("\n",failed),"全部移除",MessageBoxButton.OK,MessageBoxImage.Information);
            return done;
        }
        finally{_interactions--;}
    }
    // 右侧工具条搜索框：按显示名称实时过滤当前分类，清空关键词即恢复全部。
    private void OnIconSearch(object sender,TextChangedEventArgs e)
    {
        if(IconSearch is null||CategoryTitle is null)return;
        _iconFilter=IconSearch.Text.Trim();
        RefreshIcons();
    }
    // 侧栏「查找分类」：只过滤分类行显示，"全部" 始终保留；清空 Tabs 强制 SyncTabs 重建分组。
    private void OnCategorySearch(object sender,TextChangedEventArgs e)
    {
        if(CategorySearch is null||Tabs is null)return;
        var next=CategorySearch.Text.Trim();
        if(string.Equals(next,_categoryFilter,StringComparison.Ordinal))return;
        _categoryFilter=next;Tabs.Children.Clear();Render();
    }
    private void OnInfoDismiss(object sender,RoutedEventArgs e)
    {
        // 关闭提示条是持久偏好，重开面板不再出现；设置页里可随时打开。
        InfoBar.Visibility=Visibility.Collapsed;
        Settings=Settings with{InfoBarDismissed=true};App.Options.WriteSettings(Settings);
        if(_settingsLoaded)RefreshSettingsControls();
    }
    private void OnInfoMore(object sender,RoutedEventArgs e)=>SetStatus("拖动图标可调整顺序；拖到左侧分类行可直接归类");
    // 搜索框回车直接启动第一个匹配项，让面板同时充当轻量启动器。
    private void OnIconSearchKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key!=Key.Enter)return;
        e.Handled=true;
        if(Icons.Items.Cast<IconEntry>().FirstOrDefault() is not IconEntry first){SetStatus("没有匹配的图标");return;}
        LaunchEntry(first);
    }
    private void LaunchEntry(IconEntry entry)
    {
        if(_dragging)return;
        Run(()=>{ShellIntegration.Launch(Store,entry.Item);if(!KeepOpenForVerification)WindowState=WindowState.Minimized;});
        if(!KeepOpenForVerification)IconSearch.Text="";
    }
    private void OnUndoButton(object sender,RoutedEventArgs e)=>UndoLast();
    private void UndoLast()
    {
        var message=_undo?.Undo()??"没有可撤销的操作";
        Render();ShellIntegration.RefreshDesktop();SetStatus(message);
        if(_settingsLoaded)RefreshSettingsControls();
    }
    private void RecordUndo(string text,Action rollback)=>_undo?.Record(text,rollback);
    // 批量撤销：按 Id 逐个移回桌面；同名目标自动改名，不覆盖已有文件。
    private void RestoreByIds(IEnumerable<string> ids)
    {
        foreach(var id in ids)if(Store.State.Items.Any(i=>i.Id==id))Store.Restore(id,true);
    }
    // 拖入前把将要发生的事说清楚：待收纳数量、跳过项与失效快捷方式。
    private string ImportPreview(IDataObject data)
    {
        if(data.GetDataPresent(ItemFormat))return "松开即可把图标归入此分类";
        if(data.GetDataPresent(DataFormats.FileDrop)&&data.GetData(DataFormats.FileDrop) is string[] files&&files.Length>0)
        {
            var fresh=files.Count(f=>!Store.State.Items.Any(i=>string.Equals(i.OriginalPath,f,StringComparison.OrdinalIgnoreCase)));
            var skipped=files.Length-fresh;
            var broken=files.Count(ShortcutHealth.IsBrokenShortcut);
            return "将收纳 "+fresh+" 项"+(skipped>0?" · 已收纳 "+skipped+" 项跳过":"")+(broken>0?" · 失效快捷方式 "+broken+" 项":"")+"，松开确认";
        }
        if(ShellIntegration.ContainsRecycleBin(data))return "松开即可收纳系统回收站（不会清空内容）";
        return "松开即可放入番茄桌面";
    }
    // 分类行实例保持稳定：只有分类增删改/改名时才重建整个侧边栏；切换分类只更新颜色，
    // 不重建按钮，避免点击瞬间悬停/焦点态丢失、列表闪动。
    private void SyncTabs()
    {
        var desired=new List<(string Name,string? Id)>{("全部",null)};
        foreach(var category in Store.State.Categories.Where(c=>_categoryFilter.Length==0||c.Name.Contains(_categoryFilter,StringComparison.OrdinalIgnoreCase)))desired.Add((category.Name,category.Id));
        var buttons=Tabs.Children.OfType<Button>().ToList();
        var reusable=buttons.Count==desired.Count;
        for(var i=0;reusable&&i<desired.Count;i++)
            reusable=string.Equals((string?)buttons[i].Tag,desired[i].Id,StringComparison.Ordinal)
                     &&string.Equals(buttons[i].Content as string,desired[i].Name,StringComparison.Ordinal);
        if(reusable)return;
        Tabs.Children.Clear();
        // WinUI NavigationView 分组：桌面放「全部」，分类组标题右侧带新建按钮。
        Tabs.Children.Add(SectionHeader("桌面",false));
        AddTab(desired[0].Name,desired[0].Id);
        Tabs.Children.Add(SectionHeader("分类",true));
        foreach(var (name,id) in desired.Skip(1))AddTab(name,id);
    }
    // 分组标题是普通文本，不会被 Tabs.Children.OfType<Button>() 计入分类行。
    // 设计稿的 NavigationView 分组标题只有文字，不放按钮；
    // 「新建分类」迁到「更多」菜单，并在分类行右键菜单里同样可用。
    private FrameworkElement SectionHeader(string text,bool withAdd)
        =>new TextBlock{Text=text,FontSize=11,FontWeight=FontWeights.SemiBold,Foreground=Res("WinTextSecondary"),Margin=new Thickness(12,12,12,6),VerticalAlignment=VerticalAlignment.Center};
    // 选中态由按钮本地值给出：中性浅灰底 + 深色字，不使用蓝色高亮；样式模板只负责
    // 固定尺寸与描边。不改字体、不改 Padding，因此行宽恒定，切换分类时侧边栏不会跳动。
    private void UpdateTabSelection()
    {
        foreach(var tab in Tabs.Children.OfType<Button>())
        {
            var active=!_settingsVisible&&string.Equals(tab.Tag as string,_activeCategory,StringComparison.Ordinal);
            tab.Background=active?Res("WinHover"):Brushes.Transparent;
            tab.BorderBrush=Brushes.Transparent;
            tab.Foreground=active?Res("WinText"):Res("WinTextSecondary");
            tab.Uid=active?"selected":"";
            var categoryId=tab.Tag as string;
            SetNavCount(tab,Store.State.Items.Count(i=>categoryId is null||i.CategoryId==categoryId).ToString());
        }
        // 设置页覆盖右侧内容区时，侧栏选中态跟着移到「设置」行，避免出现「页面切了但导航没跟着切」。
        SettingsNavButton.Background=_settingsVisible?Res("WinHover"):Brushes.Transparent;
        SettingsNavButton.BorderBrush=Brushes.Transparent;
        SettingsNavButton.Foreground=_settingsVisible?Res("WinText"):Res("WinTextSecondary");
        SettingsNavButton.Uid=_settingsVisible?"selected":"";
    }
    private void AddTab(string name,string? id)
    {
        var button=new Button { Content=name,Tag=id,ToolTip=id is null?"显示所有图标":name+" · 拖动可调整顺序，右键可管理",Style=(Style)Application.Current.FindResource("SidebarRow") };
        AutomationProperties.SetAutomationId(button,"Category-"+(id??"all"));
        // 拖动结束后短时间内不触发 Click，避免排完序顺手把视图切走。
        button.Click+=(_,_)=>{if(DateTime.UtcNow-_categoryDragEnd<TimeSpan.FromMilliseconds(300))return;ShowIconsView();SelectCategory(id);};
        button.MouseRightButtonUp+=(_,e)=>{e.Handled=true;if(id is not null) CategoryMenu(button,id);};
        // 「全部」与「未分类」不参与拖动；其余分类行按住移动超过 5px 即发起排序拖放。
        if(id is not null&&id!="uncategorized")
        {
            button.PreviewMouseLeftButtonDown+=(_,e)=>_categoryDragStart=e.GetPosition(null);
            button.PreviewMouseLeftButtonUp+=(_,_)=>_categoryDragStart=null;
            button.PreviewMouseMove+=(_,e)=>
            {
                if(_categoryDragStart is not Point start||e.LeftButton!=MouseButtonState.Pressed)return;
                var point=e.GetPosition(null);
                if(Math.Abs(point.X-start.X)<5&&Math.Abs(point.Y-start.Y)<5)return;
                _categoryDragStart=null;
                try{DragDrop.DoDragDrop(button,new DataObject(CategoryDragFormat,id!),DragDropEffects.Move);}
                finally{_categoryDragEnd=DateTime.UtcNow;}
                e.Handled=true;
            };
        }
        button.AllowDrop=true;
        button.DragOver+=(_,e)=>
        {
            if(e.Data.GetDataPresent(CategoryDragFormat))
            {
                var dragId=e.Data.GetData(CategoryDragFormat) as string;
                // 目标行可以是「未分类」：语义是插到它之前，即自定义分类的末尾。
                var canDrop=id is not null&&dragId is not null&&dragId!=id;
                e.Effects=canDrop?DragDropEffects.Move:DragDropEffects.None;
                button.Background=canDrop?Res("WinHover"):Brushes.Transparent;
                e.Handled=true;
                return;
            }
            HandleDragOver(e);
        };
        button.DragLeave+=(_,_)=>UpdateTabSelection();
        button.Drop+=(_,e)=>
        {
            if(e.Data.GetDataPresent(CategoryDragFormat))
            {
                HandleCategoryDrop(e.Data.GetData(CategoryDragFormat) as string,id);
                UpdateTabSelection();e.Handled=true;return;
            }
            HandleDrop(e,id);
        };
        // 侧边栏是纵向列表：上下键（兼容左右键）在分类行之间循环，Home/End 跳到首尾。
        button.PreviewKeyDown+=(_,e)=>
        {
            if(e.Key is not (Key.Up or Key.Left or Key.Down or Key.Right or Key.Home or Key.End))return;
            var buttons=Tabs.Children.OfType<Button>().ToList();
            var current=buttons.IndexOf(button);
            var back=e.Key is Key.Up or Key.Left;
            var next=e.Key==Key.Home?0:e.Key==Key.End?buttons.Count-1:(current+(back?-1:1)+buttons.Count)%buttons.Count;
            ShowIconsView();SelectCategory(buttons[next].Tag as string);buttons[next].Focus();e.Handled=true;
        };
        Tabs.Children.Add(button);
    }
    public void SelectCategory(string? id) { _activeCategory=id;Render(); }
    // 把拖动的分类插到目标行之前；顺序写入 state.json，撤销一次性恢复原顺序。
    internal void HandleCategoryDrop(string? dragId,string? targetId)
    {
        if(dragId is null||targetId is null||dragId==targetId||dragId=="uncategorized")return;
        var before=Store.State.Categories.Select(c=>c.Id).ToList();
        var from=before.IndexOf(dragId);
        var originalTarget=before.IndexOf(targetId);
        if(from<0||originalTarget<0)return;
        var after=before.ToList();
        after.RemoveAt(from);
        var target=after.IndexOf(targetId);
        // 往下拖排到目标之后、往上拖排到目标之前，等价于「拖动项占据目标行的位置」；
        // 纯「插到目标之前」会让从上往下拖变成空操作。
        after.Insert(from<originalTarget?Math.Min(target+1,after.Count):target,dragId);
        if(before.SequenceEqual(after))return;
        if(Run(()=>Store.SetCategoryOrder(after),"分类顺序已更新"))
            RecordUndo("调整分类顺序",()=>{Store.SetCategoryOrder(before);Render();});
    }
    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private bool Run(Action action,string? message=null)
    {
        try { action();Render();if(message is not null)SetStatus(message);return true; }
        catch(Exception ex) { ShowFailure(ex);return false; }
    }
    // 系统级异常（权限、占用等）默认只有英文说明，转成中文并给出可操作的下一步。
    internal static string DescribeError(Exception exception)
    {
        var message=exception.Message??"操作未完成。";
        var denied=exception is UnauthorizedAccessException
            || (exception.HResult&0xffff)==5
            || message.Contains("Access to the path",StringComparison.OrdinalIgnoreCase)
            || message.Contains("is denied",StringComparison.OrdinalIgnoreCase);
        if(denied)return "没有权限访问该位置：\n"+message
            +"\n\n若这是「所有用户」共用的公共桌面快捷方式，程序会自动把图标移回当前用户桌面；也可手动删除原快捷方式，或以管理员身份运行后重试。原文件与收纳数据均未改动。";
        if(exception is IOException && (message.Contains("being used by another process",StringComparison.OrdinalIgnoreCase)||message.Contains("正由另一进程使用",StringComparison.OrdinalIgnoreCase)))
            return "文件正被其它程序占用，请关闭占用它的窗口后重试。原文件与收纳数据均未改动。\n\n"+message;
        return message;
    }
    public void ShowFailure(Exception exception)
    {
        try { App.ReloadStore();Render(); } catch(Exception recovery) { SetStatus("恢复未完成："+recovery.Message); }
        _interactions++;
        try { UiDialogs.Show(this,DescribeError(exception),"番茄桌面",MessageBoxButton.OK,MessageBoxImage.Information); }
        finally { _interactions--; }
    }
    private void OnCollect(object sender,RoutedEventArgs e)
    {
        ShowIconsView();
        _interactions++;
        try
        {
            var picker=new CollectWindow(Store,_activeCategory??"uncategorized"){Owner=this};
            if(picker.ShowDialog()!=true) return;
            var errors=new List<string>();var completed=0;var collected=new List<string>();
            foreach(var entry in picker.Selection)
            {
                try { collected.Add(entry.Recycle?Store.CollectRecycleBin(picker.CategoryId).Id:Store.Collect(entry.Path,picker.CategoryId).Id);completed++; }
                catch(Exception ex) { errors.Add(entry.Name+"："+DescribeError(ex));try{App.ReloadStore();}catch{break;} }
            }
            if(collected.Count>0)RecordUndo("收纳 "+collected.Count+" 个图标",()=>RestoreByIds(collected));
            _activeCategory=picker.CategoryId;Render();ShellIntegration.RefreshDesktop();
            SetStatus("已喂给番茄 "+completed+" 个图标"+(errors.Count>0?" · 失败 "+errors.Count+" 项":""));
            if(errors.Count>0) UiDialogs.Show(this,string.Join("\n",errors),"部分图标未完成收纳",MessageBoxButton.OK,MessageBoxImage.Information);
        }
        finally {_interactions--;}
    }
    private void OnNewCategory(object sender,RoutedEventArgs e)
    {
        var name=Ask("新建分类","分类名称","");
        if(name is null)return;
        var clean=name.Trim();
        if(!Run(()=>{Store.AddCategory(clean);_activeCategory=Store.State.Categories.Single(c=>c.Name==clean).Id;},"分类已创建"))return;
        RecordUndo("新建分类："+clean,()=>Store.DeleteCategory(Store.State.Categories.Single(c=>c.Name==clean).Id));
    }
    private string? Ask(string title,string label,string initial)
    {
        _interactions++;try{var dialog=new TextDialog(title,label,initial){Owner=this};return dialog.ShowDialog()==true?dialog.Value:null;}finally{_interactions--;}
    }
    // 主界面内容由 Viewbox 按 Uniform 拉伸（默认 1:1）；菜单是独立 Popup，
    // 不在该视觉树内，需要按同一比例缩放，才不会比窗口里的文字、按钮大一圈。
    private double MenuScale()
    {
        double width=ActualWidth>0?ActualWidth:Width, height=ActualHeight>0?ActualHeight:Height;
        double surfaceWidth=Surface.Width>0?Surface.Width:1040, surfaceHeight=Surface.Height>0?Surface.Height:680;
        return Math.Clamp(Math.Min(width/surfaceWidth,height/surfaceHeight),.8,1.4);
    }
    private void Popup(FrameworkElement owner,IEnumerable<object> entries,Action? closed=null)
    {
        var scale=MenuScale();
        var menu=new ContextMenu { FontSize=11,PlacementTarget=owner,LayoutTransform=new ScaleTransform(scale,scale) };
        LastMenu=menu;
        foreach(var entry in entries)
        {
            // 从子菜单里取出的项仍挂在原 ItemsControl 上，直接 Add 会抛
            // 「元素已具有逻辑父级」（设置页下拉复用菜单项时可能走这条路径）。
            if(entry is FrameworkElement fe&&LogicalTreeHelper.GetParent(fe) is ItemsControl oldParent&&oldParent.Items.Contains(entry))
                oldParent.Items.Remove(entry);
            menu.Items.Add(entry);
        }
        menu.Opened+=(_,_)=>_menus++;
        menu.Closed+=(_,_)=>{_menus=Math.Max(0,_menus-1);if(closed is not null)Dispatcher.BeginInvoke(closed,System.Windows.Threading.DispatcherPriority.ContextIdle);};
        menu.IsOpen=true;
    }
    private static MenuItem Menu(string name,Action action,bool enabled=true,object? icon=null) {var item=new MenuItem{Header=name,IsEnabled=enabled,Icon=icon};item.Click+=(_,_)=>action();return item;}
    // 分组小标题用 Tag 触发 App.xaml 里的专用外观，本身不接受点击与键盘焦点。
    private static object Group(string text)=>new MenuItem{Header=text,Tag="GroupHeader",IsHitTestVisible=false,Focusable=false,IsTabStop=false};
    // Windows Shell 菜单在不同机器上项目极多，只把几个通用动作留在主菜单，其余收进子菜单。
    // Tag 里是 Shell 规范动词，跨语言可靠；没有动词的扩展项再按名称精确匹配，
    // 不能用 Contains("打开")，否则「打开方式」「用手机打开」会挤掉「以管理员身份运行」。
    private static bool IsPrimaryShellAction(MenuItem mi)
    {
        var verb=(mi.Tag as string??"").ToLowerInvariant();
        if(verb is "open" or "runas" or "properties" or "opencontainingfolder")return true;
        return (mi.Header as string)?.Trim().ToLowerInvariant() switch
        {
            "打开" or "open" or "打开文件所在的位置" or "打开文件所在位置" or "open file location"
            or "以管理员身份运行" or "run as administrator" or "属性" or "properties"=>true,
            _=>false
        };
    }
    private void CategoryMenu(FrameworkElement owner,string id)
    {
        var category=Store.State.Categories.Single(c=>c.Id==id);var index=Store.State.Categories.IndexOf(category);
        var items=new List<MenuItem>{Menu("喂进此分类",()=>{_activeCategory=id;OnCollect(this,new());})};
        items.Add(Menu("新建分类",()=>OnNewCategory(this,new RoutedEventArgs())));
        if(id!="uncategorized")items.AddRange([
            Menu("重命名",()=>{
                var name=Ask("重命名分类","分类名称",category.Name);
                if(name is null)return;
                var oldName=category.Name;
                if(Run(()=>Store.RenameCategory(id,name),"分类已更新"))RecordUndo("重命名分类",()=>Store.RenameCategory(id,oldName));
            }),
            Menu("向前移动",()=>{if(Run(()=>Store.OrderCategory(id,-1)))RecordUndo("调整分类顺序",()=>Store.OrderCategory(id,1));},index>0),
            Menu("向后移动",()=>{if(Run(()=>Store.OrderCategory(id,1)))RecordUndo("调整分类顺序",()=>Store.OrderCategory(id,-1));},index<Store.State.Categories.Count-2),
            Menu("删除分类",()=>{
                _interactions++;
                try
                {
                    if(UiDialogs.Show(this,"删除“"+category.Name+"”？其中的图标将移入未分类，不会移回桌面。","番茄桌面",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;
                    var oldName=category.Name;
                    var itemIds=Store.State.Items.Where(i=>i.CategoryId==id).Select(i=>i.Id).ToList();
                    if(!Run(()=>Store.DeleteCategory(id),"分类已删除"))return;
                    RecordUndo("删除分类："+oldName,()=>
                    {
                        Store.AddCategory(oldName);
                        var newId=Store.State.Categories.Single(c=>c.Name==oldName).Id;
                        foreach(var itemId in itemIds)if(Store.State.Items.Any(i=>i.Id==itemId))Store.MoveItem(itemId,newId);
                    });
                }
                finally{_interactions--;}
            })
        ]);
        Popup(owner,items);
    }
    private void OnMore(object sender,RoutedEventArgs e)=>Popup((FrameworkElement)sender,MoreMenuEntries());
    // 菜单内容单独暴露给自检：自启、关闭行为、主题、热键、数据备份已迁到设置页，
    // 「文件管理」子菜单移除（打开数据目录仍在设置页），「窗口大小」整体迁入「设置 → 窗口调整」。
    // 这里只留页面入口与高级操作。
    internal List<object> MoreMenuEntries()=>[
        // 页面级入口统一收到菜单顶部：不再占用分类导航条，功能与快捷键均保留。
        Menu("新建分类",()=>OnNewCategory(this,new RoutedEventArgs())),
        Menu("浏览全部图标…",ShowCatalogView),
        Menu("打开设置",()=>OnSettingsToggle(this,new RoutedEventArgs())),
        Menu("撤销上一步（Ctrl+Z）",UndoLast),
        Menu("检查失效入口",()=>OnCleanBroken(this,new RoutedEventArgs())),
        new Separator(),
        Menu(UpdateMenuText,()=>_ = CheckOnlineUpdateAsync(true),OnlineUpdater.Enabled),
        Menu("从本地文件更新…",()=>_ = SelectUpdateAsync()),
        Menu("全部移回桌面",RestoreAll,Store.State.Items.Count>0),
        Menu("创建桌面与开始菜单入口",()=>Run(()=>ShortcutInstaller.InstallAll(),"已创建桌面与开始菜单入口，可右键固定到任务栏")),
        Menu("如何固定到任务栏",()=>{_interactions++;try{UiDialogs.Show(this,"右键任务栏上的“番茄桌面”图标，选择“固定到任务栏”。\n\n固定后点击同一个入口即可打开或收起番茄桌面。","番茄桌面");}finally{_interactions--;}}),
        Menu("打开数据目录",()=>OpenExplorer(Store.DataDirectory)),
        new Separator(),Menu("退出并结束进程",Quit)
    ];
    private async Task SelectUpdateAsync()
    {
        _interactions++;
        try
        {
            var picker=new Microsoft.Win32.OpenFileDialog{Title="选择番茄桌面新版本",Filter="番茄桌面程序或安装包 (*.exe)|*.exe",CheckFileExists=true};if(picker.ShowDialog(this)!=true)return;
            var source=picker.FileName;var target=Environment.ProcessPath!;var candidate=await Task.Run(()=>LocalUpdater.Inspect(source));
            // 统一的更新窗口：版本迁移 + 校验说明 + 「开始更新 / 取消」，不再用通用文本弹窗。
            if(!UpdateWindow.ConfirmLocal(this,CurrentVersionLabel,"v"+candidate.Version.ToString(3)))return;
            SetStatus("正在准备更新…");var staged=await Task.Run(()=>LocalUpdater.Stage(source,target,Store.DataDirectory,App.IsSandbox?Path.GetDirectoryName(Store.DataDirectory):null));
            var helper=new ProcessStartInfo(staged.Helper){UseShellExecute=false,CreateNoWindow=true};helper.ArgumentList.Add("--apply-update");helper.ArgumentList.Add(staged.Plan);Process.Start(helper);Quit();
        }
        catch(Exception ex){ShowFailure(ex);}
        finally{_interactions--;}
    }
    private async Task CheckOnlineUpdateAsync(bool manual)
    {
        if(!OnlineUpdater.Enabled)
        {
            if(manual)
            {
                SetStatus("在线更新未启用，可使用“从本地文件更新”");
                UpdateWindow.Notice(this,"在线更新","暂时无法在线更新","尚未配置发布仓库地址。","可以使用「更多 → 从本地文件更新…」。",()=>_ = SelectUpdateAsync());
            }
            return;
        }
        if(_onlineBusy){if(manual)SetStatus("正在检查或下载更新，请稍候…");return;}
        _onlineBusy=true;if(manual)_interactions++;
        try
        {
            var assemblyVersion=System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!;
            if(!manual)
            {
                // 后台检查只提示、不弹窗、不自动安装。
                using var background=OnlineUpdater.CreateClient();
                _latestRelease=await OnlineUpdater.CheckAsync(background,assemblyVersion);
                RefreshVersionBadge();
                if(_latestRelease is not null)SetStatus($"发现新版本 {_latestRelease.Version.ToString(3)}，可在更多菜单中更新");
                return;
            }
            SetStatus($"正在检查更新（当前 {CurrentVersionLabel}）…");
            using var client=OnlineUpdater.CreateClient();
            // 一个窗口走完「检查 → 确认 → 下载 → 校验 → 等待重启」；返回用户确认重启后的下载文件。
            var result=UpdateWindow.RunOnline(this,CurrentVersionLabel,Store.DataDirectory,
                ()=>OnlineUpdater.CheckAsync(client,assemblyVersion),SetStatus,()=>_ = SelectUpdateAsync());
            if(result.Release is not null)_latestRelease=result.Release;
            RefreshVersionBadge();
            if(result.File is null)return;
            SetStatus("正在准备更新…");
            var staged=await Task.Run(()=>LocalUpdater.Stage(result.File!,Environment.ProcessPath!,Store.DataDirectory,App.IsSandbox?Path.GetDirectoryName(Store.DataDirectory):null));
            using(var plan=System.Text.Json.JsonDocument.Parse(File.ReadAllText(staged.Plan)))
                if(!plan.RootElement.GetProperty("CandidateHash").GetString()!.Equals(result.Release!.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("更新文件校验失败，请重新下载。");
            var helper=new ProcessStartInfo(staged.Helper){UseShellExecute=false,CreateNoWindow=true};helper.ArgumentList.Add("--apply-update");helper.ArgumentList.Add(staged.Plan);
            if(Process.Start(helper) is null)throw new IOException("无法启动更新助手。");Quit();
        }
        catch(Exception)
        {
            if(manual)
            {
                SetStatus("更新检查未完成");
                UpdateWindow.Notice(this,"在线更新","暂时无法完成在线更新","请检查网络或系统代理后重试。","也可以使用「更多 → 从本地文件更新…」。",()=>_ = SelectUpdateAsync(),"关闭","error");
            }
        }
        finally{_onlineBusy=false;if(manual)_interactions--;}
    }
    private void RestoreAll()
    {
        _interactions++;
        try
        {
            if(UiDialogs.Show(this,"将 "+Store.State.Items.Count+" 个图标全部移回桌面？","番茄桌面",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;
            var restored=0;var remaining=new List<string>();
            foreach(var item in Store.State.Items.ToArray()) {if(RestoreItem(item.Id,silent:true))restored++;else remaining.Add(item.Name);}
            SetStatus("已移回 "+restored+" 个，剩余 "+remaining.Count+" 个");
            if(remaining.Count>0)UiDialogs.Show(this,"这些图标还留在番茄里，可稍后重试：\n"+string.Join("\n",remaining),"恢复结果",MessageBoxButton.OK,MessageBoxImage.Information);
        }
        finally{_interactions--;}
    }
    public bool RestoreItem(string id,bool silent=false)
    {
        var item=Store.State.Items.FirstOrDefault(i=>i.Id==id);
        var categoryId=item?.CategoryId??"uncategorized";
        try
        {
            var renamed=false;string destination;
            try { destination=Store.Restore(id); }
            catch(NameConflictException)
            {
                // 批量恢复不逐项弹确认框，直接自动改名，避免中途卡住。
                if(silent){destination=Store.Restore(id,true);renamed=true;}
                else
                {
                    _interactions++;
                    try
                    {
                        if(UiDialogs.Show(this,"桌面已有同名文件。是否使用新名称恢复？原文件不会被覆盖。","番茄桌面",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return false;
                        destination=Store.Restore(id,true);renamed=true;
                    }
                    finally{_interactions--;}
                }
            }
            if(item is not null){var path=destination;RecordUndo("移回桌面："+item.Name,()=>{if(item.Kind=="recycle-bin")Store.CollectRecycleBin(categoryId);else Store.Collect(path,categoryId);});}
            var relocated=Relocated(item,destination);
            ShellIntegration.RefreshDesktop();Render();
            SetStatus(relocated?"公共桌面不可写，已移回到当前用户桌面":renamed?"已改名移回桌面":"已移回桌面");
            return true;
        }
        catch(Exception ex)
        {
            // 批量恢复失败只记进结果清单，不连续弹出多个错误窗口。
            if(silent)SetStatus(DescribeError(ex).Split('\n')[0].Trim());
            else ShowFailure(ex);
            return false;
        }
    }
    // 原位置是不可写的公共桌面时，NookStore 会自动改放到当前用户桌面；这里给出明确反馈。
    private static bool Relocated(NookItem? item,string destination)
    {
        if(item is null||item.Kind is "app" or "recycle-bin")return false;
        var from=Path.GetDirectoryName(item.OriginalPath);
        var to=Path.GetDirectoryName(destination);
        return from is not null&&to is not null&&!string.Equals(from,to,StringComparison.OrdinalIgnoreCase);
    }
    // 启动统一走 LaunchIcon：鼠标释放（PreviewMouseLeftButtonUp）和 Button.Click 都可能触发，
    // 具体取决于鼠标捕获状态，两条路径都保留，由节流避免重复启动。
    private void OnIconClick(object sender,RoutedEventArgs e)
    {
        if(_dragging || DateTime.UtcNow<_suppressUntil)return;
        if(((Button)sender).Tag is NookItem item)LaunchIcon(item);
    }
    private void LaunchIcon(NookItem item)
    {
        var now=DateTime.UtcNow;
        if(_lastLaunchId==item.Id&&(now-_lastLaunchAt).TotalMilliseconds<600)return;
        _lastLaunchId=item.Id;_lastLaunchAt=now;
        // 延后一帧启动：不在鼠标事件路由中重建图标网格 / 最小化面板，双击的第二次释放不会打到半销毁的控件。
        Dispatcher.BeginInvoke(new Action(()=>
        {
            if(!Run(()=>{ShellIntegration.Launch(Store,item);if(!KeepOpenForVerification)WindowState=WindowState.Minimized;}))
                _lastLaunchId=null; // 启动失败时允许立刻重试
        }),System.Windows.Threading.DispatcherPriority.Input);
    }
    private void OnIconRightClick(object sender,MouseButtonEventArgs e)
    {
        e.Handled=true;var button=(Button)sender;if(button.Tag is not NookItem item)return;
        ShowIconMenu(button,item);
    }
    internal void ShowIconMenu(Button button,NookItem item)
    {
        var siblings=Store.State.Items.Where(i=>i.CategoryId==item.CategoryId).ToList();var index=siblings.FindIndex(i=>i.Id==item.Id);
        var oldCategory=item.CategoryId;var oldName=item.Name;
        var move=new MenuItem{Header="移动到分类"};
        foreach(var cat in Store.State.Categories)move.Items.Add(Menu(cat.Name,()=>
        {
            if(Run(()=>Store.MoveItem(item.Id,cat.Id),"已移动分类"))RecordUndo("移动分类："+item.Name,()=>Store.MoveItem(item.Id,oldCategory));
        }));
        var own=new List<object>{
            Group("番茄桌面"),
            Menu("移回桌面",()=>RestoreItem(item.Id)),move,
            Menu("修改显示名称",()=>Rename()),
            Menu("前移",()=>{if(Run(()=>Store.OrderItem(item.Id,-1)))RecordUndo("调整图标顺序",()=>Store.OrderItem(item.Id,1));},index>0),
            Menu("后移",()=>{if(Run(()=>Store.OrderItem(item.Id,1)))RecordUndo("调整图标顺序",()=>Store.OrderItem(item.Id,-1));},index<siblings.Count-1)
        };
        if(_brokenIds.Contains(item.Id))own.Add(Menu("移除失效入口",()=>ArchiveWithUndo(item)));
        void Rename()
        {
            var name=Ask("显示名称","图标在番茄桌面中的名称",item.Name);
            if(name is null)return;
            if(Run(()=>Store.RenameItem(item.Id,name)))RecordUndo("重命名图标",()=>Store.RenameItem(item.Id,oldName));
        }
        // 归档只从面板移除入口，恢复副本仍在数据目录里，因此可以整体撤销。
        void ArchiveWithUndo(NookItem target)
        {
            var snapshot=target;
            if(Run(()=>Store.ArchiveItem(target.Id),"入口已移除，恢复副本保留"))
                RecordUndo("移除入口："+target.Name,()=>Store.RestoreCatalog([snapshot]));
        }
        try
        {
            var shell=new ShellContextMenu(this,Store,item);
            void Execute(string verb,Action invoke)
            {
                _interactions++;
                try
                {
                    if(verb.Equals("rename",StringComparison.OrdinalIgnoreCase))Rename();
                    else if(verb.Equals("delete",StringComparison.OrdinalIgnoreCase))
                    {if(item.Kind=="recycle-bin")RestoreItem(item.Id);else if(UiDialogs.Show(this,"从番茄桌面移除这个入口？恢复副本会保留，软件不会卸载。","移除入口",MessageBoxButton.OKCancel)==MessageBoxResult.OK)ArchiveWithUndo(item);}
                    else if(verb.Equals("cut",StringComparison.OrdinalIgnoreCase))
                    {if(item.Kind=="recycle-bin"){RestoreItem(item.Id);return;}string? restored=null;if(Run(()=>restored=Store.Restore(item.Id),"已移回桌面并剪切")){var data=new DataObject(DataFormats.FileDrop,new[]{restored!});data.SetData("Preferred DropEffect",new MemoryStream(BitConverter.GetBytes(2)));Clipboard.SetDataObject(data,true);}}
                    else Run(invoke);
                }
                finally{_interactions--;}
            }
            // 自有操作与系统扩展操作分区展示；Shell 扩展常有十几到几十项，
            // 主菜单最多留 4 个常用动作，其余原样收进「更多 Windows 操作」，功能一个不丢。
            var shellItems=shell.Build(Execute);
            own.Add(Group("Windows 菜单"));
            var primary=new List<object>();var extra=new List<object>();
            foreach(var entry in shellItems)
            {
                if(entry is Separator)continue;
                if(entry is MenuItem mi&&IsPrimaryShellAction(mi)&&primary.Count<4)primary.Add(entry);
                else extra.Add(entry);
            }
            own.AddRange(primary);
            if(extra.Count>0)
            {
                var more=new MenuItem{Header="更多 Windows 操作"};
                foreach(var entry in extra)more.Items.Add(entry);
                own.Add(more);
            }
            own.Add(new Separator());
            own.Add(Menu("Windows 完整菜单…",()=>{_interactions++;try{shell.FullMenu(this,Execute);}finally{_interactions--;}}));
            Popup(button,own,shell.Dispose);
        }
        catch(Exception ex){own.Add(Group("Windows 菜单"));own.Add(Menu("打开",()=>Run(()=>ShellIntegration.Launch(Store,item))));SetStatus("系统扩展菜单暂不可用："+ex.Message);Popup(button,own);}
    }
    // 图标自由拖动：鼠标捕获 + RenderTransform 实时跟随，松手后把坐标写回收纳数据。
    // 不再走 OLE DoDragDrop（那样图标本身不动、只会弹出拖放高亮框）。
    // 拖出面板且落在桌面时仍执行「移回桌面」，落在侧边栏分类行时执行归类。
    private void OnIconDown(object sender,MouseButtonEventArgs e)
    {
        if(e.ChangedButton!=MouseButton.Left || sender is not Button button || button.Tag is not NookItem item)return;
        _dragButton=button;_dragItem=item;_dragMoved=false;
        // 用 ItemsControl 的坐标系计算位移，天然抵消 Viewbox 的整体缩放。
        _dragPointer=e.GetPosition(Icons);
        button.CaptureMouse();
    }
    private void OnIconMove(object sender,MouseEventArgs e)
    {
        if(_dragButton is null || e.LeftButton!=MouseButtonState.Pressed)return;
        var current=e.GetPosition(Icons);
        var delta=new Point(current.X-_dragPointer.X,current.Y-_dragPointer.Y);
        if(!_dragMoved)
        {
            if(Math.Abs(delta.X)<SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y)<SystemParameters.MinimumVerticalDragDistance)return;
            _dragMoved=true;_dragging=true;GuardHide(3000);
        }
        var windowPoint=e.GetPosition(this);
        bool outside=windowPoint.X<0||windowPoint.Y<0||windowPoint.X>ActualWidth||windowPoint.Y>ActualHeight;
        // 越过面板边界且目标不是桌面：交给 OLE，浏览器 / 微信等外部程序才能收到文件。
        // 桌面分支继续走 OnIconUp 的「移回桌面」，不改动原有语义。
        if(outside&&!ShellIntegration.IsDesktopUnderCursor()){StartExternalDrag();e.Handled=true;return;}
        _dragButton.RenderTransform=new TranslateTransform(delta.X,delta.Y);
        e.Handled=true;
    }
    // 把内部鼠标手势切换成真正的 OLE 拖放。DoDragDrop 会接管鼠标消息泵，
    // 因此先释放捕获并清空手势状态，OnIconUp 届时不会再处理同一个项目。
    private void StartExternalDrag()
    {
        var button=_dragButton;var item=_dragItem;
        if(button is null||item is null)return;
        DataObject? data;
        try{data=ExternalDrag.Build(item,Store.ManagedPath(item));}
        catch{data=null;}
        _dragButton=null;_dragItem=null;_dragMoved=false;
        button.ReleaseMouseCapture();
        button.RenderTransform=null;
        if(data is null)
        {
            _dragging=false;GuardHide(1500);
            SetStatus("图标仍保留在番茄桌面");
            return;
        }
        _dragging=true;GuardHide(5000);
        var effect=DragDropEffects.None;
        try{effect=DragDrop.DoDragDrop(button,data,ExternalDrag.AllowedEffects);}
        catch{/* 外部程序拒绝拖放时不影响面板 */}
        finally
        {
            _dragging=false;_suppressUntil=DateTime.UtcNow.AddMilliseconds(250);GuardHide(1500);
        }
        SetStatus(effect==DragDropEffects.None?"图标仍保留在番茄桌面":"已提供给其它程序，文件仍由番茄桌面托管");
    }
    private void OnIconUp(object sender,MouseButtonEventArgs e)
    {
        var button=_dragButton;var item=_dragItem;
        if(button is null||item is null)return;
        _dragButton=null;_dragItem=null;
        button.ReleaseMouseCapture();
        // 未超过拖动阈值：直接在鼠标释放时启动。图标做过 CaptureMouse/ReleaseMouseCapture，
        // 该状态下 Button.Click 在部分 WPF 版本里不会触发，不能只依赖 Click。
        if(!_dragMoved){LaunchIcon(item);return;}
        _dragging=false;_suppressUntil=DateTime.UtcNow.AddMilliseconds(250);GuardHide(1500);
        button.RenderTransform=null;
        var local=e.GetPosition(this);
        bool outside=local.X<0||local.Y<0||local.X>ActualWidth||local.Y>ActualHeight;
        if(outside)
        {
            if(ShellIntegration.IsDesktopUnderCursor())RestoreItem(item.Id);
            else SetStatus("图标仍保留在番茄桌面");
            return;
        }
        var (found,categoryId)=CategoryUnderCursor(local);
        if(found){Run(()=>Store.MoveItem(item.Id,categoryId??"uncategorized"),"已移动分类");return;}
        // 搜索状态下可见项只是子集，索引会错位，先要求清空搜索再调整顺序。
        if(_iconFilter.Length>0){SetStatus("清空搜索后可调整图标顺序");return;}
        // 按落点换算网格格子，把图标插到对应顺序位置；UniformGrid 随即紧凑重排。
        var point=e.GetPosition(Icons);
        var columns=Math.Max(1,IconColumns);
        double cellWidth=(Icons.ActualWidth>0?Icons.ActualWidth:Math.Max(200,Surface.Width-212))/columns;
        int col=(int)Math.Clamp(Math.Floor(point.X/cellWidth),0,columns-1);
        int row=Math.Max(0,(int)Math.Floor(point.Y/IconRowHeight));
        int count=Store.State.Items.Count(i=>_activeCategory is null||i.CategoryId==_activeCategory);
        int target=Math.Clamp(row*columns+col,0,Math.Max(0,count-1));
        var oldSiblings=Store.State.Items.Where(i=>i.CategoryId==item.CategoryId).ToList();
        var oldIndex=Math.Max(0,oldSiblings.IndexOf(item));
        if(Run(()=>Store.MoveItemToIndex(item.Id,target),"顺序已调整"))
            RecordUndo("调整图标顺序："+item.Name,()=>Store.MoveItemToIndex(item.Id,oldIndex));
    }
    // 松手时查找光标下的侧边栏分类行；鼠标已被捕获，只能用命中测试，不能用 Mouse.DirectlyOver。
    private (bool Found,string? Id) CategoryUnderCursor(Point local)
    {
        if(local.X<0||local.Y<0||local.X>ActualWidth||local.Y>ActualHeight)return (false,null);
        var rows=Tabs.Children.OfType<Button>().ToList();
        bool found=false;string? id=null;
        VisualTreeHelper.HitTest(this,null,r=>
        {
            for(DependencyObject? d=r.VisualHit as DependencyObject;d is not null;d=VisualTreeHelper.GetParent(d))
            {
                if(d is Button b && rows.Any(t=>ReferenceEquals(t,b))){found=true;id=b.Tag as string;return HitTestResultBehavior.Stop;}
                if(ReferenceEquals(d,this))break;
            }
            return HitTestResultBehavior.Continue;
        },new PointHitTestParameters(local));
        return (found,id);
    }
    private bool CanImport(IDataObject data)
    {
        if(data.GetDataPresent(ItemFormat))return true;
        if(data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length>0)return files.All(path=>Store.DesktopRoots.Any(root=>string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))),Path.TrimEndingDirectorySeparator(root),StringComparison.OrdinalIgnoreCase)) && (File.Exists(path)||Directory.Exists(path)) && !string.Equals(Path.GetFullPath(path),Environment.ProcessPath,StringComparison.OrdinalIgnoreCase));
        return ShellIntegration.ContainsRecycleBin(data);
    }
    private void OnDragOver(object sender,DragEventArgs e) => HandleDragOver(e);
    private void HandleDragOver(DragEventArgs e)
    {
        GuardHide(1500);
        try
        {
            var accepted=CanImport(e.Data);
            e.Effects=accepted?(e.Data.GetDataPresent(ItemFormat)?DragDropEffects.Move:DragDropEffects.Copy):DragDropEffects.None;
            // 不再绘制蓝色高亮边框，只用状态栏文字给出收纳预览，避免拖放层残留或遮挡图标。
            if(accepted)SetStatus(ImportPreview(e.Data));
            e.Handled=true;
        }
        catch{e.Effects=DragDropEffects.None;e.Handled=true;}
    }
    private void OnDragLeave(object sender,DragEventArgs e) { GuardHide(1000);SetStatus("已保存"); }
    private void OnDrop(object sender,DragEventArgs e) => HandleDrop(e,_activeCategory??"uncategorized");
    private void HandleDrop(DragEventArgs e,string? categoryId)
    {
        GuardHide(1500);e.Handled=true;categoryId??="uncategorized";
        if(!CanImport(e.Data)){e.Effects=DragDropEffects.None;return;}
        _interactions++;
        try
        {
            if(e.Data.GetDataPresent(ItemFormat) && e.Data.GetData(ItemFormat) is string id)
            {
                var oldCategory=Store.State.Items.FirstOrDefault(i=>i.Id==id)?.CategoryId??"uncategorized";
                Run(()=>Store.MoveItem(id,categoryId),"已移动分类");
                RecordUndo("拖动归类",()=>Store.MoveItem(id,oldCategory));
            }
            else
            {
                var collected=new List<string>();
                if(e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
                    foreach(var path in files){if(!Run(()=>{collected.Add(Store.Collect(path,categoryId).Id);},"已喂给番茄"))break;}
                if(ShellIntegration.ContainsRecycleBin(e.Data) && !Store.State.Items.Any(i=>i.Kind=="recycle-bin"))
                    Run(()=>{collected.Add(Store.CollectRecycleBin(categoryId).Id);},"回收站已喂给番茄");
                if(collected.Count>0)RecordUndo("收纳 "+collected.Count+" 个图标",()=>RestoreByIds(collected));
            }
            _activeCategory=categoryId;Render();ShellIntegration.RefreshDesktop();e.Effects=e.Data.GetDataPresent(ItemFormat)?DragDropEffects.Move:DragDropEffects.Copy;
        }
        finally{_interactions--;}
    }
    private void OnKeyDown(object sender,KeyEventArgs e)
    {
        // Ctrl+Z 撤销上一步收纳操作；输入框内保留系统自带的文本撤销。
        if(e.Key==Key.Z&&Keyboard.Modifiers.HasFlag(ModifierKeys.Control)&&!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)&&e.OriginalSource is not TextBox)
        {
            e.Handled=true;UndoLast();return;
        }
        // Esc 关闭页面：与标题栏 × 走同一条路径，由 OnClosing 统一裁决
        // （默认隐藏到托盘后台运行；用户开启「关闭时结束进程」时才退出进程）。
        // 弹出菜单打开时先让菜单自身消费 Esc，避免一次按键同时关菜单又关窗口。
        if(e.Key==Key.Escape && !_dragging)
        {
            if(_menus>0)return;
            e.Handled=true;
            // 从设置 / 图标库子页面退出时先复位到图标页，下次唤起不会停留在旧页面。
            if(_settingsVisible||_catalogVisible)ShowIconsView();
            if(!KeepOpenForVerification)Close();
            return;
        }
        if(e.OriginalSource is not Button button || button.Tag is not NookItem)return;
        if(e.Key==Key.Apps || (e.Key==Key.F10&&Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))){e.Handled=true;ShowIconMenu(button,(NookItem)button.Tag);return;}
        if(e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            // 上下键按当前实际列数跨行；侧边栏压缩内容区后列数会随窗口宽度变化。
            var buttons=FindVisualChildren<Button>(Icons).ToList();var index=buttons.IndexOf(button);var offset=e.Key==Key.Left?-1:e.Key==Key.Right?1:e.Key==Key.Up?-IconColumns:IconColumns;var next=Math.Clamp(index+offset,0,buttons.Count-1);buttons[next].Focus();e.Handled=true;
        }
    }
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject source) where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(source);i++){var child=VisualTreeHelper.GetChild(source,i);if(child is T value)yield return value;foreach(var descendant in FindVisualChildren<T>(child))yield return descendant;}
    }
    // 失效入口在网格里整体标灰，悬停提示给出原因；分类信息仍保留在提示中。
    public sealed record IconEntry(NookItem Item,string Path,string Category,bool Broken)
    {
        public string Id=>Item.Id;
        public string Name=>Item.Name;
        public bool Recycle=>Item.Kind=="recycle-bin";
        public double IconOpacity=>Broken?.42:1;
        public string Hint=>Broken?"目标已失效 · 可在设置里检查并清理":Category;
    }
}
