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
            return created>0?0:1;
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
            return store.State.Items.Count==0?0:4;
        }
        catch(Exception ex){try{var data=sandbox?Path.Combine(root,"Data"):root;Directory.CreateDirectory(data);File.WriteAllText(Path.Combine(data,"maintenance-error.json"),JsonSerializer.Serialize(new{message=ex.Message,hresult=ex.HResult,command,time=DateTimeOffset.Now},new JsonSerializerOptions{WriteIndented=true}));}catch{}return 5;}
        finally{if(owned)mutex.ReleaseMutex();}
    }
}
