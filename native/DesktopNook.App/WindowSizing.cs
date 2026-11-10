using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopNook.App;

internal sealed record WindowSizePreference
{
    public bool Free {get;init;}
    public bool Locked {get;init;}
    public double Zoom {get;init;}=1;
    public double Width {get;init;}=1040;
    public double Height {get;init;}=680;
    internal const double BaseWidth=1040,BaseHeight=680,Ratio=1040d/680;
    internal static readonly double[] Presets=[.8,1,1.25,1.5];
    internal WindowSizePreference Validated()=>this with
    {
        Zoom=Presets.Contains(Zoom)?Zoom:1,
        Width=double.IsFinite(Width)?Math.Clamp(Width,560,4096):BaseWidth,
        Height=double.IsFinite(Height)?Math.Clamp(Height,360,4096):BaseHeight
    };
    internal Size Desired=>Free?new(Width,Locked?Width/Ratio:Height):new(BaseWidth*Zoom,BaseHeight*Zoom);
}

public partial class MainWindow
{
    private WindowSizePreference _sizePreference=new();
    private bool _resizing;
    private Point _resizePointer;
    private Rect _resizeStart;
    internal Size PreferredWindowSize=>_sizePreference.Desired;
    internal WindowSizePreference SizePreference=>_sizePreference;

