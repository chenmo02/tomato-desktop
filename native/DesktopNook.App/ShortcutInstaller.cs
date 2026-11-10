using System.IO;
using System.Runtime.InteropServices;

namespace DesktopNook.App;

internal static class ShortcutInstaller
{
    [StructLayout(LayoutKind.Sequential)]private struct PropertyKey {public Guid Format;public uint Id;}
    [StructLayout(LayoutKind.Explicit,Size=24)]private struct Variant {[FieldOffset(0)]public ushort Type;[FieldOffset(8)]public IntPtr Text;}
    [ComImport,Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        uint GetCount();void GetAt(uint index,out PropertyKey key);void GetValue(ref PropertyKey key,out Variant value);void SetValue(ref PropertyKey key,ref Variant value);void Commit();
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHGetPropertyStoreFromParsingName(string path,IntPtr bind,uint flags,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out IPropertyStore store);
    [DllImport("shell32.dll")]private static extern void SHChangeNotify(uint eventId,uint flags,IntPtr item1,IntPtr item2);
    public static string Install()=>Create(Environment.SpecialFolder.Programs);
    public static string InstallDesktop()=>Create(Environment.SpecialFolder.DesktopDirectory);
    // 开始菜单与桌面入口都要带 AppUserModelID，任务栏才能识别同一个应用。
    // 单个入口创建失败不应影响另一个，两个都失败才视为失败。
    public static string[] InstallAll()
    {
        var links=new System.Collections.Generic.List<string>();System.Exception? failure=null;
        foreach(var folder in new[]{Environment.SpecialFolder.Programs,Environment.SpecialFolder.DesktopDirectory})
            try{links.Add(Create(folder));}catch(System.Exception ex){failure??=ex;}
        if(links.Count==0)throw failure??new IOException("无法创建 Windows 快捷方式。");
        // 重建快捷方式后通知 Shell 刷新图标缓存，已固定到任务栏的入口才会换成新图标。
        try{SHChangeNotify(0x08000000,0,IntPtr.Zero,IntPtr.Zero);}catch{/* 刷新失败不影响快捷方式本身 */}
        return links.ToArray();
    }
    private static string Create(Environment.SpecialFolder folder)
    {
        var program=Environment.ProcessPath??throw new IOException("无法确定程序位置。");
        var directory=Environment.GetFolderPath(folder);
        if(string.IsNullOrWhiteSpace(directory))throw new IOException("无法确定快捷方式目录。");
        Directory.CreateDirectory(directory);
        var link=Path.Combine(directory,"番茄桌面.lnk");
        var type=Type.GetTypeFromProgID("WScript.Shell")??throw new IOException("无法创建 Windows 快捷方式。");
        dynamic shell=Activator.CreateInstance(type)!;dynamic shortcut=shell.CreateShortcut(link);
        try{shortcut.TargetPath=program;shortcut.Arguments="";shortcut.WorkingDirectory=Path.GetDirectoryName(program);shortcut.IconLocation=program+",0";shortcut.Description="番茄桌面 — 把桌面图标，喂给番茄";shortcut.Save();}
        finally{Marshal.FinalReleaseComObject(shortcut);Marshal.FinalReleaseComObject(shell);}
        var iid=typeof(IPropertyStore).GUID;Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(link,IntPtr.Zero,2,ref iid,out var store));
        var key=new PropertyKey{Format=new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),Id=5};
        var value=new Variant{Type=31,Text=Marshal.StringToCoTaskMemUni("DesktopNook.DesktopNook")};
        try{store.SetValue(ref key,ref value);store.Commit();}finally{Marshal.FreeCoTaskMem(value.Text);Marshal.FinalReleaseComObject(store);}
        return link;
    }
}
