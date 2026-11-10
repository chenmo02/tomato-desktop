using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
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
    // 版本号唯一来源是 csproj 的 <Version>：1.2 显示「v1.2」，带修订号时保留三段。
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
    private bool _dragging; private int _interactions; private int _menus; private bool _allowExit; private DateTime _suppressUntil;
    // 双击会产生两次 Up/Click，用同一项的短节流保证只启动一次。
    private string? _lastLaunchId; private DateTime _lastLaunchAt;
    // 网格内拖动：鼠标捕获期间记录按钮与起点；松手后按落点重排顺序。
    private Button? _dragButton; private NookItem? _dragItem;
    private Point _dragPointer; private bool _dragMoved;
    // 拖放期间/刚结束时禁止「失活自动收起」，避免拖图标时整个面板消失。
    private DateTime _hideGuardUntil;
    private bool _manualPlacement; private double _manualLeft; private double _manualTop;
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
        SourceInitialized+=(_,_)=>ApplyBackdrop();
        // 窗口形状跟随皮肤圆角与 Viewbox 缩放；尺寸/DPI 变化后必须重裁，
        // 否则 DWM 背板会在四角画出未经玻璃覆盖的直角色块。
        SizeChanged+=(_,_)=>ApplyWindowRegion();
        DpiChanged+=(_,_)=>ApplyWindowRegion();
        InitializeComponent();
        // 侧栏版本文字跟随程序集版本，避免 XAML 里的占位值与 csproj 脱节。
        VersionLabel.Text="番茄桌面 · 版本 "+ProductVersion;
        // 主题必须在窗口布局前应用：App.xaml 里的画刷是共享实例，改色后所有控件同步生效。
        Settings=App.Options.ReadSettings();
        ThemeService.Apply(Settings);
        ApplyWindowIcon();InitializeGlass();ApplySkinVisual(false);InitializeSizing();
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
        // 失效检测放到后台优先级，不占用首次显示；解析快捷方式需要 UI 线程 STA。
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(ScanBroken));
        if(Store.RecoveryMessages.Count>0) SetStatus(Store.RecoveryMessages.Last());
        if(!App.IsSandbox&&OnlineUpdater.Enabled)_=CheckOnlineUpdateAsync(false);
    }
    private async void Place()
    {
        if(_resizing || KeepOpenForVerification || !IsLoaded || WindowState!=WindowState.Normal)return;
        // 用户手动拖动过面板后不再贴回任务栏，直到在“窗口大小”里选择“贴回任务栏”。
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
    public void ShowPanel() { WindowState=WindowState.Normal; Show(); Place(); Activate(); }
    public void TogglePanel(){if(WindowState==WindowState.Normal&&IsVisible)WindowState=WindowState.Minimized;else ShowPanel();}
    private void OnStateChanged(object? sender,EventArgs e) { if(WindowState==WindowState.Normal) Place(); }
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
        // 偏好默认关闭：失焦不自动收起，面板保持显示，只能由 × / 热键 / 任务栏按钮手动收起。
        if(!Settings.AutoHideOnDeactivate)return;
        if(TaskbarPlacement.PointerOverButton())return;
        Dispatcher.BeginInvoke(new Action(TryAutoHide),System.Windows.Threading.DispatcherPriority.Background);
    }
    private void TryAutoHide()
    {
        if(KeepOpenForVerification||IsActive||WindowState==WindowState.Minimized)return;
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
        if(App.Options.CloseEndsProcess){_allowExit=true;e.Cancel=false;Dispatcher.BeginInvoke(()=>Application.Current.Shutdown());}else WindowState=WindowState.Minimized;
    }
    private void OnCloseButton(object sender,RoutedEventArgs e)=>Close();
    private void OnMinimizeButton(object sender,RoutedEventArgs e)=>WindowState=WindowState.Minimized;
    // macOS 绿灯语义：展开「窗口大小」菜单，替代系统级最大化（面板默认贴任务栏显示）。
    internal void OnZoomButton(object sender,RoutedEventArgs e)
    {
        // 子项来自临时 MenuItem，Popup 会先断开旧逻辑父级再挂到 ContextMenu。
        var items=new List<object>();
        foreach(var item in WindowSizeMenu().Items)items.Add(item);
        Popup((FrameworkElement)sender,items);
    }
    public void Quit() { _allowExit=true; Application.Current.Shutdown(); }
    internal void QuitForMaintenance(){if(!_dragging&&_interactions==0)Quit();}
    public void SetStatus(string message) { Status.Text=message;Status.ToolTip=message; }
    public void Render()
    {
        if(_activeCategory is not null && !Store.State.Categories.Any(c=>c.Id==_activeCategory)) _activeCategory=null;
        Dispatcher.BeginInvoke(UpdateSizingLayout,System.Windows.Threading.DispatcherPriority.Loaded);
        SyncTabs();
        UpdateTabSelection();
        RefreshIcons();
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
            // 「元素已具有逻辑父级」（点标题栏「窗口大小」即走这条路径）。
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
    private void OnMore(object sender,RoutedEventArgs e)
    {
        // 自启、关闭行为、主题、热键与数据备份已迁到独立设置页；这里只留页面入口与高级操作。
        Popup((FrameworkElement)sender,[
        // 页面级入口统一收到菜单顶部：不再占用分类导航条，功能与快捷键均保留。
        Menu("新建分类",()=>OnNewCategory(this,new RoutedEventArgs())),
        Menu("浏览全部图标…",ShowCatalogView),
        Menu("打开设置",()=>OnSettingsToggle(this,new RoutedEventArgs())),
        Menu("撤销上一步（Ctrl+Z）",UndoLast),
        Menu("检查失效入口",()=>OnCleanBroken(this,new RoutedEventArgs())),
        FileManagerMenu(),
        new Separator(),
        WindowSizeMenu(),new Separator(),
        Menu(UpdateMenuText,()=>_ = CheckOnlineUpdateAsync(true),OnlineUpdater.Enabled),
        Menu("从本地文件更新…",()=>_ = SelectUpdateAsync()),
        Menu("全部移回桌面",RestoreAll,Store.State.Items.Count>0),
        Menu("创建桌面与开始菜单入口",()=>Run(()=>ShortcutInstaller.InstallAll(),"已创建桌面与开始菜单入口，可右键固定到任务栏")),
        Menu("如何固定到任务栏",()=>{_interactions++;try{UiDialogs.Show(this,"右键任务栏上的“番茄桌面”图标，选择“固定到任务栏”。\n\n固定后点击同一个入口即可打开或收起番茄桌面。","番茄桌面");}finally{_interactions--;}}),
        Menu("打开数据目录",()=>Process.Start(new ProcessStartInfo("explorer.exe",Store.DataDirectory){UseShellExecute=true})),
        new Separator(),Menu("退出并结束进程",Quit)
    ]);
    }
    private async Task SelectUpdateAsync()
    {
        _interactions++;
        try
        {
            var picker=new Microsoft.Win32.OpenFileDialog{Title="选择番茄桌面新版本",Filter="番茄桌面程序或安装包 (*.exe)|*.exe",CheckFileExists=true};if(picker.ShowDialog(this)!=true)return;
            var source=picker.FileName;var target=Environment.ProcessPath!;var candidate=await Task.Run(()=>LocalUpdater.Inspect(source));
            if(UiDialogs.Show(this,"当前版本："+CurrentVersionLabel+"\n更新到：v"+candidate.Version.ToString(3)+"\n\n程序会在原位置更新，保留分类、收纳内容和设置，完成后自动打开。","更新番茄桌面",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;
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
            if(manual){SetStatus("在线更新未启用，可使用“从本地文件更新”");UiDialogs.Show(this,$"当前版本：{CurrentVersionLabel}\n在线更新暂未启用：尚未配置发布仓库地址。\n需要更新时请使用“更多 → 从本地文件更新…”。","在线更新");}
            return;
        }
        if(_onlineBusy){if(manual)SetStatus("正在检查或下载更新，请稍候…");return;}
        _onlineBusy=true;if(manual)_interactions++;
        try
        {
            if(manual)SetStatus($"正在检查更新（当前 {CurrentVersionLabel}）…");
            using var client=OnlineUpdater.CreateClient();
            _latestRelease=await OnlineUpdater.CheckAsync(client,System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!);
            if(_latestRelease is null)
            {
                if(manual){SetStatus($"当前 {CurrentVersionLabel} 已是最新版本");UiDialogs.Show(this,$"当前版本：{CurrentVersionLabel}\n已是最新版本。","检查更新");}return;
            }
            SetStatus($"发现新版本 {_latestRelease.Version.ToString(3)}，可在更多菜单中更新");
            if(!manual)return;
            string notes=_latestRelease.Notes.Length>600?_latestRelease.Notes[..600]+"…":_latestRelease.Notes;
            if(UiDialogs.Show(this,$"当前版本：{CurrentVersionLabel}\n更新到：v{_latestRelease.Version.ToString(3)}\n\n{notes}\n\n下载完成后自动重启，分类、收纳内容和设置保留。","发现新版本",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;
            var download=new UpdateDownloadWindow(this,_latestRelease,Store.DataDirectory);
            if(download.ShowDialog()!=true||download.DownloadedFile is null)return;
            SetStatus("正在准备更新…");
            var staged=await Task.Run(()=>LocalUpdater.Stage(download.DownloadedFile,Environment.ProcessPath!,Store.DataDirectory,App.IsSandbox?Path.GetDirectoryName(Store.DataDirectory):null));
            using(var plan=System.Text.Json.JsonDocument.Parse(File.ReadAllText(staged.Plan)))
                if(!plan.RootElement.GetProperty("CandidateHash").GetString()!.Equals(_latestRelease.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("更新文件校验失败，请重新下载。");
            var helper=new ProcessStartInfo(staged.Helper){UseShellExecute=false,CreateNoWindow=true};helper.ArgumentList.Add("--apply-update");helper.ArgumentList.Add(staged.Plan);
            if(Process.Start(helper) is null)throw new IOException("无法启动更新助手。");Quit();
        }
        catch(Exception)
        {
            if(manual){SetStatus("更新检查未完成");UiDialogs.Show(this,$"当前版本：{CurrentVersionLabel}\n暂时无法完成在线更新，请检查网络或系统代理后重试。也可以使用“从本地文件更新”。","在线更新");}
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
        _dragButton.RenderTransform=new TranslateTransform(delta.X,delta.Y);
        e.Handled=true;
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
        if(e.Key==Key.Escape && !_dragging)
        {
            e.Handled=true;
            if(_settingsVisible){ShowIconsView();return;}
            if(_catalogVisible){ShowIconsView();return;}
            if(!KeepOpenForVerification)WindowState=WindowState.Minimized;return;
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
