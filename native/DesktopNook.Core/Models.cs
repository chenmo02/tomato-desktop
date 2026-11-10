namespace DesktopNook.Core;

public sealed class Category
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
}

public sealed class NookItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "shortcut";
    public string CategoryId { get; set; } = "uncategorized";
    public string OriginalPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Hash { get; set; } = "";
    public string PayloadPath { get; set; } = "";
    public string PayloadLayout { get; set; } = "";
    public Dictionary<string, int?>? RecycleSnapshot { get; set; }
}

public sealed class NookState
{
    public int Version { get; set; } = 1;
    public List<Category> Categories { get; set; } = [new() { Id = "office", Name = "办公效率" }, new() { Id = "design", Name = "创意设计" }, new() { Id = "dev", Name = "开发工具" }, new() { Id = "uncategorized", Name = "未分类" }];
    public List<NookItem> Items { get; set; } = [];
}

public sealed class Migration
{
    public string Operation { get; set; } = "";
    public NookItem Item { get; set; } = new();
    public string Destination { get; set; } = "";
}

public sealed class Manifest
{
    public NookItem Item { get; set; } = new();
    public bool Restored { get; set; }
}

public interface IRecycleBin
{
    bool IsVisible { get; }
    Dictionary<string, int?> Capture();
    void Hide();
    void Restore(Dictionary<string, int?> snapshot);
}

public sealed class SimulatedRecycleBin : IRecycleBin
{
    public bool IsVisible { get; private set; } = true;
    public Dictionary<string, int?> Capture() => new() { ["NewStartPanel"] = IsVisible ? 0 : 1, ["ClassicStartMenu"] = null };
    public void Hide() => IsVisible = false;
    public void Restore(Dictionary<string, int?> snapshot) => IsVisible = snapshot.GetValueOrDefault("NewStartPanel") != 1;
}

public sealed class NameConflictException(string path) : IOException("桌面已有同名文件，可改名恢复：" + System.IO.Path.GetFileName(path))
{
    public string ConflictingPath { get; } = path;
}
