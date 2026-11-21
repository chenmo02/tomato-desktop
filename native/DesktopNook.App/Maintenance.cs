using System.IO;
using System.Text.Json;
using System.Diagnostics;
using DesktopNook.Core;

namespace DesktopNook.App;
internal static class Maintenance
{
    public static int Run(string[] args,string root,string data,string signature,bool sandbox)
    {
        var command=args.Single(a=>a.StartsWith("--maintenance="))[14..];
        // 安装器已先自行创建桌面/开始菜单入口；这里只补 AppUserModelID 与自启路径，
        // 任何失败都不应让安装流程（含静默安装）中断或弹出错误框。
        if(command=="shortcut")
        {
            try{App.Options.RefreshEnabledPath();}catch{}
            if(sandbox)return 0;
            // 安装时注册资源管理器入口。收纳快捷方式同步和 Shell 固定由应用启动后处理。
            try
            {
                if(Directory.Exists(data))
                {
                    var report=Path.Combine(data,"nav-pin-report.json");
                    if(!ExplorerNavService.NamespacePresent()||!File.Exists(report))
                        File.WriteAllText(report,"{\"status\":\"pending\"}");
                    ExplorerNavService.EnsureFolderIcon(data);
                    ExplorerNavService.RegisterNamespace(data);
                }
            }
            catch{}
            var created=0;try{created=ShortcutInstaller.InstallAll().Length;}catch{}
            return created>0?0:1;
        }
        // 只刷新展示目录图标（desktop.ini + ico），不动快捷方式、不重新固定快速访问。
        // 升级后不重装也能立即换图标，也便于单独排障。
        if(command=="nav-icon")
        {
            try{return ExplorerNavService.EnsureFolderIcon(data)?0:1;}catch{return 1;}
        }
        if(command=="nav-diagnose")
        {
            try{Directory.CreateDirectory(data);File.WriteAllText(Path.Combine(data,"nav-pin-report.json"),ExplorerNavService.Diagnose(data));return 0;}catch{return 1;}
        }
        // 不重装也能重做一次固定 / 取消固定，并回读 Shell 动词状态。
        if(command is "nav-pin" or "nav-unpin")
        {
            try
            {
                Directory.CreateDirectory(data);
                var ok=ExplorerNavService.PinFolder(data,command=="nav-pin");
                File.WriteAllText(Path.Combine(data,"nav-pin-report.json"),ExplorerNavService.Diagnose(data,ok));
                return ok?0:1;
            }
            catch{return 1;}
        }
        using var mutex=new Mutex(false,"Local\\DesktopNook-"+signature);bool owned=false;
        try
        {
            Process? running=null;
            try{var pid=Path.Combine(root,"instance.pid");if(!sandbox&&!File.Exists(pid))pid=Path.Combine(ApplicationPaths.LegacyData,"instance.pid");var identity=File.ReadAllLines(pid);var candidate=Process.GetProcessById(int.Parse(identity[0]));if(candidate.Id!=Environment.ProcessId&&string.Equals(candidate.MainModule?.FileName,identity[1],StringComparison.OrdinalIgnoreCase))running=candidate;}catch{}
            try{using var stop=EventWaitHandle.OpenExisting("Local\\DesktopNook-Stop-"+signature);stop.Set();}catch(WaitHandleCannotBeOpenedException){}
            try{owned=mutex.WaitOne(10000);}catch(AbandonedMutexException){owned=true;}
            if(!owned)return 2;
            if(running is not null && !running.WaitForExit(10000))return 2;
            if(command=="stop")return 0;
            if(command=="uninstall")
            {
                App.Options.SuspendAutostartForUninstall();
                ExplorerNavService.UnregisterNavigation(data);
                return 0;
            }
            if(command=="status")return 0;
            if(command!="restore"&&command!="verify-restored")return 3;
            var desktops=sandbox?new[]{Path.Combine(root,"Desktop")}:new[]{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)};
            IRecycleBin bin=sandbox?new SandboxRecycle(root):new WindowsRecycleBin();
            var store=new NookStore(data,desktops,bin);var failures=new List<string>();
            if(command=="verify-restored")return store.State.Items.Count==0?0:6;
            foreach(var item in store.State.Items.ToArray()){try{store.Restore(item.Id,true);}catch(Exception ex){failures.Add(item.Name+": "+ex.Message);try{store=new(data,desktops,bin);}catch{break;}}}
            File.WriteAllText(Path.Combine(data,"maintenance-report.json"),JsonSerializer.Serialize(new{remaining=store.State.Items.Count,failures},new JsonSerializerOptions{WriteIndented=true}));
            // 兼容旧版卸载器的显式恢复命令。
            try{ExplorerNavService.Unregister(data);}catch{}
            return store.State.Items.Count==0?0:4;
        }
        catch(Exception ex){try{Directory.CreateDirectory(data);File.WriteAllText(Path.Combine(data,"maintenance-error.json"),JsonSerializer.Serialize(new{message=ex.Message,hresult=ex.HResult,command,time=DateTimeOffset.Now},new JsonSerializerOptions{WriteIndented=true}));}catch{}return 5;}
        finally{if(owned)mutex.ReleaseMutex();}
    }
}