    private void InitializeSizing()
    {
        _sizePreference=App.Options.ReadWindowSize();
        Width=PreferredWindowSize.Width;Height=PreferredWindowSize.Height;
        SizeChanged+=(_,_)=>UpdateSizingLayout();
        foreach(string edge in new[]{"L","R","T","B","LT","RT","LB","RB"})
        {
            // 非 layered 窗口用 SetWindowRgn 裁成圆角，最外侧的角不属于窗口命中区域。
            // 角落把手向内扩到 26px，保证圆角内仍有一块可拖动的热区。
            var corner=edge.Length==2;
            var thumb=new Thumb{Tag=edge,Width=corner?26:edge.Contains('L')||edge.Contains('R')?8:double.NaN,
                Height=corner?26:edge.Contains('T')||edge.Contains('B')?8:double.NaN,
                HorizontalAlignment=edge.Contains('L')?HorizontalAlignment.Left:edge.Contains('R')?HorizontalAlignment.Right:HorizontalAlignment.Stretch,
                VerticalAlignment=edge.Contains('T')?VerticalAlignment.Top:edge.Contains('B')?VerticalAlignment.Bottom:VerticalAlignment.Stretch,
                Cursor=edge is "LT" or "RB"?Cursors.SizeNWSE:edge is "RT" or "LB"?Cursors.SizeNESW:edge is "L" or "R"?Cursors.SizeWE:Cursors.SizeNS,
                Background=Brushes.Transparent,ToolTip="拖动调整窗口大小"};
            // 1/255 alpha 底色给透明把手保留 WPF 命中能力；窗口已由 SetWindowRgn
            // 裁成圆角，圆外的像素不再接收输入，所以角落把手必须向内扩展。
            var border=new FrameworkElementFactory(typeof(Border));border.SetValue(Border.BackgroundProperty,new SolidColorBrush(Color.FromArgb(1,81,115,218)));
            thumb.Template=new ControlTemplate(typeof(Thumb)){VisualTree=border};
            // Corners go last and win hit testing over the edge strips.
            thumb.DragStarted+=(_,_)=>{_resizing=true;_interactions++;_placementVersion++;_resizeStart=new Rect(Left,Top,ActualWidth,ActualHeight);_resizePointer=PointToScreen(Mouse.GetPosition(this));};
            thumb.DragDelta+=ResizeDelta;
            thumb.DragCompleted+=(_,e)=>
            {
                if(e.Canceled){Left=_resizeStart.Left;Top=_resizeStart.Top;Width=_resizeStart.Width;Height=_resizeStart.Height;}
                else
                {
                    _sizePreference=_sizePreference with{Width=ActualWidth,Height=ActualHeight};SaveSizePreference();
                    // 从左侧/上侧缩放会改变 Left/Top，手动摆放模式下要同步保存新位置。
                    if(_manualPlacement){_manualLeft=Left;_manualTop=Top;SaveWindowPosition();}
                }
                _resizing=false;_interactions--;Place();
            };
            ResizeHandles.Children.Add(thumb);
        }
        UpdateSizingLayout();
    }
    private void SaveSizePreference()
    {
        try{App.Options.WriteWindowSize(_sizePreference);}catch(Exception){SetStatus("窗口大小已调整，但未能保存，请稍后重试。");}
    }
    internal void SelectWindowSize(double? preset=null,bool? locked=null,bool reset=false)
    {
        _sizePreference=reset?new():preset is double zoom?new(){Zoom=zoom}:_sizePreference with
        {Free=true,Locked=locked??_sizePreference.Locked,Width=ActualWidth>0?ActualWidth:Width,Height=ActualHeight>0?ActualHeight:Height};
        _sizePreference=_sizePreference.Validated();
        Width=PreferredWindowSize.Width;Height=PreferredWindowSize.Height;UpdateSizingLayout();SaveSizePreference();Place();
    }
    internal MenuItem WindowSizeMenu()
    {
        var menu=new MenuItem{Header="窗口大小"};
        foreach(double zoom in WindowSizePreference.Presets)
        {
            var item=Menu($"{zoom*100:0}%"+(zoom==1?"（默认）":""),()=>SelectWindowSize(zoom));
            item.IsCheckable=true;item.IsChecked=!_sizePreference.Free&&_sizePreference.Zoom==zoom;menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var free=Menu("自由调整",()=>SelectWindowSize());free.IsCheckable=true;free.IsChecked=_sizePreference.Free;menu.Items.Add(free);
        var aspect=Menu("锁定宽高比",()=>SelectWindowSize(locked:!_sizePreference.Locked),_sizePreference.Free);
        aspect.IsCheckable=true;aspect.IsChecked=_sizePreference.Locked;menu.Items.Add(aspect);
        menu.Items.Add(new Separator());menu.Items.Add(Menu("恢复默认大小",()=>SelectWindowSize(reset:true)));
        menu.Items.Add(new Separator());menu.Items.Add(Menu("贴回任务栏",ResetWindowPlacement,_manualPlacement));return menu;
    }
    internal void ResetWindowPlacement()
    {
        _manualPlacement=false;_manualLeft=0;_manualTop=0;SaveWindowPosition();Place();
    }
    private void UpdateSizingLayout()
    {
        if(Surface is null||ResizeHandles is null)return;
        // Every mode changes the viewport, never the icon/text scale. Aspect lock
        // constrains the window shape only; available space controls row/column count.
        // 设计稿按 CSS 像素 1:1 落地：Surface 逻辑尺寸等于窗口逻辑尺寸，Viewbox 缩放恒为 1。
        Surface.Width=Math.Max(1,ActualWidth>0?ActualWidth:Width);
        Surface.Height=Math.Max(1,ActualHeight>0?ActualHeight:Height);
        // 开始菜单式排版：NavigationView 236px，内容区左右各 30px，磁贴约 93px 一格。
        // 默认 1040px 窗口的可用宽度 744px 对应八列；窗口变宽自动增列，变窄自动减列。
        // 高 DPI 下 SetWindowPos 折算出的逻辑宽度可能比预设少不到 1px，分子留 2px 容差，
        // 避免临界尺寸正好落在整数边界时列数来回跳。
        const double sidebarWidth=236,contentPadding=60,columnWidth=93,dpiTolerance=2;
        IconColumns=Math.Clamp((int)Math.Floor((Surface.Width-sidebarWidth-contentPadding+dpiTolerance)/columnWidth),4,30);
        foreach(var panel in FindVisualChildren<UniformGrid>(Icons))panel.Columns=IconColumns;
        ResizeHandles.Visibility=_sizePreference.Free?Visibility.Visible:Visibility.Collapsed;
        ResizeHint.Visibility=ResizeHandles.Visibility;
    }
    // 磁贴高度 104px + 上下各 1px 外边距；拖动落点换算行号时与 XAML 保持一致。
    internal const double IconRowHeight=106;
    internal int IconColumns {get;private set;}=8;
    private void ResizeDelta(object sender,DragDeltaEventArgs e)
    {
        var point=PointToScreen(Mouse.GetPosition(this));var dpi=VisualTreeHelper.GetDpi(this);
        double dx=(point.X-_resizePointer.X)/dpi.DpiScaleX,dy=(point.Y-_resizePointer.Y)/dpi.DpiScaleY;
        var rect=CalculateResize(_resizeStart,(string)((Thumb)sender).Tag,dx,dy,_sizePreference.Locked,TaskbarPlacement.WorkArea(this));
        Left=rect.Left;Top=rect.Top;Width=rect.Width;Height=rect.Height;
    }
    internal static Rect CalculateResize(Rect start,string edge,double dx,double dy,bool locked,Rect work)
    {
        double width=start.Width+(edge.Contains('L')?-dx:edge.Contains('R')?dx:0);
        double height=start.Height+(edge.Contains('T')?-dy:edge.Contains('B')?dy:0);
        double minWidth=Math.Min(560,work.Width),minHeight=Math.Min(360,work.Height);
        if(locked)
        {
            if(edge is "T" or "B"||(edge.Length==2&&Math.Abs(dy*WindowSizePreference.Ratio)>Math.Abs(dx)))width=height*WindowSizePreference.Ratio;
            double maximum=Math.Min(work.Width,work.Height*WindowSizePreference.Ratio);
            width=Math.Clamp(width,Math.Min(minWidth,maximum),maximum);height=width/WindowSizePreference.Ratio;
        }
        else{width=Math.Clamp(width,minWidth,work.Width);height=Math.Clamp(height,minHeight,work.Height);}
        double left=edge.Contains('L')?start.Right-width:start.Left,top=edge.Contains('T')?start.Bottom-height:start.Top;
        left=Math.Clamp(left,work.Left,work.Right-width);top=Math.Clamp(top,work.Top,work.Bottom-height);
        return new Rect(left,top,width,height);
    }
}
