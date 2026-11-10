using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using DesktopNook.Core;

namespace DesktopNook.App;
internal static class LiveVerification
{
    public static int Run(string root)
    {
        Directory.CreateDirectory(root);var checks=new List<string>();var failure="";
        var desktop=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var data=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DesktopNook");
        var before=File.Exists(Path.Combine(data,"state.json"))?NookStore.HashFile(Path.Combine(data,"state.json")):"";
        var bin=new WindowsRecycleBin();var snapshot=bin.Capture();
        File.WriteAllText(Path.Combine(root,"recycle-original.json"),JsonSerializer.Serialize(snapshot));
        var source=Path.Combine(desktop,"番茄验收-"+Guid.NewGuid().ToString("N")+".lnk");
        NookStore? store=null;NookItem? entry=null;string? originalHash=null;
        try
        {
            store=new(Path.Combine(root,"Data"),[desktop],bin);
            foreach(var pending in store.State.Items.ToArray())store.Restore(pending.Id,true);
            var backup=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DesktopNook.LiveBackup-"+Guid.NewGuid().ToString("N"));
            foreach(var file in Directory.EnumerateFiles(data,"*",SearchOption.AllDirectories)){var copy=Path.Combine(backup,Path.GetRelativePath(data,file));Directory.CreateDirectory(Path.GetDirectoryName(copy)!);File.Copy(file,copy,false);}
            checks.Add("Existing user configuration and vault backed up on same filesystem");
            dynamic shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;dynamic link=shell.CreateShortcut(source);
            try{link.TargetPath=Environment.ProcessPath;link.Arguments="--verification-marker --sandbox \""+root+"\"";link.WorkingDirectory=desktop;link.Save();}finally{Marshal.FinalReleaseComObject(link);Marshal.FinalReleaseComObject(shell);}
            originalHash=NookStore.HashFile(source);store=new(Path.Combine(root,"Data"),[desktop],bin);entry=store.Collect(source,"office");
            if(File.Exists(source))throw new Exception("实际桌面入口未消失。");checks.Add("Dedicated real desktop shortcut collected; original disappeared");
            store.RenameCategory("office","验收分类");store.RenameItem(entry.Id,"验收重命名");
            store=new(store.DataDirectory,[desktop],bin);entry=store.State.Items.Single();
            if(entry.Name!="验收重命名"||store.State.Categories.First().Name!="验收分类")throw new Exception("重命名未持久保存。");checks.Add("Category and icon rename persisted in a new store session");
            ShellIntegration.Launch(store,entry);
            for(var i=0;i<50&&!File.Exists(Path.Combine(root,"shortcut-launched.txt"));i++)Thread.Sleep(100);
            if(!File.Exists(Path.Combine(root,"shortcut-launched.txt")))throw new Exception("实际桌面快捷方式启动未完成。");checks.Add("Collected real desktop shortcut launches with quoted parameters");
            store.Restore(entry.Id);if(!File.Exists(source)||NookStore.HashFile(source)!=originalHash)throw new Exception("桌面恢复不一致。");checks.Add("Real desktop shortcut restored byte-for-byte");
            bin.Restore(new(){["NewStartPanel"]=0,["ClassicStartMenu"]=0});Thread.Sleep(500);
            if(!bin.IsVisible)throw new Exception("实际回收站显示设置未完成。");
            bin.Hide();Thread.Sleep(500);if(bin.IsVisible)throw new Exception("实际回收站隐藏设置未完成。");
            bin.Restore(snapshot);if(bin.Capture().Any(p=>p.Value!=snapshot[p.Key]))throw new Exception("实际回收站设置未还原。");checks.Add("Live Recycle Bin show/hide/restore with desktop refresh; exact original values restored");
        }
        catch(Exception ex){failure=ex.ToString();}
        finally
        {
            try{bin.Restore(snapshot);}catch(Exception ex){failure+="\n回收站恢复："+ex.Message;}
            try
            {
                if(store is not null){store=new(store.DataDirectory,[desktop],bin);foreach(var item in store.State.Items.ToArray())store.Restore(item.Id,true);}
                if(File.Exists(source)&&originalHash is not null&&NookStore.HashFile(source)==originalHash)File.Delete(source);
            }catch(Exception ex){failure+="\n测试入口恢复："+ex.Message;}
            var unchanged=before==(File.Exists(Path.Combine(data,"state.json"))?NookStore.HashFile(Path.Combine(data,"state.json")):"");
            if(!unchanged)failure+="\n日常配置发生变化，需要核对备份。";
            File.WriteAllText(Path.Combine(root,"live-verification.json"),JsonSerializer.Serialize(new{passed=checks.Count,failure,checks,userStateUnchanged=unchanged,recycleSettingsRestored=bin.Capture().All(p=>p.Value==snapshot[p.Key]),testShortcutRemoved=!File.Exists(source)},new JsonSerializerOptions{WriteIndented=true}));
        }
        return failure.Length==0?0:1;
    }
}
