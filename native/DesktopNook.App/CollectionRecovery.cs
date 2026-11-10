using System.IO;
using System.Text.Json;
using DesktopNook.Core;

namespace DesktopNook.App;
internal static class CollectionRecovery
{
    public static int Run(string reportDir,bool apply=false)
    {
        Directory.CreateDirectory(reportDir);var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DesktopNook");
        var state=JsonSerializer.Deserialize<NookState>(File.ReadAllText(Path.Combine(root,"state.json")))!;
        if(apply)
        {
            var backup=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DesktopNook.BackupBeforeCatalogRecovery-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(backup);
            foreach(var file in Directory.EnumerateFiles(root,"*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint})){var target=Path.Combine(backup,Path.GetRelativePath(root,file));Directory.CreateDirectory(Path.GetDirectoryName(target)!);NookStore.CopyPreservingStreams(file,target);}
            var map=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(Path.Combine(reportDir,"category-map.json")))!;
            var recycle=new WindowsRecycleBin();var recycleBefore=recycle.Capture();File.WriteAllText(Path.Combine(reportDir,"recycle-before.json"),JsonSerializer.Serialize(recycleBefore));
            var store=new NookStore(root,[Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)],recycle);
            var recovered=new List<NookItem>();var changed=new List<string>();
            foreach(var dir in Directory.EnumerateDirectories(Path.Combine(root,"vault")).OrderBy(d=>File.GetLastWriteTimeUtc(Path.Combine(d,"entry.json"))))
            {
                if(!Guid.TryParseExact(Path.GetFileName(dir),"N",out _)||!File.Exists(Path.Combine(dir,"entry.json")))continue;
                var manifest=JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(dir,"entry.json")))!;if(manifest.Restored)continue;var item=manifest.Item;
                if(!store.State.Categories.Any(c=>c.Id==item.CategoryId)){if(!map.TryGetValue(item.CategoryId,out var name))throw new IOException("缺少分类名称："+item.CategoryId);item.CategoryId=store.State.Categories.Single(c=>c.Name==name).Id;}
                if(item.Kind=="shortcut"){var hash=NookStore.HashFile(store.ManagedPath(item));if(hash!=item.Hash){changed.Add(item.Name);item.Hash=hash;}if(File.Exists(item.OriginalPath))throw new IOException("桌面存在同名入口，需要核对："+item.Name);}
                if(item.Kind=="recycle-bin"&&recycle.IsVisible)item.RecycleSnapshot=recycleBefore;
                recovered.Add(item);
            }
            var added=store.RestoreCatalog(recovered);
            if(store.State.Items.Any(i=>i.Kind=="recycle-bin")&&recycle.IsVisible)recycle.Hide();
            var check=new NookStore(root,store.DesktopRoots,recycle);
            File.WriteAllText(Path.Combine(reportDir,"recovery-result.json"),JsonSerializer.Serialize(new{success=check.State.Items.Count>=recovered.Count,recovered=added,total=check.State.Items.Count,backup,updatedShortcutHashes=changed,groups=check.State.Categories.Select(c=>new{name=c.Name,count=check.State.Items.Count(i=>i.CategoryId==c.Id)})},new JsonSerializerOptions{WriteIndented=true}));
            state=check.State;
        }
        File.WriteAllText(Path.Combine(reportDir,"native-state.json"),JsonSerializer.Serialize(state,new JsonSerializerOptions{WriteIndented=true}));
        var entries=new List<object>();foreach(var dir in Directory.EnumerateDirectories(Path.Combine(root,"vault"))){var manifest=Path.Combine(dir,"entry.json");if(!File.Exists(manifest))continue;var m=JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifest))!;entries.Add(new{id=m.Item.Id,name=m.Item.Name,kind=m.Item.Kind,category=m.Item.CategoryId,restored=m.Restored});}
        File.WriteAllText(Path.Combine(reportDir,"inventory.json"),JsonSerializer.Serialize(new{nativeItems=state.Items.Count,categories=state.Categories,entries},new JsonSerializerOptions{WriteIndented=true}));return 0;
    }
}
