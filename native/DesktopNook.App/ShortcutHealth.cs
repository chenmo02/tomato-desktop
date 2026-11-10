using System.IO;
using System.Runtime.InteropServices;
using DesktopNook.Core;

namespace DesktopNook.App;

// 失效入口检测：托管副本丢失，或 .lnk 指向的目标已不存在（软件卸载、路径迁移）。
// 只在 UI 线程（STA）调用，WScript.Shell 解析快捷方式需要 STA 单元。
internal static class ShortcutHealth
{
    public static HashSet<string> Scan(NookStore store)
    {
        var broken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in store.State.Items)
        {
            if (item.Kind == "recycle-bin") continue;
            string path;
            try { path = store.ManagedPath(item); }
            catch { broken.Add(item.Id); continue; }
            if (!File.Exists(path) && !Directory.Exists(path)) { broken.Add(item.Id); continue; }
            if (item.Kind is "file" or "folder") continue;
            if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) continue;
            var target = ResolveTarget(path);
            if (string.IsNullOrWhiteSpace(target) || target.Contains('%') || target.Contains("::")) continue;
            if (!File.Exists(target) && !Directory.Exists(target)) broken.Add(item.Id);
        }
        return broken;
    }

    public static bool IsBrokenShortcut(string path)
    {
        if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return false;
        var target = ResolveTarget(path);
        return !string.IsNullOrWhiteSpace(target) && !target.Contains('%') && !target.Contains("::")
               && !File.Exists(target) && !Directory.Exists(target);
    }

    private static string? ResolveTarget(string path)
    {
        dynamic? shell = null;
        dynamic? link = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            shell = Activator.CreateInstance(type);
            if (shell is null) return null;
            link = shell.CreateShortcut(path);
            return link.TargetPath as string;
        }
        catch { return null; }
        finally
        {
            if (link is not null) try { Marshal.FinalReleaseComObject(link); } catch { }
            if (shell is not null) try { Marshal.FinalReleaseComObject(shell); } catch { }
        }
    }
}
