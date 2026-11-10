using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DesktopNook.Core;

namespace DesktopNook.App;

// 配置包：把收纳数据、托管副本、窗口与行为偏好打成一个 zip，便于重装系统或换机迁移。
// 导入前先在当前数据目录内留一份完整备份，绝不静默覆盖用户现有收纳内容。
internal static class DataPortability
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    public static string Export(NookStore store, StartupOptions options, string target)
    {
        var full = Path.GetFullPath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (File.Exists(full)) File.Delete(full);
        using var zip = ZipFile.Open(full, ZipArchiveMode.Create);
        var statePath = Path.Combine(store.DataDirectory, "state.json");
        if (!File.Exists(statePath)) throw new IOException("当前配置不存在，无法导出。");
        zip.CreateEntryFromFile(statePath, "state.json");

        var vault = Path.Combine(store.DataDirectory, "vault");
        var files = 0;
        foreach (var file in Directory.EnumerateFiles(vault, "*", SearchOption.AllDirectories))
        {
            zip.CreateEntryFromFile(file, "vault/" + Relative(vault, file));
            files++;
        }
        // 跨卷收纳的文件不在主 vault 内，按入口 Id 单独打包，导入时还原到原托管位置。
        foreach (var item in store.State.Items.Where(i => !string.IsNullOrWhiteSpace(i.PayloadPath)))
        {
            var path = store.ManagedPath(item);
            if (File.Exists(path)) { zip.CreateEntryFromFile(path, "external/" + item.Id + "/" + Path.GetFileName(path)); files++; }
            else if (Directory.Exists(path))
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                { zip.CreateEntryFromFile(file, "external/" + item.Id + "/" + Relative(path, file)); files++; }
        }
        var position = options.ReadWindowPosition();
        var settings = new
        {
            app = options.ReadSettings(),
            size = options.ReadWindowSize(),
            position = new { manual = position.Manual, left = position.Left, top = position.Top },
            closeEndsProcess = options.CloseEndsProcess,
            desktops = store.DesktopRoots
        };
        var entry = zip.CreateEntry("settings.json");
        using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false))) writer.Write(JsonSerializer.Serialize(settings, Json));
        return "已导出 " + store.State.Items.Count + " 个入口、" + store.State.Categories.Count + " 个分类、" + files + " 个托管文件";
    }

    public static string Import(NookStore store, StartupOptions options, string source)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("配置包不存在。", source);
        var temp = Path.Combine(Path.GetTempPath(), "DesktopNook-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            using (var zip = ZipFile.OpenRead(source))
            {
                foreach (var entry in zip.Entries)
                    if (entry.FullName.Contains("..") || entry.FullName.StartsWith('/') || Path.IsPathRooted(entry.FullName))
                        throw new IOException("配置包包含非法路径，已取消导入。");
                zip.ExtractToDirectory(temp, true);
            }
            var statePath = Path.Combine(temp, "state.json");
            if (!File.Exists(statePath)) throw new IOException("配置包缺少 state.json。");
            var state = JsonSerializer.Deserialize<NookState>(File.ReadAllText(statePath)) ?? throw new IOException("配置内容为空。");
            if (state.Categories.Count == 0 || state.Categories[^1].Id != "uncategorized"
                || state.Items.Select(i => i.Id).Distinct().Count() != state.Items.Count)
                throw new IOException("配置包结构无效，已取消导入。");
            foreach (var item in state.Items)
                if (string.IsNullOrWhiteSpace(item.PayloadPath) && !Directory.Exists(Path.Combine(temp, "vault", item.Id)))
                    throw new IOException("配置包缺少入口文件：" + item.Name);

            // 换机后桌面目录不同：把原桌面路径重映射到本机，否则 Core 的路径校验会拒绝整份配置。
            // 跨卷托管项目一并归并进主 vault，避免绝对路径随机器变化而失效。
            var desktopRoot = store.DesktopRoots[0];
            var merged = 0;
            foreach (var item in state.Items)
            {
                if (item.Kind is "shortcut" or "file" or "folder")
                    item.OriginalPath = Path.Combine(desktopRoot, item.FileName);
                if (string.IsNullOrWhiteSpace(item.PayloadPath)) continue;
                var external = Path.Combine(temp, "external", item.Id);
                var target = Path.Combine(temp, "vault", item.Id);
                Directory.CreateDirectory(target);
                if (Directory.Exists(external)) CopyDirectory(external, target);
                item.PayloadPath = "";
                merged++;
            }
            File.WriteAllText(statePath, JsonSerializer.Serialize(state, Json));

            // 备份放在数据目录内，避免跨盘复制，也不会被 NookStore 当成入口目录扫描。
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backup = Path.Combine(store.DataDirectory, "backups", stamp);
            Directory.CreateDirectory(backup);
            File.Copy(Path.Combine(store.DataDirectory, "state.json"), Path.Combine(backup, "state.json"), true);
            CopyDirectory(Path.Combine(store.DataDirectory, "vault"), Path.Combine(backup, "vault"));

            var vault = Path.Combine(store.DataDirectory, "vault");
            Directory.Move(vault, Path.Combine(store.DataDirectory, "vault.old-" + stamp));
            Directory.CreateDirectory(vault);
            CopyDirectory(Path.Combine(temp, "vault"), vault);
            File.Copy(statePath, Path.Combine(store.DataDirectory, "state.json"), true);

            var settingsPath = Path.Combine(temp, "settings.json");
            if (File.Exists(settingsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
                var root = document.RootElement;
                if (root.TryGetProperty("app", out var app)) options.WriteSettings(JsonSerializer.Deserialize<AppSettings>(app.GetRawText()) ?? new());
                if (root.TryGetProperty("size", out var size)) options.WriteWindowSize(JsonSerializer.Deserialize<WindowSizePreference>(size.GetRawText()) ?? new());
                if (root.TryGetProperty("position", out var position))
                    options.WriteWindowPosition(position.GetProperty("manual").GetBoolean(), position.GetProperty("left").GetDouble(), position.GetProperty("top").GetDouble());
                if (root.TryGetProperty("closeEndsProcess", out var close)) options.CloseEndsProcess = close.GetBoolean();
            }
            return "已导入 " + state.Items.Count + " 个入口（归并跨卷文件 " + merged + " 项）。原数据备份于 backups/" + stamp + "，请重新打开面板核对。";
        }
        finally { try { Directory.Delete(temp, true); } catch { /* 临时目录清理失败不影响导入结果 */ } }
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }
}
