using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace DesktopNook.App;
internal static class UpdateVerification
{
    public static int Run(string root,string oldExe,string candidate)
    {
        Directory.CreateDirectory(root);var profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DesktopNook.UpdateVerification-"+Guid.NewGuid().ToString("N"));var desktop=Path.Combine(profile,"Desktop");Directory.CreateDirectory(desktop);
        var data=Path.Combine(profile,"Data");var store=new DesktopNook.Core.NookStore(data,[desktop],new SandboxRecycle(profile));
        var doc=Path.Combine(desktop,"更新保留内容.url");File.WriteAllText(doc,"user edited data");var item=store.Collect(doc,"office");store.RenameCategory("office","用户分类");
        var targetDir=Path.Combine(root,"portable");Directory.CreateDirectory(targetDir);var target=Path.Combine(targetDir,"DesktopNook.exe");File.Copy(oldExe,target,false);
        var before=LocalUpdater.Hash(Path.Combine(data,"state.json"));var staged=LocalUpdater.Stage(candidate,target,data,profile,0);
        var helperStart=new ProcessStartInfo(staged.Helper){UseShellExecute=false,CreateNoWindow=true};helperStart.ArgumentList.Add("--apply-update");helperStart.ArgumentList.Add(staged.Plan);
        int result;using(var helper=Process.Start(helperStart)!){if(!helper.WaitForExit(30000))throw new IOException("更新助手未结束。");result=helper.ExitCode;}
        using var updateResult=JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(staged.Plan)!,"update-result.json")));
        var ok=result==0&&LocalUpdater.Inspect(target).Version==LocalUpdater.Inspect(candidate).Version&&LocalUpdater.Hash(Path.Combine(data,"state.json"))==before&&File.ReadAllText(store.ManagedPath(item))=="user edited data";
        var stop=new ProcessStartInfo(target){UseShellExecute=false,CreateNoWindow=true};stop.ArgumentList.Add("--maintenance=stop");stop.ArgumentList.Add("--sandbox");stop.ArgumentList.Add(profile);using(var process=Process.Start(stop)!){process.WaitForExit();}
        File.WriteAllText(Path.Combine(root,"update-verification.json"),JsonSerializer.Serialize(new{success=ok,helperExit=result,oldVersion=LocalUpdater.Inspect(oldExe).Version.ToString(),newVersion=LocalUpdater.Inspect(target).Version.ToString(),configurationHashUnchanged=LocalUpdater.Hash(Path.Combine(data,"state.json"))==before,payloadUnchanged=File.ReadAllText(store.ManagedPath(item))=="user edited data",newProcessConfirmed=updateResult.RootElement.GetProperty("success").GetBoolean()},new JsonSerializerOptions{WriteIndented=true}));
        return ok?0:1;
    }
}
