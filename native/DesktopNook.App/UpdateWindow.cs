using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DesktopNook.App;

// 统一的更新窗口：确认 / 检查 / 下载 / 校验 / 等待重启 / 已是最新 / 失败 / 本地文件更新
// 共用一个 480px 外壳，状态切换只替换内容区与底部操作栏。
// 视觉规范见 design/update-window-ui.html；色板全部取 App.xaml 的动态资源，
// 因此深浅主题、系统强调色与两套皮肤自动跟随主面板，无需重建窗口。
internal sealed class UpdateWindow : Window
{
    internal sealed record Result(string? File,OnlineRelease? Release);

    private enum Phase { Checking,Available,Downloading,Verifying,Ready,UpToDate,Failure,Local,Notice }

    // 标题区的圆形状态图标已移除；这里只保留 InfoBar 与版本迁移轨仍在使用的小图形。
    // WPF Path mini-language：只用 M / L / A / Z，Stretch=Uniform 把 24×24 坐标缩到图标框里。
    private const string I_CHECK="M4,12 L9,17 L20,6";
    private const string I_ALERT="M12,3 L22,20 L2,20 Z M12,9 L12,14 M12,17 L12,17.5";
    private const string I_INFO="M12,3 A 9,9 0 1 1 12,21 A 9,9 0 1 1 12,3 M12,11 L12,16 M12,8 L12,8.2";
    private const string I_ARROW="M4,12 L19,12 M14,7 L19,12 L14,17";

    private const string SafeText="数据不会变：分类、收纳内容与设置都会保留，更新只在原位置替换程序文件。";

    private static Style Look(string key)=>(Style)Application.Current.FindResource(key);
    private static void Ref(FrameworkElement element,DependencyProperty property,string key)=>element.SetResourceReference(property,key);
    private static Path Glyph(string data,double size,double thickness=1.6)=>new()
    {
        Data=Geometry.Parse(data),Width=size,Height=size,Stretch=Stretch.Uniform,
        StrokeThickness=thickness,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,
        StrokeLineJoin=PenLineJoin.Round,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center
    };

    private Phase _phase=Phase.Notice;
    private string _current=MainWindow.CurrentVersionLabel;
    private OnlineRelease? _release;
    private string _dataRoot="";
    private Func<Task<OnlineRelease?>>? _check;
    private Action<string>? _status;
    private Action? _onLocal;
    private CancellationTokenSource? _cancel;
    private string? _downloadedFile;
    private double _pct;
    private bool _verifyingShown;
    private bool _closed;

    private readonly StackPanel _head;
    private readonly TextBlock _eyebrow,_headline,_description;
    private readonly Border _rail;
    private readonly TextBlock _railCurrent,_railTarget,_railMeta;
    private readonly Border _progressBox;
    private readonly TextBlock _progressLabel,_progressPct,_progressMeta1,_progressMeta2;
    private readonly Border _barTrack,_barFill;
    private readonly Border[] _dots=new Border[4];
    private readonly TextBlock[] _steps=new TextBlock[4];
    private readonly Border _infoBox;
    private readonly Path _infoIcon;
    private readonly TextBlock _infoText;
    private readonly Button _link,_secondary,_primary;

    // 尺寸自检用：标题区与内容卡必须共用同一左边缘。
    internal double HeaderInset=>_head.Margin.Left;
    internal double ContentInset=>_rail.Margin.Left;

