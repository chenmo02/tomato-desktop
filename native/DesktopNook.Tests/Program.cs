using DesktopNook.Core;
using System.Text.Json;

var results = new List<object>();
var runRoot = Path.Combine(Path.GetTempPath(), "DesktopNook-Tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(runRoot);
void Assert(bool value, string message) { if (!value) throw new Exception(message); }
void Test(string name, Action<string> run)
{
    var directory = Path.Combine(runRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
    try { run(directory); results.Add(new { name, passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception e) { results.Add(new { name, passed = false, error = e.ToString() }); Console.WriteLine("FAIL " + name + ": " + e.Message); }
}
(string desktop, string data, SimulatedRecycleBin recycle) Setup(string root)
{
    var desktop = Path.Combine(root, "Desktop 中文"); Directory.CreateDirectory(desktop);
    return (desktop, Path.Combine(root, "Data"), new());
}
string FileAt(string desktop, string name = "应用 空格.lnk", string contents = "shortcut-with-arguments") { var file = Path.Combine(desktop, name); File.WriteAllText(file, contents); return file; }
Test("Roundtrip preserves shortcut bytes and persistent category", root => {
    var (desktop,data,bin) = Setup(root); var source = FileAt(desktop); var bytes = File.ReadAllBytes(source);
    var engine = new NookStore(data, [desktop], bin); var entry = engine.Collect(source, "office");
    Assert(!File.Exists(source) && File.Exists(engine.ManagedPath(entry)), "collect must remove desktop entry only after vault exists");
    engine = new(data, [desktop], bin); Assert(engine.State.Items.Single().CategoryId == "office", "persistence");
    var restored = engine.Restore(entry.Id); Assert(bytes.SequenceEqual(File.ReadAllBytes(restored)) && engine.State.Items.Count == 0, "exact roundtrip");
    Assert(File.Exists(engine.ManagedPath(entry)), "recovery backup retained");
});
Test("Conflict never overwrites, renamed restore preserves both files", root => {
    var (desktop,data,bin)=Setup(root); var source=FileAt(desktop); var engine=new NookStore(data,[desktop],bin); var item=engine.Collect(source,"office"); File.WriteAllText(source,"new-user-file");
    try { engine.Restore(item.Id); throw new Exception("expected conflict"); } catch(NameConflictException) {}
    Assert(File.ReadAllText(source)=="new-user-file" && engine.State.Items.Count==1,"conflict preserved");
    var restored=engine.Restore(item.Id,true); Assert(restored!=source && File.ReadAllText(restored)=="shortcut-with-arguments", "renamed restore");
});
Test("Desktop roots and file types restricted", root=>{
    var(desktop,data,bin)=Setup(root);var engine=new NookStore(data,[desktop],bin);
    foreach(var path in new[]{FileAt(root,"outside.lnk"),FileAt(root,"outside.txt")}) { try {engine.Collect(path,"office");throw new Exception("accepted outside source");}catch(IOException){} Assert(File.Exists(path),"outside file unchanged"); }
});
Test("Category reassignment, deletion and duplicate validation", root=>{
    var(desktop,data,bin)=Setup(root);var engine=new NookStore(data,[desktop],bin);engine.AddCategory("通讯");var cat=engine.State.Categories.Single(c=>c.Name=="通讯");
    var item=engine.Collect(FileAt(desktop),"office");engine.MoveItem(item.Id,cat.Id);engine.RenameItem(item.Id,"自定义名称");engine.DeleteCategory(cat.Id);
    Assert(engine.State.Items.Single().CategoryId=="uncategorized" && engine.State.Items.Single().Name=="自定义名称","delete preserves item");
    try{engine.AddCategory("办公效率");throw new Exception("duplicate accepted");}catch(ArgumentException){}
    engine.OrderCategory("dev",-1);Assert(engine.State.Categories[^1].Id=="uncategorized","system category remains last");
});
foreach(var stage in new[]{"journal","vault","collect-state","source-removed"}) Test("Interrupted collect recovery: "+stage, root=>{
    var(desktop,data,bin)=Setup(root);var source=FileAt(desktop);var engine=new NookStore(data,[desktop],bin,s=>{if(s==stage)throw new IOException("injected crash");});
    try{engine.Collect(source,"office");}catch(IOException){}
    engine=new(data,[desktop],bin);
    var exists=File.Exists(source);Assert(exists?engine.State.Items.Count==0:engine.State.Items.Count==1,"one active location after recovery");
    Assert(exists || File.Exists(engine.ManagedPath(engine.State.Items.Single())),"never lose unique copy");
});
foreach(var stage in new[]{"journal","destination-created","restore-state"}) Test("Interrupted restore recovery: "+stage,root=>{
    var(desktop,data,bin)=Setup(root);var source=FileAt(desktop);var engine=new NookStore(data,[desktop],bin);var item=engine.Collect(source,"office");
    engine=new(data,[desktop],bin,s=>{if(s==stage)throw new IOException("injected crash");});try{engine.Restore(item.Id);}catch(IOException){}
    engine=new(data,[desktop],bin);Assert(File.Exists(source)?engine.State.Items.Count==0:engine.State.Items.Count==1,"restored state matches desktop");Assert(File.Exists(engine.ManagedPath(item)),"backup survives all interruptions");
});
Test("Source changes during collection are not deleted",root=>{
    var(desktop,data,bin)=Setup(root);var source=FileAt(desktop);var engine=new NookStore(data,[desktop],bin,s=>{if(s=="collect-state")File.WriteAllText(source,"changed externally");});try{engine.Collect(source,"office");}catch(IOException){}
    engine=new(data,[desktop],bin);Assert(File.ReadAllText(source)=="changed externally"&&engine.State.Items.Count==0,"changed source preserved");
});
Test("Recycle Bin distinct state roundtrip, no file emulation",root=>{
    var(desktop,data,bin)=Setup(root);var engine=new NookStore(data,[desktop],bin);var entry=engine.CollectRecycleBin("dev");Assert(!bin.IsVisible&&entry.Kind=="recycle-bin","system hidden and tracked");
    engine=new(data,[desktop],bin);engine.Restore(entry.Id);Assert(bin.IsVisible&&engine.State.Items.Count==0,"system restored");Assert(!Directory.EnumerateFiles(desktop).Any(),"no fake shortcut created");
});
Test("Recycle Bin interrupted hide restores original state",root=>{
    var(desktop,data,bin)=Setup(root);var engine=new NookStore(data,[desktop],bin,s=>{if(s=="recycle-hidden")throw new IOException("crash");});try{engine.CollectRecycleBin("office");}catch(IOException){}
    engine=new(data,[desktop],bin);Assert(bin.IsVisible&&engine.State.Items.Count==0,"original registry snapshot restored");
});
Test("Corrupt config rebuilt from manifests, damaged originals retained",root=>{
    var(desktop,data,bin)=Setup(root);var engine=new NookStore(data,[desktop],bin);var entry=engine.Collect(FileAt(desktop),"office");
    File.WriteAllText(Path.Combine(data,"state.json"),"{broken");File.WriteAllText(Path.Combine(data,"state.json.bak"),"{broken");
    engine=new(data,[desktop],bin);Assert(engine.State.Items.Any(i=>i.Id==entry.Id),"manifest recovered entry");Assert(Directory.EnumerateFiles(data,"state.json.damaged-*").Any(),"damaged config retained");engine.Restore(entry.Id);
});
Test("Corrupt backup does not resurrect restored entry",root=>{
    var(desktop,data,bin)=Setup(root);var engine=new NookStore(data,[desktop],bin);var item=engine.Collect(FileAt(desktop),"office");engine.Restore(item.Id);
    File.WriteAllText(Path.Combine(data,"state.json"),"broken");engine=new(data,[desktop],bin);Assert(engine.State.Items.Count==0,"manifest tombstone overrides old backup");
});
Test("Different shortcuts to same application stay independent",root=>{
    var(desktop,data,bin)=Setup(root);var engine=new NookStore(data,[desktop],bin);engine.Collect(FileAt(desktop,"one.lnk"),"office");engine.Collect(FileAt(desktop,"two.lnk"),"dev");Assert(engine.State.Items.Count==2,"do not merge by target");
});
if(OperatingSystem.IsWindows())Test("Windows security alternate streams preserved on roundtrip",root=>{
    var(desktop,data,bin)=Setup(root);var source=FileAt(desktop);const string zone="[ZoneTransfer]\r\nZoneId=3\r\n";File.WriteAllText(source+":Zone.Identifier",zone);
    var engine=new NookStore(data,[desktop],bin);var item=engine.Collect(source,"office");Assert(File.ReadAllText(engine.ManagedPath(item)+":Zone.Identifier")==zone,"vault retains security stream");
    var restored=engine.Restore(item.Id);Assert(File.ReadAllText(restored+":Zone.Identifier")==zone,"desktop retains security stream");
});
Test("Repeated category and item renames in actual LocalAppData retain attributes and backup",root=>{
    var regressionRoot=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DesktopNook-SaveRegression-"+Guid.NewGuid().ToString("N"));
    var(desktop,data,bin)=Setup(regressionRoot);var engine=new NookStore(data,[desktop],bin);var statePath=Path.Combine(data,"state.json");
    var attributes=File.GetAttributes(statePath);
    engine.RenameCategory("office","办公重命名");engine.AddCategory("临时分类");engine.RenameCategory("office","第二次办公重命名");
    var item=engine.Collect(FileAt(desktop),"office");engine.RenameItem(item.Id,"入口重命名");
    engine=new(data,[desktop],bin);Assert(engine.State.Categories.Single(c=>c.Id=="office").Name=="第二次办公重命名","category rename persisted after reopen");
    Assert(engine.State.Items.Single().Name=="入口重命名","item rename persisted after reopen");
    Assert((File.GetAttributes(statePath)&FileAttributes.Encrypted)==(attributes&FileAttributes.Encrypted),"encryption attribute retained");
    Assert(File.Exists(statePath+".bak"),"previous state backup retained");
    engine.Restore(item.Id);Console.WriteLine("LocalAppData regression: "+regressionRoot+"; attributes="+attributes);
});
Test("Removing a menu entry retains recovery copy and system item is protected",root=>{
    var(desktop,data,bin)=Setup(root);var store=new NookStore(data,[desktop],bin);var item=store.Collect(FileAt(desktop),"office");store.ArchiveItem(item.Id);
    Assert(store.State.Items.Count==0&&File.Exists(store.ManagedPath(item)),"archive retains complete recovery file");
    store=new(data,[desktop],bin);Assert(store.State.Items.Count==0,"removed entry does not reappear after restart");
    var system=store.CollectRecycleBin("office");try{store.ArchiveItem(system.Id);throw new Exception("system archive should be rejected");}catch(IOException){}
    Assert(store.State.Items.Count==1&&!bin.IsVisible,"system item still recoverable");store.Restore(system.Id);
});
Test("Regular documents remain editable and restore latest content",root=>{
    var(desktop,data,bin)=Setup(root);var source=FileAt(desktop,"报告 中文.txt","original");var store=new NookStore(data,[desktop],bin);var item=store.Collect(source,"office");Assert(item.Kind=="file"&&!File.Exists(source),"document moved");File.WriteAllText(store.ManagedPath(item),"edited in collection");store=new(data,[desktop],bin);var restored=store.Restore(item.Id);Assert(File.ReadAllText(restored)=="edited in collection","latest changes restored");
});
Test("Nested folders including empty directories roundtrip without recursive deletion",root=>{
    var(desktop,data,bin)=Setup(root);var folder=Path.Combine(desktop,"项目.v1");Directory.CreateDirectory(Path.Combine(folder,"空目录"));Directory.CreateDirectory(Path.Combine(folder,"nested"));FileAt(Path.Combine(folder,"nested"),"资料.bin","important bytes");var store=new NookStore(data,[desktop],bin);var item=store.Collect(folder,"office");Assert(item.Kind=="folder"&&!Directory.Exists(folder),"folder leaves desktop");File.WriteAllText(Path.Combine(store.ManagedPath(item),"新增.txt"),"new content");store.Restore(item.Id);Assert(Directory.Exists(Path.Combine(folder,"空目录"))&&File.ReadAllText(Path.Combine(folder,"nested","资料.bin"))=="important bytes"&&File.Exists(Path.Combine(folder,"新增.txt")),"complete edited folder restored");
});
Test("Folder same-name conflicts never overwrite either tree",root=>{
    var(desktop,data,bin)=Setup(root);var folder=Path.Combine(desktop,"项目.v1");Directory.CreateDirectory(folder);FileAt(folder,"original.txt","stored");var store=new NookStore(data,[desktop],bin);var item=store.Collect(folder,"office");Directory.CreateDirectory(folder);FileAt(folder,"new.txt","desktop");try{store.Restore(item.Id);throw new Exception("expected conflict");}catch(NameConflictException){}var restored=store.Restore(item.Id,true);Assert(Path.GetFileName(restored)=="项目.v1 (2)"&&File.Exists(Path.Combine(folder,"new.txt"))&&File.Exists(Path.Combine(restored,"original.txt")),"both trees preserved");
});
foreach(var stage in new[]{"journal","mutable-moved","collect-state"})Test("Folder collect interruption recovery: "+stage,root=>{
    var(desktop,data,bin)=Setup(root);var folder=Path.Combine(desktop,"目录");Directory.CreateDirectory(folder);FileAt(folder,"data.txt","bytes");var store=new NookStore(data,[desktop],bin,s=>{if(s==stage)throw new IOException("injected interruption");});try{store.Collect(folder,"office");}catch(IOException){}store=new(data,[desktop],bin);Assert(Directory.Exists(folder)?store.State.Items.Count==0:store.State.Items.Count==1,"one managed state");if(store.State.Items.Count>0)store.Restore(store.State.Items.Single().Id);Assert(File.ReadAllText(Path.Combine(folder,"data.txt"))=="bytes","contents recovered");
});
foreach(var stage in new[]{"journal","destination-created","restore-state"})Test("Folder restore interruption recovery: "+stage,root=>{
    var(desktop,data,bin)=Setup(root);var folder=Path.Combine(desktop,"目录");Directory.CreateDirectory(folder);FileAt(folder,"data.txt","bytes");var store=new NookStore(data,[desktop],bin);var item=store.Collect(folder,"office");store=new(data,[desktop],bin,s=>{if(s==stage)throw new IOException("injected interruption");});try{store.Restore(item.Id);}catch(IOException){}store=new(data,[desktop],bin);if(store.State.Items.Count>0)store.Restore(item.Id);Assert(File.ReadAllText(Path.Combine(folder,"data.txt"))=="bytes"&&store.State.Items.Count==0,"restore completed safely");
});
Test("Mutable restore destination created concurrently is retained",root=>{
    var(desktop,data,bin)=Setup(root);var source=FileAt(desktop,"file.txt","stored");var store=new NookStore(data,[desktop],bin);var item=store.Collect(source,"office");store=new(data,[desktop],bin,s=>{if(s=="journal")File.WriteAllText(source,"concurrent desktop content");});try{store.Restore(item.Id);}catch(IOException){}store=new(data,[desktop],bin);Assert(store.State.Items.Count==1&&File.ReadAllText(source)=="concurrent desktop content"&&File.ReadAllText(store.ManagedPath(item))=="stored","both files retained");
});
Test("Cross-volume folder payload remains on source volume and restores",root=>{
    var desktop=Path.Combine(Environment.CurrentDirectory,"test-results","cross-volume-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(desktop);var folder=Path.Combine(desktop,"目录");Directory.CreateDirectory(folder);FileAt(folder,"数据.txt","cross-volume");var data=Path.Combine(root,"Data");var store=new NookStore(data,[desktop],new SimulatedRecycleBin());var item=store.Collect(folder,"office");store=new(data,[desktop],new SimulatedRecycleBin());store.Restore(item.Id);Assert(File.ReadAllText(Path.Combine(folder,"数据.txt"))=="cross-volume","source volume restored");
});
Test("User entry.json files never collide with or become recovery manifests",root=>{
    var(desktop,data,bin)=Setup(root);var source=FileAt(desktop,"entry.json","ordinary user content");var folder=Path.Combine(desktop,"project");Directory.CreateDirectory(folder);FileAt(folder,"entry.json","not a recovery manifest");var store=new NookStore(data,[desktop],bin);store.Collect(source,"office");store.Collect(folder,"office");File.WriteAllText(Path.Combine(data,"state.json"),"broken");File.WriteAllText(Path.Combine(data,"state.json.bak"),"broken");store=new(data,[desktop],bin);Assert(store.State.Items.Count==2,"only actual manifests scanned");foreach(var item in store.State.Items.ToArray())store.Restore(item.Id);Assert(File.ReadAllText(source)=="ordinary user content"&&File.ReadAllText(Path.Combine(folder,"entry.json"))=="not a recovery manifest","user JSON preserved");
});
Test("Lost collection index can be merged from intact records without moving payloads",root=>{
    var(desktop,data,bin)=Setup(root);var store=new NookStore(data,[desktop],bin);var first=store.Collect(FileAt(desktop,"one.lnk"),"office");var second=store.Collect(FileAt(desktop,"two.lnk"),"dev");store.State.Items.Clear();File.WriteAllText(Path.Combine(data,"state.json"),JsonSerializer.Serialize(store.State));store=new(data,[desktop],bin);Assert(store.RestoreCatalog([first,second])==2,"both records recovered");Assert(store.RestoreCatalog([first,second])==0,"recovery is idempotent");store=new(data,[desktop],bin);Assert(store.State.Items.Count==2&&store.State.Items[0].CategoryId=="office"&&File.Exists(store.ManagedPath(first)),"classification and files retained");
});
var failed = results.Count(r => JsonSerializer.Serialize(r).Contains("\"passed\":false"));
var output = args.FirstOrDefault() ?? Path.Combine(runRoot,"results.json");Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output,JsonSerializer.Serialize(new{passed=results.Count-failed,failed,runRoot,checks=results},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{results.Count-failed} passed, {failed} failed. {output}");Environment.ExitCode=failed==0?0:1;
