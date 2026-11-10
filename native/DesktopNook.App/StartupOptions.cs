using Microsoft.Win32;

namespace DesktopNook.App;
internal sealed record WindowPosition { public bool Manual{get;init;} public double Left{get;init;} public double Top{get;init;} }
// 应用级偏好集中成一个 JSON 值：导出/导入配置包时随数据一起打包，不需要逐键搬运。
internal sealed record AppSettings
{
    // 主题：system 跟随系统、light 固定浅色、dark 固定深色。
    public string Theme {get;init;}="system";
    // 皮肤：native 原生 Win11 外观（默认）、glass 玻璃拟态。脏值由 SkinService.Normalize 回落。
    // 与整个 AppSettings 一起序列化，配置包导出/导入自动带上，无需单独搬运。
    public string Skin {get;init;}="native";
    public bool AccentFollow {get;init;}=true;
    public bool AutoCollect {get;init;}
    public bool HotKey {get;init;}=true;
    public bool InfoBarDismissed {get;init;}
    // 失焦自动收起：默认关闭，面板只能通过 × / 热键 / 任务栏按钮手动收起。
    public bool AutoHideOnDeactivate {get;init;}
    // 「全部图标」页是否列出 Windows 工具、命令行、诊断组件等系统项目；入口在「设置 → 行为」。
    public bool ShowSystemItems {get;init;}
    public string ThemeMode => Theme is "light" or "dark"?Theme:"system";
}
internal sealed class StartupOptions(string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run",string settingsKey = @"Software\DesktopNook\Settings")
{
    private const string ValueName="DesktopNook";
    internal WindowSizePreference ReadWindowSize()
    {
        try{using var key=Registry.CurrentUser.OpenSubKey(settingsKey);return (System.Text.Json.JsonSerializer.Deserialize<WindowSizePreference>(key?.GetValue("WindowSize") as string??"{}")??new()).Validated();}catch{return new();}
    }
    internal void WriteWindowSize(WindowSizePreference value){using var key=Registry.CurrentUser.CreateSubKey(settingsKey,true);key.SetValue("WindowSize",System.Text.Json.JsonSerializer.Serialize(value.Validated()),RegistryValueKind.String);key.Flush();}
    internal (bool Manual,double Left,double Top) ReadWindowPosition()
    {
        try
        {
            using var key=Registry.CurrentUser.OpenSubKey(settingsKey);
            var position=System.Text.Json.JsonSerializer.Deserialize<WindowPosition>(key?.GetValue("WindowPosition") as string??"{}")??new();
            return(position.Manual&&double.IsFinite(position.Left)&&double.IsFinite(position.Top),position.Left,position.Top);
        }
        catch{return(false,0,0);}
    }
    internal void WriteWindowPosition(bool manual,double left,double top){using var key=Registry.CurrentUser.CreateSubKey(settingsKey,true);key.SetValue("WindowPosition",System.Text.Json.JsonSerializer.Serialize(new WindowPosition{Manual=manual,Left=left,Top=top}),RegistryValueKind.String);key.Flush();}
    public bool Enabled {get{using var key=Registry.CurrentUser.OpenSubKey(runKey);return key?.GetValue(ValueName) is string value&&!string.IsNullOrWhiteSpace(value);}}
    public string? Command {get{using var key=Registry.CurrentUser.OpenSubKey(runKey);return key?.GetValue(ValueName) as string;}}
    public bool CloseEndsProcess {get{using var key=Registry.CurrentUser.OpenSubKey(settingsKey);return key?.GetValue("CloseEndsProcess") is not int value || value!=0;}set{using var key=Registry.CurrentUser.CreateSubKey(settingsKey,true);key.SetValue("CloseEndsProcess",value?1:0,RegistryValueKind.DWord);key.Flush();}}
    public void SetEnabled(bool enabled,string? executable=null)
    {
        using var key=Registry.CurrentUser.CreateSubKey(runKey,true);
        if(enabled)
        {
            executable??=Environment.ProcessPath??throw new InvalidOperationException("无法确定程序位置。");
            if(executable.Contains('"'))throw new ArgumentException("程序路径无效。");
            var command="\""+executable+"\" --autostart";
            if(command.Length>260)throw new InvalidOperationException("程序路径过长，请将程序放在较短路径后启用自启。");
            key.SetValue(ValueName,command,RegistryValueKind.String);
        }
        else key.DeleteValue(ValueName,false);
        key.Flush();
    }
    public void RefreshEnabledPath(){if(Enabled)SetEnabled(true);}
    internal AppSettings ReadSettings()
    {
        try{using var key=Registry.CurrentUser.OpenSubKey(settingsKey);return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(key?.GetValue("AppSettings") as string??"{}")??new();}catch{return new();}
    }
    internal void WriteSettings(AppSettings value){using var key=Registry.CurrentUser.CreateSubKey(settingsKey,true);key.SetValue("AppSettings",System.Text.Json.JsonSerializer.Serialize(value),RegistryValueKind.String);key.Flush();}
}
