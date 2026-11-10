using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;

namespace DesktopNook.App;

internal sealed record TaskbarAnchor(Rect Button,Rect Tray,Rect Work,double Dpi);
internal static class TaskbarPlacement
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; public readonly Rect ToRect()=>new(Left,Top,Right-Left,Bottom-Top); }
    [StructLayout(LayoutKind.Sequential)] private struct Point {public int X,Y;}
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo {public uint Size;public NativeRect Monitor,Work;public uint Flags;}
    private delegate bool EnumCallback(IntPtr window,IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback,IntPtr data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window,System.Text.StringBuilder name,int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref NativeRect rect,uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern IntPtr GetWindowLongPtr(IntPtr h,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")]private static extern IntPtr SetWindowLongPtr(IntPtr h,int index,IntPtr value);
    public static void EnableToggle(Window window){var h=new WindowInteropHelper(window).Handle;SetWindowLongPtr(h,-16,(IntPtr)(GetWindowLongPtr(h,-16).ToInt64()|0x20000));SetWindowPos(h,IntPtr.Zero,0,0,0,0,0x37);}
    private static readonly object Gate=new();private static Task<TaskbarAnchor?>? _pending;
    public static TaskbarAnchor? Cached { get; private set; }
    public static bool PointerOverButton(){GetCursorPos(out var p);return Cached?.Button.Contains(new System.Windows.Point(p.X,p.Y))==true;}
    private static List<IntPtr> Trays()
    {
        var trays=new List<IntPtr>();EnumWindows((window,_)=>{var name=new System.Text.StringBuilder(128);GetClassName(window,name,name.Capacity);if(name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")trays.Add(window);return true;},IntPtr.Zero);return trays;
    }
    private static TaskbarAnchor Anchor(IntPtr tray,Rect button)
    {
        GetWindowRect(tray,out var trayRect);var reference=new NativeRect{Left=(int)button.Left,Top=(int)button.Top,Right=(int)button.Right,Bottom=(int)button.Bottom};
        var info=new MonitorInfo{Size=(uint)Marshal.SizeOf<MonitorInfo>()};if(!GetMonitorInfo(MonitorFromRect(ref reference,2),ref info))throw new InvalidOperationException("无法读取任务栏所在屏幕。");
        var dpi=GetDpiForWindow(tray);return new(button,trayRect.ToRect(),info.Work.ToRect(),dpi==0?96:dpi);
    }
    public static TaskbarAnchor Fallback()
    {
        if(Cached is not null)return Cached;
        GetCursorPos(out var mouse);var trays=Trays();var tray=trays.FirstOrDefault(t=>{GetWindowRect(t,out var r);return r.ToRect().Contains(new System.Windows.Point(mouse.X,mouse.Y));});
        if(tray==IntPtr.Zero)tray=trays.FirstOrDefault();
        if(tray!=IntPtr.Zero){GetWindowRect(tray,out var r);var x=r.ToRect().Contains(new System.Windows.Point(mouse.X,mouse.Y))?mouse.X-20:r.Left;return Anchor(tray,new Rect(x,r.Top,40,r.Bottom-r.Top));}
        var area=SystemParameters.WorkArea;return new(new Rect(area.Left,area.Bottom,40,40),new Rect(area.Left,area.Bottom,area.Width,40),area,96);
    }
    public static async Task<TaskbarAnchor?> FindAsync()
    {
        Task<TaskbarAnchor?> task;
        lock(Gate){if(_pending is null || _pending.IsCompleted)_pending=Task.Run(Find);task=_pending;}
        // UI Automation runs outside the WPF thread and must never block opening the panel.
        if(await Task.WhenAny(task,Task.Delay(700))!=task)return null;
        var result=await task;if(result is not null)Cached=result;return result;
    }
    private static TaskbarAnchor? Find()
    {
        try
        {
            GetCursorPos(out var mouse);var candidates=new List<TaskbarAnchor>();
            foreach(var tray in Trays())
            {
                var root=AutomationElement.FromHandle(tray);
                var elements=root.FindAll(TreeScope.Descendants,System.Windows.Automation.Condition.TrueCondition);
                foreach(AutomationElement element in elements)
                {
                    var info=element.Current;
                    if(!info.AutomationId.Contains("DesktopNook.DesktopNook",StringComparison.OrdinalIgnoreCase) && !info.Name.Contains("番茄桌面",StringComparison.OrdinalIgnoreCase))continue;
                    if(info.ControlType!=ControlType.Button && info.ControlType!=ControlType.ListItem)continue;
                    var bounds=info.BoundingRectangle;if(bounds.IsEmpty || bounds.Width<5 || bounds.Height<5 || info.IsOffscreen)continue;
                    candidates.Add(Anchor(tray,bounds));
                }
            }
            return candidates.FirstOrDefault(c=>c.Tray.Contains(new System.Windows.Point(mouse.X,mouse.Y)))??candidates.FirstOrDefault();
        }
        catch{return null;}
    }
    internal static Rect Calculate(TaskbarAnchor anchor,double logicalWidth,double logicalHeight)
    {
        var scale=anchor.Dpi/96;var width=logicalWidth*scale;var height=logicalHeight*scale;
        var fit=Math.Min(1,Math.Min(anchor.Work.Width/width,anchor.Work.Height/height));width*=fit;height*=fit;
        var x=anchor.Button.Left;var y=anchor.Tray.Top-height-8*scale;
        if(anchor.Tray.Bottom<=anchor.Work.Top+1)y=anchor.Tray.Bottom+8*scale;
        else if(anchor.Tray.Right<=anchor.Work.Left+1){x=anchor.Tray.Right+8*scale;y=anchor.Button.Top;}
        else if(anchor.Tray.Left>=anchor.Work.Right-1){x=anchor.Tray.Left-width-8*scale;y=anchor.Button.Top;}
        x=Math.Clamp(x,anchor.Work.Left,anchor.Work.Right-width);y=Math.Clamp(y,anchor.Work.Top,anchor.Work.Bottom-height);
        return new Rect(x,y,width,height);
    }
    internal static Rect WorkArea(Window window)
    {
        GetWindowRect(new WindowInteropHelper(window).Handle,out var rect);var info=new MonitorInfo{Size=(uint)Marshal.SizeOf<MonitorInfo>()};
        if(GetMonitorInfo(MonitorFromRect(ref rect,2),ref info)){double scale=GetDpiForWindow(new WindowInteropHelper(window).Handle)/96d;return new Rect(info.Work.Left/scale,info.Work.Top/scale,(info.Work.Right-info.Work.Left)/scale,(info.Work.Bottom-info.Work.Top)/scale);}
        return SystemParameters.WorkArea;
    }
    public static void Apply(Window window,TaskbarAnchor anchor,Size? preferred=null)
    {
        var size=preferred??new Size(WindowSizePreference.BaseWidth,WindowSizePreference.BaseHeight);
        var rect=Calculate(anchor,size.Width,size.Height);
        SetWindowPos(new WindowInteropHelper(window).Handle,IntPtr.Zero,(int)Math.Round(rect.X),(int)Math.Round(rect.Y),(int)Math.Round(rect.Width),(int)Math.Round(rect.Height),0x14);
    }
    // 读取/恢复用户手动拖动后的位置。GetWindowRect 是物理像素，窗口的 Left/Top 是逻辑像素。
    public static Rect Bounds(Window window)
    {
        var handle=new WindowInteropHelper(window).Handle;GetWindowRect(handle,out var rect);
        double scale=GetDpiForWindow(handle)/96d;if(scale<=0)scale=1;
        return new Rect(rect.Left/scale,rect.Top/scale,(rect.Right-rect.Left)/scale,(rect.Bottom-rect.Top)/scale);
    }
    public static void Move(Window window,double left,double top)
    {
        var handle=new WindowInteropHelper(window).Handle;
        double scale=GetDpiForWindow(handle)/96d;if(scale<=0)scale=1;
        SetWindowPos(handle,IntPtr.Zero,(int)Math.Round(left*scale),(int)Math.Round(top*scale),0,0,0x15); // NOSIZE|NOZORDER|NOACTIVATE
    }
}
