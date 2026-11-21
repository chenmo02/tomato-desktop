using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Resources;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using DesktopNook.Core;
using Microsoft.Win32;

namespace DesktopNook.App;

// 文件资源管理器导航入口：在数据目录下维护一个名为「番茄桌面」的展示文件夹，
// 里面为每项收纳内容生成一个 .lnk（指向 vault 里真正的托管文件），再把这个
// 文件夹固定到「快速访问」，左侧导航窗格就会和桌面 / 下载 / 文档并列出现。
//
// 用快捷方式而不是复制或硬链接：托管文件可能跨卷、也可能是文件夹，只有 .lnk
// 能保持「点开即打开原项目」且不破坏恢复清单。展示目录里没有任何用户原件。
internal static class ExplorerNavService
{
    internal const string FolderName = "番茄桌面";
    internal const string IconFileName = "AppIcon.ico";
    // Windows 11 22H2+ 的「固定到快速访问」Shell 动词已不可靠；用官方 Shell 命名空间
    // 注册把入口挂到「此电脑」/ 导航窗格，图标由 DefaultIcon 指定。GUID 固定，卸载可整键清理。
    internal const string NamespaceClsid="{73EEDBFB-AA07-4910-93E9-C18F51D09546}";
    private const string NamespaceClsidKey=@"Software\Classes\CLSID\"+NamespaceClsid;
    private const string NamespaceLinkKey=@"Software\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\"+NamespaceClsid;
    private const string DesktopIniName = "desktop.ini";
    private static readonly object IconGate=new();
    private static readonly object SyncGate=new();
    private static bool? _lastQuickPinOk;
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId,uint flags,IntPtr item1,IntPtr item2);

    internal static string FolderPath(string dataDirectory)=>Path.Combine(dataDirectory,FolderName);
    internal static string IconPath(string dataDirectory)=>Path.Combine(dataDirectory,IconFileName);
    internal static string DesktopIniPath(string dataDirectory)=>Path.Combine(FolderPath(dataDirectory),DesktopIniName);

    // Windows 只为带 System 属性的文件夹读取 desktop.ini。系统自带的桌面 / 下载 / 文档
    // 在导航窗格里各有图标，走的也是同一套 IconResource 机制；这里我们指向软件自己的
    // AppIcon.ico（16–256px 全尺寸），让「番茄桌面」不再显示默认黄色文件夹。
    // 图标文件放在数据目录而不是展示目录：点开入口时只看到收纳项的快捷方式。
    internal static string IconResourceOf(string dataDirectory)
    {
        var icon=IconPath(dataDirectory);
        if(File.Exists(icon)&&new FileInfo(icon).Length>6)return icon+",0";
        // 打包资源万一取不到，退回 EXE 内嵌的 Win32 图标（同样来自 Assets/AppIcon.ico）。
        var exe=Environment.ProcessPath;
        return string.IsNullOrEmpty(exe)?string.Empty:exe+",0";
    }

    // 幂等：只在 ico 缺失或 desktop.ini / 属性不符时重写。安装、启动、数据变更都会调用，
    // 因此从旧版（已固定但没图标）升级过来也能自动补齐，无需重新固定快速访问。
    internal static bool EnsureFolderIcon(string dataDirectory)
    {
        lock(IconGate)
        {
            var folder=FolderPath(dataDirectory);
            Directory.CreateDirectory(folder);
            var icon=IconPath(dataDirectory);
            if(!File.Exists(icon)||new FileInfo(icon).Length<6)
            {
                var bytes=LoadEmbeddedIcon();
                if(bytes is {Length:>6})try{File.WriteAllBytes(icon,bytes);}catch{}
            }
            var resource=IconResourceOf(dataDirectory);
            if(resource.Length==0)return false;
            var plain=resource[..resource.LastIndexOf(',')];
            var content="[.ShellClassInfo]\r\nIconResource="+resource+"\r\nIconFile="+plain+"\r\nIconIndex=0\r\n";
            // 纯 ASCII 路径用无 BOM 的 ANSI/UTF-8（Shell 解析最稳）；含中文路径改 UTF-16。
            var encoding=content.All(c=>c<128)?(Encoding)new UTF8Encoding(false):Encoding.Unicode;
            var ini=DesktopIniPath(dataDirectory);
            try
            {
                bool changed=!File.Exists(ini)||!string.Equals(File.ReadAllText(ini,encoding),content,StringComparison.Ordinal);
                if(changed)
                {
                    if(File.Exists(ini))File.SetAttributes(ini,FileAttributes.Normal);
                    File.WriteAllText(ini,content,encoding);
                    File.SetAttributes(ini,FileAttributes.Hidden|FileAttributes.System);
                }
                else
                {
                    var attrs=File.GetAttributes(ini);
                    if((attrs&(FileAttributes.Hidden|FileAttributes.System))!=(FileAttributes.Hidden|FileAttributes.System))
                        File.SetAttributes(ini,attrs|FileAttributes.Hidden|FileAttributes.System);
                }
                var dirAttrs=File.GetAttributes(folder);
                if((dirAttrs&FileAttributes.System)==0)File.SetAttributes(folder,dirAttrs|FileAttributes.System);
                NotifyFolder(folder);
                return true;
            }
            catch{return false;}
        }
    }

    private static byte[]? LoadEmbeddedIcon()
    {
        try
        {
            var info=Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AppIcon.ico",UriKind.Absolute));
            if(info?.Stream is not null)
            {
                using var ms=new MemoryStream();info.Stream.CopyTo(ms);info.Stream.Dispose();return ms.ToArray();
            }
        }
        catch{/* 维护模式下可能没有可用的 pack 资源解析器，走下面的程序集资源 */}
        try
        {
            var asm=typeof(ExplorerNavService).Assembly;
            var name=asm.GetManifestResourceNames().FirstOrDefault(n=>n.EndsWith(".g.resources",StringComparison.OrdinalIgnoreCase));
            if(name is null)return null;
            using var stream=asm.GetManifestResourceStream(name);
            if(stream is null)return null;
            using var reader=new ResourceReader(stream);
            foreach(DictionaryEntry entry in reader)
            {
                if(!string.Equals(entry.Key as string,"assets/appicon.ico",StringComparison.OrdinalIgnoreCase))continue;
                if(entry.Value is byte[] raw)return raw;
                if(entry.Value is Stream s){using var ms=new MemoryStream();s.CopyTo(ms);return ms.ToArray();}
            }
        }
        catch{}
        return null;
    }

    // 图标缓存不会自动感知 desktop.ini 变化：先通知目录已更新，再刷新全局关联。
    private static void NotifyFolder(string folder)
    {
        try
        {
            var ptr=Marshal.StringToHGlobalUni(folder);
            try{SHChangeNotify(0x00001000,0x0005,ptr,IntPtr.Zero);} // SHCNE_UPDATEDIR | SHCNF_PATHW
            finally{Marshal.FreeHGlobal(ptr);}
        }
        catch{}
        try{SHChangeNotify(0x08000000,0,IntPtr.Zero,IntPtr.Zero);}catch{}
    }

    // 卸载只清理 Windows 导航入口，保留数据目录及其中的文件。
    internal static void UnregisterNavigation(string dataDirectory)
    {
        UnregisterNamespace();
        var folder=FolderPath(dataDirectory);
        if(App.IsSandbox||!Directory.Exists(folder))return;
        // 旧 Windows 的快速访问固定是尽力清理，避免 Shell 忙碌时拖住卸载。
        try{ShellIntegration.RunStaAsync(()=>Pin(folder,false)).Wait(1500);}catch{}
    }

    // 旧版卸载器的恢复命令还会清理生成的展示目录。
    internal static void Unregister(string dataDirectory)
    {
        UnregisterNavigation(dataDirectory);
        var folder=FolderPath(dataDirectory);
        try
        {
            if(Directory.Exists(folder))
            {
                // 带 System / Hidden 属性的 desktop.ini 会让 Directory.Delete 失败，先复位。
                var ini=DesktopIniPath(dataDirectory);
                if(File.Exists(ini))File.SetAttributes(ini,FileAttributes.Normal);
                File.SetAttributes(folder,File.GetAttributes(folder)&~FileAttributes.System);
                Directory.Delete(folder,true);
            }
        }
        catch{}
        try
        {
            var icon=IconPath(dataDirectory);
            if(File.Exists(icon))File.Delete(icon);
        }
        catch{}
        try{SHChangeNotify(0x08000000,0,IntPtr.Zero,IntPtr.Zero);}catch{}
    }

    private static string? _lastSignature;

    // 每次数据变更后调用；内容签名未变时直接跳过，避免反复重写几十个 .lnk。
    // 返回写入的快捷方式数量，-1 表示本次被签名短路。
    internal static int Sync(string dataDirectory,NookStore store) =>
        Sync(dataDirectory,ShellIntegration.SnapshotCollectedItems(store));

    internal static int Sync(string dataDirectory,IReadOnlyList<CollectedShellItem> items)
    {
        lock(SyncGate)return SyncLinks(dataDirectory,items);
    }

    private static int SyncLinks(string dataDirectory,IReadOnlyList<CollectedShellItem> items)
    {
        var folder=FolderPath(dataDirectory);
        Directory.CreateDirectory(folder);
        // 升级场景：旧版已经把目录固定到快速访问，启动时靠这里补写图标，不必重新固定。
        EnsureFolderIcon(dataDirectory);
        var entries=new List<(string Link,string Target,string Name)>();
        foreach(var item in items)
        {
            var target=item.Path;
            if(target is null||!File.Exists(target)&&!Directory.Exists(target))continue;
            entries.Add((LinkName(item,entries),target,item.Name));
        }
        var signature=string.Join("\n",entries.Select(e=>e.Link+"|"+e.Target+"|"+e.Name));
        if(signature==_lastSignature&&entries.Count==Directory.EnumerateFiles(folder,"*.lnk").Count())return -1;
        _lastSignature=signature;

        var expected=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        object? shell=null;
        try
        {
            var type=Type.GetTypeFromProgID("WScript.Shell");
            if(type is null)return 0;
            shell=Activator.CreateInstance(type);
            dynamic ws=shell!;
            foreach(var entry in entries)
            {
                var link=Path.Combine(folder,entry.Link+".lnk");
                expected.Add(link);
                object? shortcut=null;
                try
                {
                    shortcut=ws.CreateShortcut(link);
                    dynamic sc=shortcut!;
                    sc.TargetPath=entry.Target;
                    sc.Arguments="";
                    sc.WorkingDirectory=Path.GetDirectoryName(entry.Target)??folder;
                    sc.IconLocation=entry.Target+",0";
                    sc.Description="番茄桌面 · "+entry.Name;
                    sc.Save();
                }
                catch{/* 单项失败不影响其余入口 */}
                finally{if(shortcut is not null&&Marshal.IsComObject(shortcut))Marshal.FinalReleaseComObject(shortcut);}
            }
        }
        finally{if(shell is not null&&Marshal.IsComObject(shell))Marshal.FinalReleaseComObject(shell);}
        // 收纳项被移回 / 归档 / 改名后，清掉目录里遗留的旧快捷方式。
        foreach(var stale in Directory.EnumerateFiles(folder,"*.lnk").Where(f=>!expected.Contains(f)).ToArray())
            try{File.Delete(stale);}catch{}
        return expected.Count;
    }

    private static string LinkName(CollectedShellItem item,List<(string Link,string Target,string Name)> taken)
    {
        var name=item.Name;
        foreach(var c in Path.GetInvalidFileNameChars())name=name.Replace(c,'_');
        name=name.Trim().TrimEnd('.', ' ');
        if(name.Length==0)name="项目-"+(item.Id.Length>8?item.Id[..8]:item.Id);
        var candidate=name;var index=2;
        while(taken.Any(t=>string.Equals(t.Link,candidate,StringComparison.OrdinalIgnoreCase)))
        {
            candidate=name+" ("+index+")";
            index++;
        }
        return candidate;
    }

    // 按微软 Shell 命名空间机制注册一个文件夹实例，使它作为独立项目出现在
    // 「此电脑」/ 左侧导航窗格。全部写在 HKCU（当前用户，无需管理员），卸载整键清理。
    internal static bool RegisterNamespace(string dataDirectory)
    {
        if(App.IsSandbox)return false;   // 自检绝不改用户注册表
        var folder=FolderPath(dataDirectory);
        Directory.CreateDirectory(folder);
        var icon=IconPath(dataDirectory);
        try
        {
            using var clsid=Registry.CurrentUser.CreateSubKey(NamespaceClsidKey,true);
            if(clsid is null)return false;
            clsid.SetValue("",FolderName,RegistryValueKind.String);
            clsid.SetValue("System.IsPinnedToNameSpaceTree",1,RegistryValueKind.DWord);
            using(var proc=clsid.CreateSubKey("InProcServer32",true))
            {
                if(proc is not null)
                {
                    proc.SetValue("",@"%SystemRoot%\System32\shell32.dll",RegistryValueKind.ExpandString);
                    proc.SetValue("ThreadingModel","Both",RegistryValueKind.String);
                }
            }
            using(var iconKey=clsid.CreateSubKey("DefaultIcon",true))
                iconKey?.SetValue("",icon+",0",RegistryValueKind.ExpandString);
            using(var instance=clsid.CreateSubKey("Instance",true))
            {
                if(instance is not null)
                {
                    instance.SetValue("CLSID","{0E5AAE11-A475-4c5b-AB00-C66DE400274E}",RegistryValueKind.String);
                    using var bag=instance.CreateSubKey("InitPropertyBag",true);
                    if(bag is not null)
                    {
                        bag.SetValue("Attributes",0x11,RegistryValueKind.DWord);
                        bag.SetValue("TargetFolderPath",folder,RegistryValueKind.ExpandString);
                    }
                }
            }
            using(var shellFolder=clsid.CreateSubKey("ShellFolder",true))
            {
                if(shellFolder is not null)
                {
                    shellFolder.SetValue("Attributes",unchecked((int)0xF080004D),RegistryValueKind.DWord);
                    shellFolder.SetValue("FolderValueFlags",0x29,RegistryValueKind.DWord);
                }
            }
            using var link=Registry.CurrentUser.CreateSubKey(NamespaceLinkKey,true);
            if(link is null)return false;
            link.SetValue("",FolderName,RegistryValueKind.String);
            try{SHChangeNotify(0x08000000,0,IntPtr.Zero,IntPtr.Zero);}catch{}
            return true;
        }
        catch{return false;}
    }

    internal static void UnregisterNamespace()
    {
        if(App.IsSandbox)return;
        try{Registry.CurrentUser.DeleteSubKeyTree(NamespaceClsidKey,false);}catch{}
        try{Registry.CurrentUser.DeleteSubKeyTree(NamespaceLinkKey,false);}catch{}
        try{SHChangeNotify(0x08000000,0,IntPtr.Zero,IntPtr.Zero);}catch{}
    }

    // 从注册表回读，而不是只看「写入时没抛异常」。
    internal static bool NamespacePresent()
    {
        try
        {
            using var link=Registry.CurrentUser.OpenSubKey(NamespaceLinkKey);
            using var bag=Registry.CurrentUser.OpenSubKey(NamespaceClsidKey+@"\Instance\InitPropertyBag");
            return link is not null&&bag?.GetValue("TargetFolderPath") is string path&&path.Length>0;
        }
        catch{return false;}
    }

    // 只读诊断：把 Shell 实际暴露的文件夹动词与快速访问项导出，便于在真实机器上
    // 判断「固定」为什么没生效（动词改名 / COM 不可用 / 只是 Explorer 未刷新）。
    internal static string Diagnose(string dataDirectory,bool? pinResult=null)
    {
        var folder=FolderPath(dataDirectory);
        var verbs=new List<string>();
        var quick=new List<string>();
        var thisPc=new List<string>();
        object? shell=null;
        try
        {
            var type=Type.GetTypeFromProgID("Shell.Application");
            if(type is null)verbs.Add("progid-null");
            else
            {
                shell=Activator.CreateInstance(type);
                dynamic sh=shell!;
                try
                {
                    var ns=sh.NameSpace(folder);
                    if(ns is null)verbs.Add("namespace-null");
                    else foreach(var v in ns.Self.Verbs())verbs.Add(Convert.ToString(v.Name)??"");
                }
                catch(Exception ex){verbs.Add("verbs-error:"+ex.Message);}
                try
                {
                    var qa=sh.NameSpace("shell:::{679f85cb-0220-4080-b529-b7069910e810}");
                    if(qa is null)quick.Add("qa-namespace-null");
                    else foreach(var i in qa.Items())quick.Add(Convert.ToString(i.Path)??"");
                }
                catch(Exception ex){quick.Add("qa-error:"+ex.Message);}
                try
                {
                    var pc=sh.NameSpace(17); // ssfDRIVES = 此电脑
                    if(pc is null)thisPc.Add("pc-namespace-null");
                    else foreach(var i in pc.Items())thisPc.Add(Convert.ToString(i.Name)??"");
                }
                catch(Exception ex){thisPc.Add("pc-error:"+ex.Message);}
            }
        }
        catch(Exception ex){verbs.Add("shell-error:"+ex.Message);}
        finally{if(shell is not null&&Marshal.IsComObject(shell))Marshal.FinalReleaseComObject(shell);}
        return System.Text.Json.JsonSerializer.Serialize(new{
            folder,exists=Directory.Exists(folder),
            namespaceClsid=NamespaceClsid,namespacePresent=NamespacePresent(),
            quickAccessPin=_lastQuickPinOk,verbs,
            // 动词是 Shell 自己给出的状态：已固定时会出现「从快速访问取消固定」。
            verbPinned=verbs.Any(v=>v.Contains("从快速访问取消固定")||v.Contains("Unpin from Quick access",StringComparison.OrdinalIgnoreCase)),
            quickAccess=quick,thisPc=thisPc,
            thisPcHasNook=thisPc.Any(t=>t.Contains("番茄桌面")),pinResult},
            new System.Text.Json.JsonSerializerOptions{WriteIndented=true});
    }

    // 固定动词执行后，Shell 会异步刷新快速访问；进程立刻退出（安装器 ExecWait 只等进程）
    // 很容易把这次操作丢掉。这里执行后轮询「动词是否已翻转」来确认真生效，
    // 未生效再退回规范动词重试一次。已固定时动词是「从快速访问取消固定」，反之亦然。
    private static bool Pin(string folder,bool pin)
    {
        object? shell=null;
        // 动词轮询要有总预算：每次 IsPinnedState 都要新建 Shell.Application 并枚举 Verbs，
        // Explorer 响应慢时 24 轮会拖到几分钟，而快速访问固定本来就只是尽力而为。
        var clock=System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var type=Type.GetTypeFromProgID("Shell.Application");
            if(type is null)return false;
            shell=Activator.CreateInstance(type);
            dynamic sh=shell!;
            for(int attempt=0;attempt<2;attempt++)
            {
                var ns=sh.NameSpace(folder);
                if(ns is null)return false;
                dynamic self=ns.Self;
                if(attempt==0)
                {
                    bool invoked=false;
                    try
                    {
                        foreach(var v in self.Verbs())
                        {
                            var label=Convert.ToString(v.Name)?.Replace("&","")??"";
                            bool match=pin
                                ?label.Contains("固定到快速访问")||label.Contains("Pin to Quick access",StringComparison.OrdinalIgnoreCase)
                                :label.Contains("从快速访问取消固定")||label.Contains("Unpin from Quick access",StringComparison.OrdinalIgnoreCase);
                            if(match){v.DoIt();invoked=true;break;}
                        }
                    }
                    catch{/* 某些系统不暴露 Verbs 集合，退回规范动词 */}
                    if(!invoked)try{self.InvokeVerb(pin?"pintohome":"unpinfromhome");invoked=true;}catch{}
                    if(!invoked)break;
                }
                else
                {
                    try{self.InvokeVerb(pin?"pintohome":"unpinfromhome");}catch{}
                }
                for(int i=0;i<12;i++)
                {
                    if(clock.ElapsedMilliseconds>4000)break;
                    PumpWait(150);
                    if(IsPinnedState(folder,pin))return true;
                }
            }
            return IsPinnedState(folder,pin);
        }
        catch{return false;}
        finally{if(shell is not null&&Marshal.IsComObject(shell))Marshal.FinalReleaseComObject(shell);}
    }

    // 维护进程没有窗口，固定动词由 Explorer 异步执行，本进程只需让出时间片。
    // 原先用 Dispatcher.PushFrame + DispatcherTimer 等待：一旦 STA 线程被某个 COM
    // 调用阻塞，计时器再也不会 Tick，PushFrame 就永久挂死，安装器的 ExecWait 也随之卡死。
    private static void PumpWait(int milliseconds)=>System.Threading.Thread.Sleep(milliseconds);

    // 每次都新开 Shell 实例读动词，避免复用 Shell.Application 时拿到缓存的旧菜单。
    private static bool IsPinnedState(string folder,bool wantPinned)
    {
        object? shell=null;
        try
        {
            var type=Type.GetTypeFromProgID("Shell.Application");
            if(type is null)return false;
            shell=Activator.CreateInstance(type);
            dynamic sh=shell!;
            var ns=sh.NameSpace(folder);
            if(ns is null)return false;
            foreach(var v in ns.Self.Verbs())
            {
                var label=Convert.ToString(v.Name)?.Replace("&","")??"";
                bool unpin=label.Contains("从快速访问取消固定")||label.Contains("Unpin from Quick access",StringComparison.OrdinalIgnoreCase);
                bool doPin=label.Contains("固定到快速访问")||label.Contains("Pin to Quick access",StringComparison.OrdinalIgnoreCase);
                if(wantPinned&&unpin)return true;
                if(!wantPinned&&doPin)return true;
            }
        }
        catch{}
        finally{if(shell is not null&&Marshal.IsComObject(shell))Marshal.FinalReleaseComObject(shell);}
        return false;
    }

    // 供 --maintenance=nav-pin / nav-unpin 单独调用，不必重跑整个安装流程。
    internal static bool PinFolder(string dataDirectory,bool pin)
    {
        var folder=FolderPath(dataDirectory);
        Directory.CreateDirectory(folder);
        if(!pin)UnregisterNamespace();
        var namespaceOk=pin&&RegisterNamespace(dataDirectory);
        _lastQuickPinOk=Pin(folder,pin);
        try{SHChangeNotify(0x08000000,0,IntPtr.Zero,IntPtr.Zero);}catch{}
        return pin?(namespaceOk||_lastQuickPinOk==true):true;
    }
}
