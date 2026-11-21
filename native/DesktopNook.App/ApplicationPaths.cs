using System.IO;
using Microsoft.Win32;

namespace DesktopNook.App;

internal static class ApplicationPaths
{
    internal const string ExecutableName = "TomatoDesktop.exe";
    internal const string InstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DesktopNook";
    internal static string LegacyData => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopNook");
    internal static string InstalledData(string directory) => Path.Combine(directory, "Data", "Collection");

    internal static string RegisteredData
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(InstallKey);
            return key?.GetValue("InstallLocation") is string directory && Path.IsPathFullyQualified(directory)
                ? InstalledData(directory)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TomatoDesktop", "Data", "Collection");
        }
    }

    internal static bool IsRegisteredData(string directory) => SamePath(RegisteredData, directory);

    private static bool SamePath(string first, string second) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);

    // Publish a complete collection in one rename. Keep the source as a recoverable backup.
    internal static void PrepareData(string destination, string? source)
    {
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        RejectLink(parent);

        if (string.IsNullOrEmpty(source) || SamePath(source, destination) ||
            !Directory.Exists(source) ||
            !Directory.Exists(Path.Combine(source, "vault")) && !File.Exists(Path.Combine(source, "state.json")) &&
            !File.Exists(Path.Combine(source, "state.json.bak")) && !File.Exists(Path.Combine(source, "migration.json")))
            return;

        RejectLink(source);
        var origin = Path.Combine(destination, ".migration-source");
        if (Directory.Exists(destination))
        {
            RejectLink(destination);
            if (File.Exists(origin) && SamePath(File.ReadAllText(origin), source)) return;
            throw new IOException("新旧位置均有收纳数据。请使用原安装目录，或选择一个全新目录。两处的数据已保留。");
        }

        var staging = Path.Combine(parent, ".migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(source))
            {
                var name = Path.GetFileName(path);
                if (name is "vault" or "backups" or "migration.json" or "operations.log" or "state.json" ||
                    name.StartsWith("state.json.", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("vault.old-", StringComparison.OrdinalIgnoreCase))
                {
                    if (Path.GetFullPath(parent).StartsWith(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        SamePath(path, parent))
                        throw new IOException("请选择旧托管目录之外的安装位置。");
                    DesktopNook.Core.NookStore.CopyPayload(path, Path.Combine(staging, name));
                }
            }
            File.WriteAllText(Path.Combine(staging, ".migration-source"), Path.GetFullPath(source));
            Directory.Move(staging, destination);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("收纳数据目录中有链接或云占位文件，请选择本地存储位置后重试。");
    }
}
