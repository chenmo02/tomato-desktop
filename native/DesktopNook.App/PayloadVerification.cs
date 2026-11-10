using System.IO;
using System.Text.Json;
using DesktopNook.Core;

namespace DesktopNook.App;
internal static class PayloadVerification
{
    public static int Run(string reportRoot)
    {
        Directory.CreateDirectory(reportRoot);var desktop=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);var prefix="番茄文件验收-"+Guid.NewGuid().ToString("N");var file=Path.Combine(desktop,prefix+".txt");var folder=Path.Combine(desktop,prefix);var checks=new List<string>();var failure="";NookStore? store=null;
        try
        {
            File.WriteAllText(file,"desktop document");Directory.CreateDirectory(Path.Combine(folder,"空目录"));File.WriteAllText(Path.Combine(folder,"资料.txt"),"folder original");
            store=new(Path.Combine(reportRoot,"Data"),[desktop],new SimulatedRecycleBin());var doc=store.Collect(file,"office");var directory=store.Collect(folder,"office");
            if(File.Exists(file)||Directory.Exists(folder))throw new Exception("源桌面项目仍可见。");checks.Add("Real desktop file and complete folder moved into collection");
            File.WriteAllText(store.ManagedPath(doc),"edited document");File.WriteAllText(Path.Combine(store.ManagedPath(directory),"新增.txt"),"new folder content");
            store=new(store.DataDirectory,[desktop],new SimulatedRecycleBin());store.Restore(doc.Id);store.Restore(directory.Id);
            if(File.ReadAllText(file)!="edited document"||File.ReadAllText(Path.Combine(folder,"资料.txt"))!="folder original"||File.ReadAllText(Path.Combine(folder,"新增.txt"))!="new folder content"||!Directory.Exists(Path.Combine(folder,"空目录")))throw new Exception("内容或结构恢复不完整。");
            checks.Add("Edits, added file and empty subdirectory retained on restore");
            var icon=SharpIcons.Load(Environment.ProcessPath!);if(icon is not System.Windows.Media.Imaging.BitmapSource image||image.PixelWidth<48)throw new Exception("高分辨率图标未取得。");checks.Add("Native icon extraction returns a real high-resolution bitmap");
        }
        catch(Exception ex){failure=ex.ToString();}
        finally
        {
            try
            {
                if(store is not null){store=new(store.DataDirectory,[desktop],new SimulatedRecycleBin());foreach(var item in store.State.Items.ToArray())store.Restore(item.Id,true);}
                // Only the exact fixtures created above; no recursive deletion is used.
                if(File.Exists(file)&&File.ReadAllText(file) is "desktop document" or "edited document")File.Delete(file);
                foreach(var leaf in new[]{"资料.txt","新增.txt"}){var path=Path.Combine(folder,leaf);if(File.Exists(path)&&File.ReadAllText(path) is "folder original" or "new folder content")File.Delete(path);}
                var empty=Path.Combine(folder,"空目录");if(Directory.Exists(empty)&&!Directory.EnumerateFileSystemEntries(empty).Any())Directory.Delete(empty);
                if(Directory.Exists(folder)&&!Directory.EnumerateFileSystemEntries(folder).Any())Directory.Delete(folder);
            }
            catch(Exception ex){failure+="\n恢复／清理："+ex.Message;}
            File.WriteAllText(Path.Combine(reportRoot,"payload-verification.json"),JsonSerializer.Serialize(new{passed=checks.Count,failure,checks,testFileRemoved=!File.Exists(file),testFolderRemoved=!Directory.Exists(folder)},new JsonSerializerOptions{WriteIndented=true}));
        }
        return failure.Length==0?0:1;
    }
}
