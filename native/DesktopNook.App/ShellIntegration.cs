using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopNook.Core;

namespace DesktopNook.App;

internal static class ShellIntegration
{
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path,uint attributes,ref ShellFileInfo info,uint size,uint flags);
    [DllImport("shell32.dll",EntryPoint="SHGetFileInfoW")] private static extern IntPtr SHGetFileInfoPidl(IntPtr pidl,uint attributes,ref ShellFileInfo info,uint size,uint flags);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern int SHParseDisplayName(string name,IntPtr bind,out IntPtr pidl,uint attributes,out uint found);
    [DllImport("shell32.dll")] private static extern IntPtr ILCombine(IntPtr parent,IntPtr child);
    [DllImport("shell32.dll")] private static extern int SHGetNameFromIDList(IntPtr pidl,uint name,out IntPtr text);
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint events,uint flags,IntPtr item,IntPtr second);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointNative point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(PointNative point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd,uint flag);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd,StringBuilder name,int count);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback,IntPtr state);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent,IntPtr after,string? className,string? name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd,uint msg,IntPtr wp,IntPtr lp,uint flags,uint timeout,out IntPtr result);
    private delegate bool EnumCallback(IntPtr hwnd,IntPtr param);
    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X,Y; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct ShellFileInfo { public IntPtr Icon; public int Index; public uint Attributes; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string Display; [MarshalAs(UnmanagedType.ByValTStr,SizeConst=80)] public string Type; }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,ImageSource> Icons = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,bool> Loading=new();
    // 携带缓存键：一个图标解码完成后只通知对应控件，避免「一个图标就绪 → 上百个控件全部刷新」的 N² 广播风暴。
    public static event Action<string>? IconReady;
    internal static IntPtr VerificationDesktopHandle { get; set; }
    public static void SetAppId(string id) => Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(id));
    public static ImageSource AppIcon { get; } = new BitmapImage(new Uri("pack://application:,,,/Assets/AppIcon.png"));
    private static readonly System.Collections.Concurrent.BlockingCollection<(string Path,string Key,int Pixels,bool Fast)> IconJobs=new();
    internal static string IconKey(string path,bool recycle,int pixels,bool fast)
    {
        string resolved=recycle&&!fast?"::"+WindowsRecycleBin.RecycleId:path;
        return (fast?"fast|":"")+resolved+"|"+pixels;
    }
    static ShellIntegration()
    {
        AppIcon.Freeze();
        // 两条 STA 线程并行解码：图标库首屏通常有几十个条目，单线程排队会让占位图标停留过久。
        for(int i=0;i<2;i++)
        {
            int index=i;
            var worker=new Thread(()=>
            {
                foreach(var job in IconJobs.GetConsumingEnumerable())
                {
                    try{Icons[job.Key]=(job.Fast?SharpIcons.LoadFast(job.Path,job.Pixels):SharpIcons.Load(job.Path,job.Pixels))??AppIcon;}
                    catch {Icons[job.Key]=AppIcon;}
                    finally{Loading.TryRemove(job.Key,out _);IconReady?.Invoke(job.Key);}
                }
            }){IsBackground=true,Name="DesktopNook shell icons "+(index+1)};
            worker.SetApartmentState(ApartmentState.STA);worker.Start();
        }
    }
    public static ImageSource IconFor(string path,bool recycle=false,int pixels=96)
    {
        pixels=Math.Clamp(pixels,16,1024);
        string resolved=recycle?"::"+WindowsRecycleBin.RecycleId:path;
        string key=IconKey(resolved,false,pixels,false);
        if(Icons.TryGetValue(key,out var cached))return cached;
        if(Loading.TryAdd(key,true))IconJobs.Add((resolved,key,pixels,false));
        return AppIcon;
    }
    public static ImageSource IconForFast(string path,int pixels=96)
    {
        pixels=Math.Clamp(pixels,16,1024);
        string key=IconKey(path,false,pixels,true);
        if(Icons.TryGetValue(key,out var cached))return cached;
        if(Loading.TryAdd(key,true))IconJobs.Add((path,key,pixels,true));
        return AppIcon;
    }
    public static void Launch(NookStore store,NookItem item)
    {
        if(item.Kind=="recycle-bin"){Process.Start(new ProcessStartInfo("explorer.exe","shell:::"+WindowsRecycleBin.RecycleId){UseShellExecute=true});return;}
        var path=store.ManagedPath(item);
        if(!File.Exists(path)&&!Directory.Exists(path))throw new FileNotFoundException("托管项目不存在，请打开数据目录检查备份。",path);
        var start=new ProcessStartInfo(path){UseShellExecute=true};
        // 不强制改写工作目录：快捷方式沿用自身记录的「起始位置」，文件/文件夹用系统默认
        // （通常是 exe 所在目录）。旧实现把工作目录固定成原桌面路径，搬进 vault 的 exe
        // 与文档可能因找不到同目录资源而静默退出，表现为「双击没反应」。
        // 「全部图标」生成的 UWP 快捷方式不带起始位置，显式指向托管目录。
        if(item.Kind=="app")
        {
            var dir=Path.GetDirectoryName(path);
            if(!string.IsNullOrEmpty(dir)&&Directory.Exists(dir))start.WorkingDirectory=dir;
        }
        Process.Start(start);
    }
    private static string ClassName(IntPtr window) { var text = new StringBuilder(256); GetClassName(window,text,text.Capacity); return text.ToString(); }
    public static bool IsDesktopUnderCursor()
    {
        if(!GetCursorPos(out var point)) return false;
        var window = WindowFromPoint(point); var rootHandle=GetAncestor(window,2);
        if(App.IsSandbox && VerificationDesktopHandle!=IntPtr.Zero && rootHandle==VerificationDesktopHandle)return true;
        var root = ClassName(rootHandle);
        if(root is not ("Progman" or "WorkerW")) return false;
        // Only Explorer's desktop view/desktop background; never an Explorer folder window.
        for(var current=window;current!=IntPtr.Zero;current=GetParent(current))
            if(ClassName(current) is "SHELLDLL_DefView" or "SysListView32" or "Progman") return true;
        return root == "WorkerW";
    }
    public static void RefreshDesktop()
    {
        SHChangeNotify(0x08000000,0x1000,IntPtr.Zero,IntPtr.Zero);
        EnumWindows((window,_)=>{
            var view = FindWindowEx(window,IntPtr.Zero,"SHELLDLL_DefView",null);
            if(view!=IntPtr.Zero) SendMessageTimeout(view,0x111,(IntPtr)0x7103,IntPtr.Zero,2,1000,out _);
            return true;
        },IntPtr.Zero);
    }
    public static bool ContainsRecycleBin(IDataObject data)
    {
        if(!data.GetDataPresent("Shell IDList Array")) return false;
        if(data.GetData("Shell IDList Array") is not MemoryStream stream || stream.Length > 1024*1024) return false;
        return ContainsRecycleBinCida(stream.ToArray());
    }
    public static bool ContainsRecycleBinCida(byte[] bytes)
    {
        if(bytes.Length<12) return false; var count=BitConverter.ToUInt32(bytes,0);
        if(count is 0 or >200 || (count+2)*4>bytes.Length) return false;
        static byte[]? Pidl(byte[] b,uint offset)
        {
            var start=(long)offset; var cursor=start;
            while(cursor+2<=b.Length)
            {
                var size=BitConverter.ToUInt16(b,(int)cursor); if(size==0) return b[(int)start..((int)cursor+2)];
                if(size<2 || cursor+size>b.Length) return null; cursor+=size;
            }
            return null;
        }
        var parent=Pidl(bytes,BitConverter.ToUInt32(bytes,4)); if(parent is null) return false;
        for(var i=0;i<count;i++)
        {
            var child=Pidl(bytes,BitConverter.ToUInt32(bytes,8+i*4)); if(child is null) continue;
            var p=Marshal.AllocCoTaskMem(parent.Length); var c=Marshal.AllocCoTaskMem(child.Length); IntPtr combined=IntPtr.Zero; IntPtr text=IntPtr.Zero;
            try
            {
                Marshal.Copy(parent,0,p,parent.Length);Marshal.Copy(child,0,c,child.Length);combined=ILCombine(p,c);
                if(combined!=IntPtr.Zero && SHGetNameFromIDList(combined,0x80028000,out text)==0 && Marshal.PtrToStringUni(text)?.StartsWith("::"+WindowsRecycleBin.RecycleId,StringComparison.OrdinalIgnoreCase)==true) return true;
            }
            finally { Marshal.FreeCoTaskMem(p);Marshal.FreeCoTaskMem(c);if(combined!=IntPtr.Zero)Marshal.FreeCoTaskMem(combined);if(text!=IntPtr.Zero)Marshal.FreeCoTaskMem(text); }
        }
        return false;
    }
    internal static byte[] RecyclePayloadForVerification()
    {
        Marshal.ThrowExceptionForHR(SHParseDisplayName("::"+WindowsRecycleBin.RecycleId,IntPtr.Zero,out var pidl,0,out _));
        try
        {
            var length=0;
            while(length<65536) {var size=(ushort)Marshal.ReadInt16(pidl,length);length+=size==0?2:size;if(size==0)break;}
            if(length>=65536)throw new IOException("系统图标 IDList 大小异常。");
            var bytes=new byte[14+length];BitConverter.GetBytes(1u).CopyTo(bytes,0);BitConverter.GetBytes(12u).CopyTo(bytes,4);BitConverter.GetBytes(14u).CopyTo(bytes,8);Marshal.Copy(pidl,bytes,14,length);return bytes;
        }
        finally{Marshal.FreeCoTaskMem(pidl);}
    }
}
