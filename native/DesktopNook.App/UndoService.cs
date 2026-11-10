using System.IO;
using System.Text;

namespace DesktopNook.App;

// 撤销与审计：内存保留最近 20 步可逆操作，磁盘保留 30 天操作日志。
// 只记录「用户主动触发且会改变收纳状态」的动作，纯界面切换不进栈。
internal sealed class UndoService
{
    private sealed record Step(string Text, Action Rollback, DateTime At);
    private readonly List<Step> _steps = [];
    private readonly string _logPath;
    private const int MaxSteps = 20;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    public UndoService(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _logPath = Path.Combine(dataDirectory, "operations.log");
        PruneLog();
    }

    public int Count => _steps.Count;
    public string? Next => _steps.Count > 0 ? _steps[^1].Text : null;

    public void Record(string text, Action rollback)
    {
        _steps.Add(new(text, rollback, DateTime.Now));
        while (_steps.Count > MaxSteps) _steps.RemoveAt(0);
        AppendLog(text);
    }

    public string Undo()
    {
        if (_steps.Count == 0) return "没有可撤销的操作";
        var step = _steps[^1];
        _steps.RemoveAt(_steps.Count - 1);
        try { step.Rollback(); }
        catch (Exception ex)
        {
            AppendLog("撤销失败：" + step.Text + "（" + ex.Message + "）");
            return "撤销未完成：" + ex.Message;
        }
        AppendLog("撤销：" + step.Text);
        return "已撤销：" + step.Text;
    }

    public string Recent(int count = 12)
    {
        try
        {
            if (!File.Exists(_logPath)) return "暂无操作记录";
            return string.Join("\n", File.ReadAllLines(_logPath).TakeLast(count));
        }
        catch { return "操作记录读取失败"; }
    }

    private void AppendLog(string text)
    {
        try
        {
            var clean = text.Replace('\t', ' ').Replace("\r", " ").Replace("\n", " ");
            File.AppendAllText(_logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\t" + clean + Environment.NewLine, Encoding.UTF8);
        }
        catch { /* 日志写入失败不影响主流程 */ }
    }

    private void PruneLog()
    {
        try
        {
            if (!File.Exists(_logPath)) return;
            var lines = File.ReadAllLines(_logPath);
            var cutoff = DateTime.Now - Retention;
            var kept = lines.Where(line => line.Length < 19 || !DateTime.TryParse(line[..19], out var at) || at >= cutoff).ToArray();
            if (kept.Length != lines.Length) File.WriteAllLines(_logPath, kept, Encoding.UTF8);
        }
        catch { /* 日志清理失败不影响主流程 */ }
    }
}
