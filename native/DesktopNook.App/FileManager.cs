using System;
using System.Diagnostics;

namespace DesktopNook.App;

// 「更多 → 文件管理」子菜单已按需求移除；这里只保留打开目录的通用能力，
// 供「打开数据目录」菜单项与「设置 → 数据与备份 → 数据目录」共用。
public partial class MainWindow
{
    private void OpenExplorer(string? path)
    {
        try
        {
            var start=new ProcessStartInfo("explorer.exe"){UseShellExecute=true};
            if(!string.IsNullOrWhiteSpace(path))start.ArgumentList.Add(path);
            Process.Start(start);
            SetStatus("已打开文件管理器");
        }
        catch(Exception ex){SetStatus("无法打开文件管理器："+ex.Message);}
    }
}
