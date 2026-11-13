using System.IO;
using System.Text.Json;
using System.Diagnostics;
using DesktopNook.Core;

namespace DesktopNook.App;
internal static class Maintenance
{
    public static int Run(string[] args,string root,string signature,bool sandbox)
    {
        var command=args.Single(a=>a.StartsWith("--maintenance="))[14..];
        // 安装器已先自行创建桌面/开始菜单入口；这里只补 AppUserModelID 与自启路径，
        // 任何失败都不应让安装流程（含静默安装）中断或弹出错误框。
        if(command=="shortcut")
        {
            if(sandbox)return 0;
            var created=0;try{created=ShortcutInstaller.InstallAll().Length;}catch{}
            try{new StartupOptions().RefreshEnabledPath();}catch{}
            // Shell 注册放到独立 STA 线程并限时等待：资源管理器繁忙时 Shell.Application 的
            // Verbs() / InvokeVerb 会同步阻塞且没有超时，直接跑在主线程会让安装器的
            // ExecWait 永远等下去。限时失败只影响「快速访问固定」这个尽力而为的入口，
            // 导航窗格注册表项与快捷方式都已就位，安装不应被拖住。
            var reportPath=Path.Combine(root,"nav-pin-report.json");
            try{Directory.CreateDirectory(root);File.WriteAllText(reportPath,"{\"status\":\"pending\"}");}catch{}
            var worker=new Thread(()=>
            {
                bool? pinned=null;
                try
                {
                    var desktops=new[]{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)};
                    // 安装完成即建立资源管理器「番茄桌面」入口：生成展示目录、同步快捷方式、固定到快速访问。
                    pinned=ExplorerNavService.Register(root,new NookStore(root,desktops,new WindowsRecycleBin()));
                }
                catch{}
                // 留下可回读的诊断：安装后若入口没出现，能一眼看出是动词失败还是 Explorer 缓存。
                try{File.WriteAllText(reportPath,ExplorerNavService.Diagnose(root,pinned));}catch{}
            }){IsBackground=true};
            worker.SetApartmentState(System.Threading.ApartmentState.STA);
            worker.Start();
            if(!worker.Join(8000))
                try{File.WriteAllText(reportPath,JsonSerializer.Serialize(new{status="timeout",phase="shell-register",hint="导航窗格注册表项通常已写入；快速访问固定可能未生效，可运行 --maintenance=nav-pin 重试"}));}catch{}
            return created>0?0:1;
        }
        // 只刷新展示目录图标（desktop.ini + ico），不动快捷方式、不重新固定快速访问。
        // 升级后不重装也能立即换图标，也便于单独排障。
        if(command=="nav-icon")
        {
            var data=sandbox?Path.Combine(root,"Data"):root;
            try{return ExplorerNavService.EnsureFolderIcon(data)?0:1;}catch{return 1;}
        }
        if(command=="nav-diagnose")
        {
            var data=sandbox?Path.Combine(root,"Data"):root;
            try{Directory.CreateDirectory(data);File.WriteAllText(Path.Combine(data,"nav-pin-report.json"),ExplorerNavService.Diagnose(data));return 0;}catch{return 1;}
        }
        // 不重装也能重做一次固定 / 取消固定，并回读 Shell 动词状态。
        if(command is "nav-pin" or "nav-unpin")
        {
            var data=sandbox?Path.Combine(root,"Data"):root;
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
            try{var identity=File.ReadAllLines(Path.Combine(root,"instance.pid"));var candidate=Process.GetProcessById(int.Parse(identity[0]));if(candidate.Id!=Environment.ProcessId&&string.Equals(candidate.MainModule?.FileName,identity[1],StringComparison.OrdinalIgnoreCase))running=candidate;}catch{}
            try{using var stop=EventWaitHandle.OpenExisting("Local\\DesktopNook-Stop-"+signature);stop.Set();}catch(WaitHandleCannotBeOpenedException){}
            try{owned=mutex.WaitOne(10000);}catch(AbandonedMutexException){owned=true;}
            if(!owned)return 2;
            if(running is not null && !running.WaitForExit(10000))return 2;
            if(command=="stop")return 0;
            var data=sandbox?Path.Combine(root,"Data"):root;
            if(command=="status")return 0;
            if(command!="restore"&&command!="verify-restored")return 3;
            var desktops=sandbox?new[]{Path.Combine(root,"Desktop")}:new[]{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)};
            IRecycleBin bin=sandbox?new SandboxRecycle(root):new WindowsRecycleBin();
            var store=new NookStore(data,desktops,bin);var failures=new List<string>();
            if(command=="verify-restored")return store.State.Items.Count==0?0:6;
            foreach(var item in store.State.Items.ToArray()){try{store.Restore(item.Id,true);}catch(Exception ex){failures.Add(item.Name+": "+ex.Message);try{store=new(data,desktops,bin);}catch{break;}}}
            File.WriteAllText(Path.Combine(data,"maintenance-report.json"),JsonSerializer.Serialize(new{remaining=store.State.Items.Count,failures},new JsonSerializerOptions{WriteIndented=true}));
            // 卸载前取消快速访问固定并清掉只含快捷方式的展示目录，真实数据保留。
            try{ExplorerNavService.Unregister(data);}catch{}
            return store.State.Items.Count==0?0:4;
        }
        catch(Exception ex){try{var data=sandbox?Path.Combine(root,"Data"):root;Directory.CreateDirectory(data);File.WriteAllText(Path.Combine(data,"maintenance-error.json"),JsonSerializer.Serialize(new{message=ex.Message,hresult=ex.HResult,command,time=DateTimeOffset.Now},new JsonSerializerOptions{WriteIndented=true}));}catch{}return 5;}
        finally{if(owned)mutex.ReleaseMutex();}
    }
}