    internal UpdateWindow(Window owner)
    {
        // Chrome(rich:true) 会把 Width/Height 各加 20 并留出 10px 阴影边距，最终窗口宽 480。
        Owner=owner;Title="番茄桌面 · 更新";Width=460;SizeToContent=SizeToContent.Height;
        ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        ShowInTaskbar=false;UseLayoutRounding=true;SnapsToDevicePixels=true;FontSize=13;

        var root=new StackPanel();

        // 标题区：无状态图标，眉标 / 标题 / 说明纵向单列，与下方内容卡共用 24px 左边缘。
        _head=new StackPanel{Margin=new Thickness(24,22,24,16)};
        _eyebrow=new TextBlock{FontSize=11,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,7)};
        Ref(_eyebrow,TextBlock.ForegroundProperty,"WinAccent");
        _headline=new TextBlock{FontSize=20,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,6)};
        Ref(_headline,TextBlock.ForegroundProperty,"WinText");
        _description=new TextBlock{FontSize=12,TextWrapping=TextWrapping.Wrap};
        Ref(_description,TextBlock.ForegroundProperty,"WinTextSecondary");
        _head.Children.Add(_eyebrow);_head.Children.Add(_headline);_head.Children.Add(_description);
        root.Children.Add(_head);

        // 版本迁移轨：当前版本 → 目标版本，下面是大小 / 来源 / 校验方式。
        _rail=new Border{CornerRadius=new CornerRadius(8),BorderThickness=new Thickness(1),Padding=new Thickness(14,12,14,11),Margin=new Thickness(24,0,24,14)};
        Ref(_rail,Border.BackgroundProperty,"WinCardFill");Ref(_rail,Border.BorderBrushProperty,"WinStroke");
        var rail=new Grid();
        rail.RowDefinitions.Add(new(){Height=GridLength.Auto});rail.RowDefinitions.Add(new(){Height=GridLength.Auto});rail.RowDefinitions.Add(new(){Height=GridLength.Auto});
        rail.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        rail.ColumnDefinitions.Add(new(){Width=new GridLength(24)});
        rail.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        var current=new StackPanel();var target=new StackPanel();
        var currentLabel=new TextBlock{Text="当前版本",FontSize=10.5,Margin=new Thickness(0,0,0,4)};
        var targetLabel=new TextBlock{Text="更新到",FontSize=10.5,Margin=new Thickness(0,0,0,4)};
        Ref(currentLabel,TextBlock.ForegroundProperty,"WinTextTertiary");Ref(targetLabel,TextBlock.ForegroundProperty,"WinTextTertiary");
        _railCurrent=new TextBlock{FontSize=16,FontWeight=FontWeights.SemiBold};
        _railTarget=new TextBlock{FontSize=16,FontWeight=FontWeights.SemiBold};
        Ref(_railCurrent,TextBlock.ForegroundProperty,"WinText");Ref(_railTarget,TextBlock.ForegroundProperty,"WinAccent");
        current.Children.Add(currentLabel);current.Children.Add(_railCurrent);
        target.Children.Add(targetLabel);target.Children.Add(_railTarget);
        Grid.SetColumn(current,0);Grid.SetColumn(target,2);rail.Children.Add(current);rail.Children.Add(target);
        var arrow=Glyph(I_ARROW,18);Ref(arrow,Shape.StrokeProperty,"WinTextTertiary");Grid.SetColumn(arrow,1);rail.Children.Add(arrow);
        var rule=new Border{Height=1,Margin=new Thickness(0,11,0,0)};Ref(rule,Border.BackgroundProperty,"WinStrokeSoft");
        Grid.SetRow(rule,1);Grid.SetColumnSpan(rule,3);rail.Children.Add(rule);
        _railMeta=new TextBlock{FontSize=10.5,Margin=new Thickness(0,9,0,0),TextWrapping=TextWrapping.Wrap};
        Ref(_railMeta,TextBlock.ForegroundProperty,"WinTextTertiary");
        Grid.SetRow(_railMeta,2);Grid.SetColumnSpan(_railMeta,3);rail.Children.Add(_railMeta);
        _rail.Child=rail;root.Children.Add(_rail);

        // 进度：文件名 / 百分比 → 6px 圆角进度条 → 四段阶段 → 字节数。
        _progressBox=new Border{CornerRadius=new CornerRadius(8),BorderThickness=new Thickness(1),Padding=new Thickness(14,13,14,12),Margin=new Thickness(24,0,24,14)};
        Ref(_progressBox,Border.BackgroundProperty,"WinCardFill");Ref(_progressBox,Border.BorderBrushProperty,"WinStroke");
        var progress=new StackPanel();
        var row=new Grid();row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        _progressLabel=new TextBlock{FontSize=12,FontWeight=FontWeights.SemiBold};Ref(_progressLabel,TextBlock.ForegroundProperty,"WinText");
        _progressPct=new TextBlock{FontSize=11,FontFamily=new FontFamily("Cascadia Mono, Consolas"),HorizontalAlignment=HorizontalAlignment.Right};
        Ref(_progressPct,TextBlock.ForegroundProperty,"WinAccent");
        Grid.SetColumn(_progressPct,1);row.Children.Add(_progressLabel);row.Children.Add(_progressPct);progress.Children.Add(row);
        var bar=new Grid{Height=6,Margin=new Thickness(0,9,0,0),ClipToBounds=true};
        _barTrack=new Border{CornerRadius=new CornerRadius(3)};Ref(_barTrack,Border.BackgroundProperty,"WinHover");
        _barFill=new Border{CornerRadius=new CornerRadius(3),HorizontalAlignment=HorizontalAlignment.Left,Width=0};
        Ref(_barFill,Border.BackgroundProperty,"WinAccent");
        bar.Children.Add(_barTrack);bar.Children.Add(_barFill);
        _barTrack.SizeChanged+=(_,_)=>ApplyBarWidth();
        progress.Children.Add(bar);
        var steps=new Grid{Margin=new Thickness(0,12,0,0)};
        for(int i=0;i<4;i++)steps.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        for(int i=0;i<4;i++)
        {
            var cell=new StackPanel{Orientation=Orientation.Horizontal};
            _dots[i]=new Border{Width=6,Height=6,CornerRadius=new CornerRadius(3),Margin=new Thickness(0,0,5,0),VerticalAlignment=VerticalAlignment.Center};
            Ref(_dots[i],Border.BackgroundProperty,"WinStroke");
            _steps[i]=new TextBlock{FontSize=10};Ref(_steps[i],TextBlock.ForegroundProperty,"WinTextTertiary");
            cell.Children.Add(_dots[i]);cell.Children.Add(_steps[i]);
            Grid.SetColumn(cell,i);steps.Children.Add(cell);
        }
        progress.Children.Add(steps);
        var meta=new Grid{Margin=new Thickness(0,10,0,0)};
        meta.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});meta.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        _progressMeta1=new TextBlock{FontSize=10.5};_progressMeta2=new TextBlock{FontSize=10.5,HorizontalAlignment=HorizontalAlignment.Right};
        Ref(_progressMeta1,TextBlock.ForegroundProperty,"WinTextTertiary");Ref(_progressMeta2,TextBlock.ForegroundProperty,"WinTextTertiary");
        Grid.SetColumn(_progressMeta2,1);meta.Children.Add(_progressMeta1);meta.Children.Add(_progressMeta2);
        progress.Children.Add(meta);
        _progressBox.Child=progress;root.Children.Add(_progressBox);

        // InfoBar：安全声明 / 成功 / 失败归因，底色与描边跟随强调色资源。
        _infoBox=new Border{CornerRadius=new CornerRadius(6),BorderThickness=new Thickness(1),Padding=new Thickness(12,10,12,10),Margin=new Thickness(24,0,24,18)};
        Ref(_infoBox,Border.BackgroundProperty,"WinAccentSoft");Ref(_infoBox,Border.BorderBrushProperty,"WinAccentLine");
        var info=new DockPanel();
        _infoIcon=Glyph(I_INFO,15);Ref(_infoIcon,Shape.StrokeProperty,"WinAccent");_infoIcon.Margin=new Thickness(0,0,9,0);
        DockPanel.SetDock(_infoIcon,Dock.Left);info.Children.Add(_infoIcon);
        _infoText=new TextBlock{FontSize=11.5,TextWrapping=TextWrapping.Wrap};
        Ref(_infoText,TextBlock.ForegroundProperty,"WinText");
        info.Children.Add(_infoText);_infoBox.Child=info;root.Children.Add(_infoBox);

        // 底部操作栏：左侧链接入口，右侧次按钮 + 主按钮。
        var foot=new Border{BorderThickness=new Thickness(0,1,0,0),Padding=new Thickness(24,13,24,13)};
        Ref(foot,Border.BorderBrushProperty,"WinStrokeSoft");
        var footGrid=new Grid();footGrid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
        footGrid.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        _link=new Button{Content="从本地文件更新…",Background=Brushes.Transparent,BorderBrush=Brushes.Transparent,
            BorderThickness=new Thickness(0),FontSize=11.5,Cursor=Cursors.Hand,Padding=new Thickness(2,6,2,6),VerticalAlignment=VerticalAlignment.Center};
        Ref(_link,Control.ForegroundProperty,"WinAccent");
        _link.Click+=(_,_)=>OpenLocal();
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        _secondary=new Button{Style=Look("WinSecondaryButton"),MinWidth=88,Margin=new Thickness(0,0,8,0)};
        _primary=new Button{Style=Look("PrimaryButton"),MinWidth=88,IsDefault=true};
        _secondary.Click+=(_,_)=>OnSecondary();_primary.Click+=(_,_)=>OnPrimary();
        actions.Children.Add(_secondary);actions.Children.Add(_primary);
        Grid.SetColumn(actions,1);footGrid.Children.Add(_link);footGrid.Children.Add(actions);
        foot.Child=footGrid;root.Children.Add(foot);

        Content=root;UiDialogs.Chrome(this,rich:true);
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){e.Handled=true;Reject();}};
        Closed+=(_,_)=>{_closed=true;_cancel?.Cancel();};
    }

    // ---------- 状态机 ----------
    private void Base(Phase phase,string eyebrow,string headline,string description,string tone)
    {
        _phase=phase;
        _eyebrow.Text=eyebrow;_headline.Text=headline;_description.Text=description;
        // 状态色由被移除的圆形图标迁移到眉标；InfoBar 仍保留小图形与同一套状态色。
        string ink=tone switch{"success"=>"WinSyncGreen","error"=>"WinClose",_=>"WinAccent"};
        Ref(_eyebrow,TextBlock.ForegroundProperty,ink);
        Ref(_infoIcon,Shape.StrokeProperty,ink);
        _infoIcon.Data=Geometry.Parse(tone switch{"success"=>I_CHECK,"error"=>I_ALERT,_=>I_INFO});
    }
    private void Parts(bool rail,bool progress,bool info,bool link,string? secondary,string? primary,bool disabled=false)
    {
        _rail.Visibility=rail?Visibility.Visible:Visibility.Collapsed;
        _progressBox.Visibility=progress?Visibility.Visible:Visibility.Collapsed;
        _infoBox.Visibility=info?Visibility.Visible:Visibility.Collapsed;
        _link.Visibility=link&&_onLocal is not null?Visibility.Visible:Visibility.Collapsed;
        _secondary.Visibility=secondary is null?Visibility.Collapsed:Visibility.Visible;
        _primary.Visibility=primary is null?Visibility.Collapsed:Visibility.Visible;
        if(secondary is not null)_secondary.Content=secondary;
        if(primary is not null)_primary.Content=primary;
        _secondary.IsEnabled=!disabled;_primary.IsEnabled=!disabled;
    }
    private void Rail(string target,params string[] meta)
    {
        _railCurrent.Text=_current;_railTarget.Text=target;_railMeta.Text=string.Join("   ·   ",meta);
    }
    private void Steps(string[] names,int now)
    {
        for(int i=0;i<4;i++)
        {
            _steps[i].Text=names[i];
            string ink=i<now?"WinAccent":i==now?"WinAccent":"WinTextTertiary";
            Ref(_steps[i],TextBlock.ForegroundProperty,ink);
            Ref(_dots[i],Border.BackgroundProperty,i<=now?"WinAccent":"WinStroke");
        }
    }
    private void ApplyBarWidth()=>_barFill.Width=Math.Max(0,_barTrack.ActualWidth*_pct/100);

    internal void ShowChecking()
    {
        Base(Phase.Checking,"检查更新","正在检查是否有新版本","正在读取发布记录，通常只需几秒。","info");
        Parts(false,true,true,false,"取消检查",null);
        _progressLabel.Text="检查中";_progressPct.Text="…";_pct=34;ApplyBarWidth();
        _progressMeta1.Text="发布记录";_progressMeta2.Text="请稍候";
        Steps(["检查","下载","校验","重启"],0);
        _infoText.Text="后台检查只提示，不会自动下载或安装。";
    }
    internal void ShowAvailable(OnlineRelease release)
    {
        _release=release;
        Base(Phase.Available,"有可用更新","番茄桌面有新版本","确认后开始下载，校验通过会替换程序并自动重新打开。","info");
        Parts(true,false,true,true,"稍后","立即更新");
        Rail("v"+release.Version.ToString(3),$"安装包 {release.Size/1048576d:F1} MB","发布记录","SHA-256 校验");
        _infoText.Text=SafeText;
    }
    internal void ShowDownloading(OnlineRelease release)
    {
        _release=release;_pct=0;_verifyingShown=false;
        Base(Phase.Downloading,"正在下载","正在下载更新","下载完成后会校验文件，再在原位置替换并重启。","info");
        Parts(true,true,true,false,"取消下载",null);
        Rail("v"+release.Version.ToString(3),$"安装包 {release.Size/1048576d:F1} MB","发布记录","SHA-256 校验");
        _progressLabel.Text=OnlineUpdater.AssetName;_progressPct.Text="0%";ApplyBarWidth();
        _progressMeta1.Text=$"0.0 MB / {release.Size/1048576d:F1} MB";_progressMeta2.Text="校验后自动重启";
        Steps(["下载","校验","准备","重启"],0);
        _infoText.Text="取消下载不会影响当前版本，随时可以重新检查更新。";
    }
    internal void ShowVerifying()
    {
        _phase=Phase.Verifying;_pct=100;
        _progressLabel.Text="正在校验更新文件";_progressPct.Text="100%";ApplyBarWidth();
        _progressMeta1.Text="下载完成";_progressMeta2.Text="校验与准备";
        Steps(["下载","校验","准备","重启"],1);
        Parts(true,true,true,false,"请稍候…",null,disabled:true);
        _infoText.Text="校验不通过不会替换任何文件，当前版本保持可用。";
    }
    internal void ShowReady()
    {
        Base(Phase.Ready,"更新已就绪","更新已准备好","点击立即重启完成替换，程序会自动重新打开。","success");
        Parts(true,false,true,false,"稍后重启","立即重启");
        if(_release is not null)Rail("v"+_release.Version.ToString(3),$"安装包 {_release.Size/1048576d:F1} MB","发布记录","SHA-256 校验");
        _infoText.Text="现在重启会关闭当前程序。如果有正在编辑的内容，可以先选「稍后重启」。";
    }
    internal void ShowUpToDate()
    {
        Base(Phase.UpToDate,"无需更新","当前已是最新版本","没有找到更新的版本，不需要做任何操作。","success");
        Parts(false,false,false,true,null,"完成");
    }
    internal void ShowFailure(bool canRetry,string? headline=null,string? description=null)
    {
        Base(Phase.Failure,"更新未完成",headline??"下载未完成",
            description??"网络或发布文件暂时不可用，当前版本可以继续正常使用。","error");
        Parts(_release is not null,false,true,true,"关闭",canRetry?"重试":"完成");
        if(_release is not null)Rail("v"+_release.Version.ToString(3),$"安装包 {_release.Size/1048576d:F1} MB","发布记录","SHA-256 校验");
        _infoText.Text="没有替换任何文件。可以重试、检查网络，或改用「更多 → 从本地文件更新…」。";
    }
    internal void ShowLocal(string current,string target)
    {
        _current=current;
        Base(Phase.Local,"本地文件","从本地文件更新","已选择安装包，确认版本与文件校验通过后开始替换。","info");
        Parts(true,false,true,false,"取消","开始更新");
        Rail(target,"本地安装包","版本校验","产品标识校验");
        _infoText.Text="校验内容：版本、产品标识与文件。完成后在原位置更新并自动重新打开。";
    }
    internal void ShowNotice(string eyebrow,string headline,string description,string info,string primary,string tone="info")
    {
        Base(Phase.Notice,eyebrow,headline,description,tone);
        Parts(false,false,info.Length>0,true,null,primary);
        _infoText.Text=info;
    }

    // ---------- 交互 ----------
    private void Reject()
    {
        try{DialogResult=false;}catch(InvalidOperationException){Close();}
    }
    private void Accept()
    {
        try{DialogResult=true;}catch(InvalidOperationException){Close();}
    }
    private void OpenLocal()
    {
        // 先关掉自己的模态循环，再让主窗口去开文件选择器，避免嵌套模态对话框。
        var callback=_onLocal;Reject();
        if(callback is not null)Dispatcher.BeginInvoke(callback);
    }
    private void OnPrimary()
    {
        switch(_phase)
        {
            case Phase.Available:
            case Phase.Failure when _release is not null: StartDownload();break;
            case Phase.Failure: _=RunCheck();break;
            case Phase.Ready:
            case Phase.Local: Accept();break;
            default: Reject();break;
        }
    }
    private void OnSecondary()
    {
        if(_phase is Phase.Downloading or Phase.Checking or Phase.Verifying)_cancel?.Cancel();
        Reject();
    }

    // ---------- 在线流程：一个窗口走完检查 → 确认 → 下载 → 等待重启 ----------
    private void ConfigureOnline(string current,string dataRoot,Func<Task<OnlineRelease?>> check,Action<string>? status,Action? onLocal)
    {
        _current=current;_dataRoot=dataRoot;_check=check;_status=status;_onLocal=onLocal;
        Loaded+=(_,_)=>_=RunCheck();
    }
    private async Task RunCheck()
    {
        ShowChecking();
        try
        {
            var release=_check is null?null:await _check();
            if(release is null)
            {
                _status?.Invoke($"当前 {_current} 已是最新版本");
                if(!_closed)ShowUpToDate();
            }
            else
            {
                _status?.Invoke($"发现新版本 {release.Version.ToString(3)}");
                if(!_closed)ShowAvailable(release);
            }
        }
        catch(Exception)
        {
            _status?.Invoke("更新检查未完成");
            if(!_closed)ShowFailure(true,"检查未完成","暂时无法读取发布记录，请检查网络或系统代理后重试。当前版本可以继续正常使用。");
        }
    }
    private void ApplyProgress(double value)
    {
        if(!IsLoaded)return;
        _pct=Math.Clamp(value,0,100);
        _progressPct.Text=$"{_pct:F0}%";ApplyBarWidth();
        if(_release is not null)_progressMeta1.Text=$"{_release.Size*_pct/100/1048576d:F1} MB / {_release.Size/1048576d:F1} MB";
        if(_pct>=100&&!_verifyingShown){_verifyingShown=true;ShowVerifying();}
    }
    private async void StartDownload()
    {
        if(_release is null)return;
        var cts=new CancellationTokenSource();_cancel=cts;
        ShowDownloading(_release);
        try
        {
            using var client=OnlineUpdater.CreateClient();
            var progress=new Progress<double>(ApplyProgress);
            var file=await OnlineUpdater.DownloadAsync(client,_release,_dataRoot,progress,cts.Token);
            if(cts.IsCancellationRequested)return;
            _downloadedFile=file;ShowReady();
        }
        catch(OperationCanceledException){ }
        catch(Exception)
        {
            if(!cts.IsCancellationRequested&&!_closed){_status?.Invoke("更新下载未完成");ShowFailure(true);}
        }
        finally
        {
            if(ReferenceEquals(_cancel,cts))_cancel=null;
            cts.Dispose();
        }
    }

    // ---------- 对外入口 ----------
    internal static bool ConfirmLocal(Window owner,string current,string target)
    {
        var window=new UpdateWindow(owner);window.ShowLocal(current,target);
        return window.ShowDialog()==true;
    }
    internal static void Notice(Window owner,string eyebrow,string headline,string description,string info,Action? onLocal=null,string primary="完成",string tone="info")
    {
        var window=new UpdateWindow(owner){_onLocal=onLocal};
        window.ShowNotice(eyebrow,headline,description,info,primary,tone);
        window.ShowDialog();
    }
    internal static Result RunOnline(Window owner,string current,string dataRoot,Func<Task<OnlineRelease?>> check,Action<string>? status=null,Action? onLocal=null)
    {
        var window=new UpdateWindow(owner);
        window.ConfigureOnline(current,dataRoot,check,status,onLocal);
        window.ShowDialog();
        return new Result(window.DialogResult==true?window._downloadedFile:null,window._release);
    }

    // 尺寸自检用：只渲染状态，不发起网络请求、不打开模态对话框。
    internal void PreviewChecking()=>ShowChecking();
    internal void PreviewAvailable(string current,OnlineRelease release)
    {
        _current=current;_onLocal=()=>{};ShowAvailable(release);
    }
    internal void PreviewDownload(string current,OnlineRelease release)
    {
        _current=current;_onLocal=null;ShowDownloading(release);
        _pct=64;_progressPct.Text="64%";ApplyBarWidth();
        _progressMeta1.Text=$"{release.Size*.64/1048576d:F1} MB / {release.Size/1048576d:F1} MB";
    }
}
