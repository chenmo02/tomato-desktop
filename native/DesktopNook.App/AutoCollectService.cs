using System.IO;
using System.Windows.Threading;
using DesktopNook.Core;

namespace DesktopNook.App;

// 自动收纳：默认关闭。开启后每 15 秒轮询桌面目录，把新增且未被收纳的可见项目
// 收进「未分类」。跳过隐藏项、程序自身、系统文件与正在写入的文件。
//
// 后台占用优化：面板收起时把间隔放宽到 60 秒、恢复显示时立即补扫并回到 15 秒；
// 每轮扫描复用已收纳路径的哈希索引，避免对每个桌面条目线性扫一遍状态表；
// 「是否指向程序自身」的 COM 判定按路径 + 写入时间缓存，不再每 15 秒重建
// WScript.Shell 解析所有快捷方式。
internal sealed class AutoCollectService
{
    private static readonly TimeSpan VisibleInterval=TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BackgroundInterval=TimeSpan.FromSeconds(60);
    private const int OwnShortcutCacheLimit=256;
    private readonly Func<NookStore> _store;
    private readonly Action<int> _onCollected;
    private readonly DispatcherTimer _timer;
    private readonly object _cacheGate=new();
    private readonly Dictionary<string,(long WriteTicks,bool Own)> _ownShortcutCache=new(StringComparer.OrdinalIgnoreCase);
    private bool _enabled;
    private bool _background;

    public AutoCollectService(Func<NookStore> store, Action<int> onCollected)
    {
        _store = store;
        _onCollected = onCollected;
        _timer = new DispatcherTimer { Interval = VisibleInterval };
        _timer.Tick += (_, _) => { if (_enabled) ScanNow(); };
    }

    public bool Enabled => _enabled;
    // 供资源自检核对降频，不改变运行时行为。
    internal TimeSpan ScanInterval=>_timer.Interval;

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (enabled) _timer.Start(); else _timer.Stop();
    }

    // 面板收起 / 恢复时由 MainWindow 调用。恢复后排队补扫一次，
    // 保证后台 60 秒间隔期间新增的桌面项目不会因为降频而漏收。
    public void SetBackgroundMode(bool background)
    {
        if(_background==background)return;
        _background=background;
        _timer.Interval=background?BackgroundInterval:VisibleInterval;
        if(!background&&_enabled)
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,new Action(()=>{if(_enabled&&!_background)ScanNow();}));
    }

    public int ScanNow()
    {
        var store = _store();
        var added = 0;
        // 已收纳路径建一次哈希索引：原实现每个桌面条目都线性扫一次 State.Items，
        // 收纳上百项时每轮轮询变成 O(条目数 × 状态数)，是后台最明显的 CPU 浪费。
        var managed=new HashSet<string>(store.State.Items.Select(i=>i.OriginalPath),StringComparer.OrdinalIgnoreCase);
        // 公共桌面是所有用户共用的系统级目录，普通权限无法创建/移除文件；
        // 自动收纳只处理可写的当前用户桌面，避免每轮轮询都失败或留下半完成迁移。
        var shared = App.IsSandbox ? "" : Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        foreach (var root in store.DesktopRoots.Where(Directory.Exists))
        {
            if (!string.IsNullOrEmpty(shared) && string.Equals(Path.GetFullPath(root), Path.GetFullPath(shared), StringComparison.OrdinalIgnoreCase)) continue;
            if (!NookStore.IsWritableDesktop(root)) continue;
            string[] entries;
            try { entries = Directory.EnumerateFileSystemEntries(root).ToArray(); }
            catch { continue; }
            foreach (var path in entries)
            {
                try
                {
                    var name = Path.GetFileName(path);
                    if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    if ((File.GetAttributes(path) & FileAttributes.Hidden) != 0) continue;
                    if (name.Contains("番茄桌面", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Path.GetExtension(path).Equals(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    if (managed.Contains(path)) continue;
                    if (IsOwnShortcut(path)) continue;
                    // 刚落盘的文件可能仍在写入，等两秒后的下一轮再收纳。
                    if (File.Exists(path) && DateTime.Now - File.GetLastWriteTime(path) < TimeSpan.FromSeconds(2)) continue;
                    store.Collect(path, "uncategorized");
                    managed.Add(path);
                    added++;
                }
                catch { /* 单个项目失败不影响其余项目，也不阻塞面板 */ }
            }
        }
        if (added > 0) { ShellIntegration.RefreshDesktop(); _onCollected(added); }
        return added;
    }

    // 只有快捷方式的目标被改写（LastWriteTimeUtc 变化）时才重做 COM 解析。
    private bool IsOwnShortcut(string path)
    {
        if(!Path.GetExtension(path).Equals(".lnk",StringComparison.OrdinalIgnoreCase))return false;
        long ticks;
        try{ticks=File.GetLastWriteTimeUtc(path).Ticks;}catch{return false;}
        lock(_cacheGate)
            if(_ownShortcutCache.TryGetValue(path,out var hit)&&hit.WriteTicks==ticks)return hit.Own;
        var own=ResolveOwnShortcut(path);
        lock(_cacheGate)
        {
            if(_ownShortcutCache.Count>=OwnShortcutCacheLimit)_ownShortcutCache.Clear();
            _ownShortcutCache[path]=(ticks,own);
        }
        return own;
    }

    private static bool ResolveOwnShortcut(string path)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return false;
            dynamic? shell = null; dynamic? link = null;
            try
            {
                shell = Activator.CreateInstance(type);
                if (shell is null) return false;
                link = shell.CreateShortcut(path);
                var target = link.TargetPath as string;
                return !string.IsNullOrEmpty(target) && string.Equals(Path.GetFullPath(target), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (link is not null) try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); } catch { }
                if (shell is not null) try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); } catch { }
            }
        }
        catch { return false; }
    }
}
