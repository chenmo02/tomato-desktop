using System.IO;
using System.Windows.Threading;
using DesktopNook.Core;

namespace DesktopNook.App;

// 自动收纳：默认关闭。开启后每 15 秒轮询桌面目录，把新增且未被收纳的可见项目
// 收进「未分类」。跳过隐藏项、程序自身、系统文件与正在写入的文件。
internal sealed class AutoCollectService
{
    private readonly Func<NookStore> _store;
    private readonly Action<int> _onCollected;
    private readonly DispatcherTimer _timer;
    private bool _enabled;

    public AutoCollectService(Func<NookStore> store, Action<int> onCollected)
    {
        _store = store;
        _onCollected = onCollected;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) => { if (_enabled) ScanNow(); };
    }

    public bool Enabled => _enabled;

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (enabled) _timer.Start(); else _timer.Stop();
    }

    public int ScanNow()
    {
        var store = _store();
        var added = 0;
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
                    if (store.State.Items.Any(i => string.Equals(i.OriginalPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                    if (IsOwnShortcut(path)) continue;
                    // 刚落盘的文件可能仍在写入，等两秒后的下一轮再收纳。
                    if (File.Exists(path) && DateTime.Now - File.GetLastWriteTime(path) < TimeSpan.FromSeconds(2)) continue;
                    store.Collect(path, "uncategorized");
                    added++;
                }
                catch { /* 单个项目失败不影响其余项目，也不阻塞面板 */ }
            }
        }
        if (added > 0) { ShellIntegration.RefreshDesktop(); _onCollected(added); }
        return added;
    }

    private static bool IsOwnShortcut(string path)
    {
        if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return false;
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
