using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace DesktopNook.App;

internal sealed class UpdatePlan
{
    public string Target {get;set;}="";
    public string Candidate {get;set;}="";
    public string CandidateHash {get;set;}="";
    public string PreviousHash {get;set;}="";
    public string Version {get;set;}="";
    public string DataRoot {get;set;}="";
    public string? Sandbox {get;set;}
    public bool Installer {get;set;}
    public int ProcessId {get;set;}
}
internal static class LocalUpdater
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    internal static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file));}
    internal static (Version Version,bool Installer) Inspect(string path)
    {
        var info=FileVersionInfo.GetVersionInfo(path);
        if(info.ProductName is not ("DesktopNook" or "番茄桌面"))throw new IOException("请选择番茄桌面的新版本程序或安装包。");
        if(!Version.TryParse(info.FileVersion,out var version))throw new IOException("无法读取所选程序版本。");
        version=new Version(version.Major,version.Minor,Math.Max(0,version.Build),Math.Max(0,version.Revision));
        return(version,info.FileDescription?.Contains("安装程序")==true);
    }
    public static (string Helper,string Plan,string Version) Stage(string source,string target,string dataRoot,string? sandbox=null,int? processId=null)
    {
        source=Path.GetFullPath(source);target=Path.GetFullPath(target);
        if(source.Equals(target,StringComparison.OrdinalIgnoreCase))throw new IOException("请选择其他位置的新版本文件。");
        var candidate=Inspect(source);var current=Inspect(target);
        if(candidate.Version<current.Version)throw new IOException("所选版本较旧，请选择当前版本或更新版本。");
        if(candidate.Installer && (!Path.GetFileName(target).Equals("DesktopNook.exe",StringComparison.OrdinalIgnoreCase)||!File.Exists(Path.Combine(Path.GetDirectoryName(target)!,"卸载番茄桌面.exe"))))throw new IOException("当前是免安装版，请选择新版番茄桌面 EXE；安装版可选择安装包覆盖升级。");
        var dir=Path.Combine(dataRoot,"updates",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        var staged=Path.Combine(dir,candidate.Installer?"package.exe":"candidate.exe");var helper=Path.Combine(dir,"DesktopNook.Updater.exe");
        DesktopNook.Core.NookStore.CopyPreservingStreams(source,staged);DesktopNook.Core.NookStore.CopyPreservingStreams(Environment.ProcessPath!,helper);
        if(Hash(staged)!=Hash(source))throw new IOException("更新文件校验失败，当前程序未更改。");
        // Save metadata only; never duplicate or relocate managed user files/folders during updates.
        var metadata=Path.Combine(dir,"metadata-backup");Directory.CreateDirectory(metadata);
        foreach(var name in new[]{"state.json","state.json.bak","migration.json"}){var file=Path.Combine(dataRoot,name);if(File.Exists(file))File.Copy(file,Path.Combine(metadata,name),false);}
        var vault=Path.Combine(dataRoot,"vault");if(Directory.Exists(vault))foreach(var folder in Directory.EnumerateDirectories(vault)){if(Guid.TryParseExact(Path.GetFileName(folder),"N",out _)&&File.Exists(Path.Combine(folder,"entry.json"))){var dest=Path.Combine(metadata,"vault",Path.GetFileName(folder));Directory.CreateDirectory(dest);File.Copy(Path.Combine(folder,"entry.json"),Path.Combine(dest,"entry.json"),false);}}
        var plan=new UpdatePlan{Target=target,Candidate=staged,CandidateHash=Hash(staged),PreviousHash=Hash(target),Version=candidate.Version.ToString(),DataRoot=Path.GetFullPath(dataRoot),Sandbox=sandbox,Installer=candidate.Installer,ProcessId=processId??Environment.ProcessId};
        var planFile=Path.Combine(dir,"update.json");File.WriteAllText(planFile,JsonSerializer.Serialize(plan,Json));
        return(helper,planFile,plan.Version);
    }
    public static int Apply(string planPath)
    {
        var log=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(planPath))!,"update-result.json");var backup="";var replaced=false;UpdatePlan? plan=null;
        try
        {
            plan=JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(planPath))??throw new IOException("更新计划无效。");
            var directory=Path.GetDirectoryName(Path.GetFullPath(planPath))!;
            if(!Path.GetDirectoryName(Path.GetFullPath(plan.Candidate))!.Equals(directory,StringComparison.OrdinalIgnoreCase)||Hash(plan.Candidate)!=plan.CandidateHash)throw new IOException("更新文件不完整，当前程序保留。");
            var candidate=Inspect(plan.Candidate);var current=Inspect(plan.Target);
            if(candidate.Version<current.Version||candidate.Installer!=plan.Installer||Hash(plan.Target)!=plan.PreviousHash)throw new IOException("当前程序或更新文件已发生变化，请重新选择更新。");
            if(plan.ProcessId>0){try{using var process=Process.GetProcessById(plan.ProcessId);if(!string.Equals(process.MainModule?.FileName,plan.Target,StringComparison.OrdinalIgnoreCase))throw new IOException("待退出程序身份不一致。");if(!process.WaitForExit(30000))throw new IOException("旧程序仍在运行，尚未更新。");}catch(ArgumentException){}}
            if(plan.Installer)
            {
                var info=new ProcessStartInfo(plan.Candidate){UseShellExecute=false,CreateNoWindow=true};info.ArgumentList.Add("/S");info.ArgumentList.Add("/D="+Path.GetDirectoryName(plan.Target));using var installer=Process.Start(info)??throw new IOException("未能启动更新包。");installer.WaitForExit();if(installer.ExitCode!=0)throw new IOException("更新包未完成，退出码 "+installer.ExitCode);
                if(Inspect(plan.Target).Version<candidate.Version)throw new IOException("安装包未更新到目标版本。");
            }
            else
            {
                // Rename first: handles Explorer mapping an old icon resource without overwriting it.
                backup=Path.Combine(Path.GetDirectoryName(plan.Target)!,".DesktopNook-previous-"+Guid.NewGuid().ToString("N")+".exe");
                File.Move(plan.Target,backup,false);
                try{DesktopNook.Core.NookStore.CopyPreservingStreams(plan.Candidate,plan.Target);if(Hash(plan.Target)!=plan.CandidateHash)throw new IOException("新版程序校验失败。");replaced=true;}
                catch{if(File.Exists(plan.Target))File.Move(plan.Target,plan.Target+".failed-"+Guid.NewGuid().ToString("N"));File.Move(backup,plan.Target);backup="";throw;}
                var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\DesktopNook",true);
                using(key){if(string.Equals(key?.GetValue("InstallLocation") as string,Path.GetDirectoryName(plan.Target),StringComparison.OrdinalIgnoreCase))key!.SetValue("DisplayVersion",plan.Version);}
            }
            var start=new ProcessStartInfo(plan.Target){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(plan.Target)!};
            if(plan.Sandbox is not null){start.ArgumentList.Add("--sandbox");start.ArgumentList.Add(plan.Sandbox);}
            start.ArgumentList.Add("--update-ready");var ready=Path.Combine(directory,"ready.json");start.ArgumentList.Add(ready);
            using var updated=Process.Start(start)??throw new IOException("新版程序未能启动，备份已保留。");
            var confirmed=false;for(var i=0;i<100;i++){if(File.Exists(ready)){confirmed=true;break;}if(updated.HasExited)break;Thread.Sleep(100);}
            File.WriteAllText(log,JsonSerializer.Serialize(new{success=confirmed,version=plan.Version,replaced,backup,processId=updated.Id,reason=confirmed?"":"新版未确认就绪，旧程序备份保留"},Json));
            return confirmed?0:2;
        }
        catch(Exception ex)
        {
            File.WriteAllText(log,JsonSerializer.Serialize(new{success=false,error=ex.Message,backup,replaced},Json));
            System.Windows.MessageBox.Show("更新未完成：\n"+ex.Message+"\n\n收纳数据保留。","番茄桌面更新");return 1;
        }
    }
}
