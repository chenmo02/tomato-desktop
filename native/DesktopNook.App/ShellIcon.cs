using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopNook.App;

// Request physical pixels after both the panel's Viewbox and monitor DPI have
// been applied. Moving to another monitor or resizing reloads the matching size.
public sealed class ShellIcon : Image
{
    public static readonly DependencyProperty IconPathProperty=DependencyProperty.Register(nameof(IconPath),typeof(string),typeof(ShellIcon),new PropertyMetadata("",Changed));
    public static readonly DependencyProperty IsRecycleBinProperty=DependencyProperty.Register(nameof(IsRecycleBin),typeof(bool),typeof(ShellIcon),new PropertyMetadata(false,Changed));
    public static readonly DependencyProperty FastProperty=DependencyProperty.Register(nameof(Fast),typeof(bool),typeof(ShellIcon),new PropertyMetadata(false,Changed));
    // 图标库一次有上百个条目：固定请求尺寸，避免布局过程中产生大量不同尺寸的孤儿请求。
    public static readonly DependencyProperty FixedPixelsProperty=DependencyProperty.Register(nameof(FixedPixels),typeof(int),typeof(ShellIcon),new PropertyMetadata(0,Changed));
    // 延迟请求：图标库只为滚动到视口内的卡片解码图标，未露面的条目不进队列。
    public static readonly DependencyProperty DeferredProperty=DependencyProperty.Register(nameof(Deferred),typeof(bool),typeof(ShellIcon),new PropertyMetadata(false,Changed));
    public string IconPath {get=>(string)GetValue(IconPathProperty);set=>SetValue(IconPathProperty,value);}
    public bool IsRecycleBin {get=>(bool)GetValue(IsRecycleBinProperty);set=>SetValue(IsRecycleBinProperty,value);}
    public bool Fast {get=>(bool)GetValue(FastProperty);set=>SetValue(FastProperty,value);}
    public int FixedPixels {get=>(int)GetValue(FixedPixelsProperty);set=>SetValue(FixedPixelsProperty,value);}
    public bool Deferred {get=>(bool)GetValue(DeferredProperty);set=>SetValue(DeferredProperty,value);}
    private string? _key;
    private bool _requested;
    public bool IconRequested=>_requested;
    public ShellIcon()
    {
        SnapsToDevicePixels=true;UseLayoutRounding=true;
        RenderOptions.SetBitmapScalingMode(this,BitmapScalingMode.HighQuality);
        Loaded+=(_,_)=>
        {
            ShellIntegration.IconReady+=Ready;
            if(!Deferred)_requested=true;
            // 固定像素的图标与布局尺寸无关，不需要在每次 LayoutUpdated 时重新计算。
            if(FixedPixels<=0)LayoutUpdated+=LayoutChanged;
            Refresh();
        };
        Unloaded+=(_,_)=>{ShellIntegration.IconReady-=Ready;LayoutUpdated-=LayoutChanged;};
    }
    public void RequestIcon()
    {
        if(_requested)return;
        _requested=true;
        if(IsLoaded)Refresh();
    }
    private static void Changed(DependencyObject sender,DependencyPropertyChangedEventArgs e){var icon=(ShellIcon)sender;icon._key=null;if(icon.IsLoaded)icon.Refresh();}
    private void LayoutChanged(object? sender,EventArgs e)=>Refresh();
    private void Ready(string key)
    {
        // 每个图标就绪只唤醒对应控件，避免整页控件同时排入 Dispatcher 队列。
        if(!string.Equals(key,_key,StringComparison.Ordinal))return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>
        {
            if(IsLoaded&&string.Equals(key,_key,StringComparison.Ordinal))Refresh(true);
        }));
    }
    protected override void OnDpiChanged(DpiScale oldDpi,DpiScale newDpi){base.OnDpiChanged(oldDpi,newDpi);_key=null;}
    // 主界面图标以 2× 物理像素向 Shell 请求，再由 WPF HighQuality 缩到控件尺寸。
    // 1:1 取样时，只要 Shell 给的帧与显示尺寸差几像素就会出现插值软边；超采样让边缘抗锯齿干净。
    // 图标库（FixedPixels）不走这条路径，避免上百条目解码 2× 位图。
    internal const int SupersampleFactor=2;
    internal static int PixelSize(double logicalSize,double transformScale,double dpiScale)=>Math.Clamp((int)Math.Ceiling(logicalSize*transformScale*dpiScale),16,1024);
    private void Refresh(bool force=false)
    {
        if(Deferred&&!_requested)return;
        int pixels;
        if(FixedPixels>0)pixels=FixedPixels;
        else
        {
            var dpi=VisualTreeHelper.GetDpi(this);double width=ActualWidth,height=ActualHeight;
            if(Window.GetWindow(this) is Window window)
            {
                var bounds=TransformToAncestor(window).TransformBounds(new Rect(0,0,width,height));width=bounds.Width;height=bounds.Height;
            }
            pixels=PixelSize(Math.Max(width,height)*SupersampleFactor,1,Math.Max(dpi.DpiScaleX,dpi.DpiScaleY));
        }
        string key=ShellIntegration.IconKey(IconPath,IsRecycleBin,pixels,Fast);
        if(!force&&_key==key)return;
        _key=key;
        var icon=Fast?ShellIntegration.IconForFast(IconPath,pixels):ShellIntegration.IconFor(IconPath,IsRecycleBin,pixels);
        // 异步加载中返回的是占位图标，别把已经显示出来的真实图标覆盖回去。
        if(ReferenceEquals(icon,ShellIntegration.AppIcon)&&Source is not null&&!ReferenceEquals(Source,ShellIntegration.AppIcon))return;
        if(!ReferenceEquals(Source,icon))Source=icon;
    }
}
