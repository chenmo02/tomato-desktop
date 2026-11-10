using DesktopNook.Core;
using System.IO;
using Microsoft.Win32;

namespace DesktopNook.App;

internal sealed class WindowsRecycleBin(string? testBase = null) : IRecycleBin
{
    public const string RecycleId = "{645FF040-5081-101B-9F08-00AA002F954E}";
    private string BaseKey => testBase ?? @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons";
    private static readonly string[] Panels = ["NewStartPanel", "ClassicStartMenu"];
    public bool IsVisible => Capture()["NewStartPanel"] != 1;
    public Dictionary<string,int?> Capture()
    {
        var result = new Dictionary<string,int?>();
        foreach(var panel in Panels)
        {
            using var key = Registry.CurrentUser.OpenSubKey(BaseKey + "\\" + panel);
            var value = key?.GetValue(RecycleId);
            if (value is not null && value is not int) throw new IOException("系统回收站图标设置格式异常，未进行修改。");
            result[panel] = value as int?;
        }
        return result;
    }
    private void CheckPolicies()
    {
        if (testBase is not null) return;
        foreach(var hive in new[]{Registry.CurrentUser, Registry.LocalMachine})
        {
            using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\NonEnum");
            if (key?.GetValue(RecycleId) is int value && value != 0) throw new IOException("回收站显示由系统策略控制，未修改该策略。");
        }
    }
    public void Hide()
    {
        CheckPolicies();
        foreach(var panel in Panels) { using var key = Registry.CurrentUser.CreateSubKey(BaseKey + "\\" + panel, true); key.SetValue(RecycleId,1,RegistryValueKind.DWord); key.Flush(); }
        if(IsVisible) throw new IOException("未能保存回收站隐藏设置。"); Refresh();
    }
    public void Restore(Dictionary<string,int?> snapshot)
    {
        foreach(var panel in Panels)
        {
            using var key = Registry.CurrentUser.CreateSubKey(BaseKey + "\\" + panel, true);
            if(snapshot[panel] is int value) key.SetValue(RecycleId,value,RegistryValueKind.DWord); else key.DeleteValue(RecycleId,false); key.Flush();
        }
        if(Capture().Any(pair => pair.Value != snapshot[pair.Key])) throw new IOException("回收站显示设置恢复未完成，恢复记录已保留。");
        Refresh();
    }
    private void Refresh() { if(testBase is null) ShellIntegration.RefreshDesktop(); }
}
