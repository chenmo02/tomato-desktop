using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DesktopNook.App;

/// <summary>Real OLE/mouse roundtrip using two owned windows and a test-only desktop root.</summary>
internal static class OleDragVerification
{
    [StructLayout(LayoutKind.Sequential)]private struct CursorPoint{public int X,Y;}
    [DllImport("user32.dll")]private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]private static extern IntPtr WindowFromPoint(CursorPoint point);
    [DllImport("user32.dll")]private static extern IntPtr GetAncestor(IntPtr window,uint flag);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll")]private static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
    private static void Move(Point point)=>SetCursorPos((int)point.X,(int)point.Y);
    public static async Task Run(MainWindow main,string kind="shortcut")
    {
        if(!App.IsSandbox)throw new InvalidOperationException("OLE verification requires sandbox.");
        GetCursorPos(out var originalCursor);
        var hostLeft=main.Left>=225?main.Left-225:Math.Min(SystemParameters.WorkArea.Right-220,main.Left+main.Width+16);
        var host=new Window{Title="番茄桌面隔离拖放验证",Width=210,Height=105,Left=hostLeft,Top=Math.Max(10,main.Top),ShowInTaskbar=false,Topmost=true,ResizeMode=ResizeMode.NoResize};
        var source=new Button{Content="独立测试目录",FontSize=12};host.Content=source;
        try
        {
            main.ShowPanel();host.Show();await main.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            SetWindowPos(new WindowInteropHelper(main).Handle,(IntPtr)(-1),0,0,0,0,0x43);
            SetWindowPos(new WindowInteropHelper(host).Handle,(IntPtr)(-1),0,0,0,0,0x43);
            SetForegroundWindow(new WindowInteropHelper(host).Handle);
            await Task.Delay(350);
            ShellIntegration.VerificationDesktopHandle=new WindowInteropHelper(host).Handle;
            var file=Path.Combine(App.Store.DesktopRoots[0],kind=="folder"?"真实文件夹拖放验证":kind=="file"?"真实文档拖放验证.txt":"真实拖放验证.url");
            if(kind=="folder"){Directory.CreateDirectory(file);File.WriteAllText(Path.Combine(file,"data.txt"),"folder drag data");}
            else File.WriteAllText(file,kind=="file"?"document drag data":"[InternetShortcut]\r\nURL=https://example.invalid/drag-verification\r\n");
            var sourcePoint=source.PointToScreen(new Point(source.ActualWidth/2,source.ActualHeight/2));
            var targetPoint=main.DropPoint;
            if(GetAncestor(WindowFromPoint(new CursorPoint{X=(int)sourcePoint.X,Y=(int)sourcePoint.Y}),2)!=new WindowInteropHelper(host).Handle)throw new Exception("验证源窗口未处于可交互位置。");
            if(GetAncestor(WindowFromPoint(new CursorPoint{X=(int)targetPoint.X,Y=(int)targetPoint.Y}),2)!=new WindowInteropHelper(main).Handle)throw new Exception("验证目标窗口未处于可交互位置。");
            Move(sourcePoint);mouse_event(2,0,0,0,UIntPtr.Zero);await Task.Delay(100);
            if((GetAsyncKeyState(1)&0x8000)==0)throw new Exception("验证输入未建立鼠标按下状态。");
            var incomingPointer=Task.Run(()=>{try{Thread.Sleep(700);for(var i=1;i<=20;i++){Move(new Point(sourcePoint.X+(targetPoint.X-sourcePoint.X)*i/20,sourcePoint.Y+(targetPoint.Y-sourcePoint.Y)*i/20));Thread.Sleep(40);}Thread.Sleep(250);mouse_event(4,0,0,0,UIntPtr.Zero);}finally{mouse_event(4,0,0,0,UIntPtr.Zero);}});
            var data=new DataObject(DataFormats.FileDrop,new[]{file});
            var result=DragDrop.DoDragDrop(source,data,DragDropEffects.Copy|DragDropEffects.Move|DragDropEffects.Link);
            await incomingPointer;await main.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            var item=App.Store.State.Items.SingleOrDefault(i=>i.OriginalPath==file)??throw new Exception("真实 OLE FileDrop 未触发收纳，结果："+result);
            if(File.Exists(file)||Directory.Exists(file))throw new Exception("收纳后测试桌面仍有原入口。");
            main.UpdateLayout();var button=main.IconButton(item.Id);
            var from=button.PointToScreen(new Point(button.ActualWidth/2,button.ActualHeight/2));
            var to=source.PointToScreen(new Point(source.ActualWidth/2,source.ActualHeight/2));
            await Task.Run(()=>
            {
                try
                {
                    Move(from);Thread.Sleep(80);mouse_event(2,0,0,0,UIntPtr.Zero);Thread.Sleep(70);
                    for(var i=1;i<=20;i++){Move(new Point(from.X+(to.X-from.X)*i/20,from.Y+(to.Y-from.Y)*i/20));Thread.Sleep(25);}
                    Thread.Sleep(60);mouse_event(4,0,0,0,UIntPtr.Zero);
                }
                finally{mouse_event(4,0,0,0,UIntPtr.Zero);}
            });
            await main.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            if((!File.Exists(file)&&!Directory.Exists(file))||App.Store.State.Items.Any(i=>i.Id==item.Id))throw new Exception("真实鼠标拖出未完成独立测试桌面的恢复。");
        }
        finally
        {
            ShellIntegration.VerificationDesktopHandle=IntPtr.Zero;mouse_event(4,0,0,0,UIntPtr.Zero);host.Close();SetCursorPos(originalCursor.X,originalCursor.Y);main.SelectCategory(null);
        }
    }
}
