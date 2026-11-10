using System.Runtime.InteropServices;
namespace DesktopNook.Core;
internal static class WindowsStreams
{
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct StreamData{public long Size;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=296)]public string Name;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr FindFirstStreamW(string file,int level,out StreamData data,int flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool FindNextStreamW(IntPtr handle,out StreamData data);
    [DllImport("kernel32.dll")]private static extern bool FindClose(IntPtr handle);
    public static void Copy(string source,string destination)
    {
        if(!OperatingSystem.IsWindows())return;
        var handle=FindFirstStreamW(source,0,out var data,0);
        if(handle==(IntPtr)(-1)){var error=Marshal.GetLastWin32Error();if(error is 38 or 1 or 50 or 87)return;throw new IOException("无法读取快捷方式附加流。",new System.ComponentModel.Win32Exception(error));}
        try
        {
            do
            {
                if(data.Name=="::$DATA")continue;
                if(!data.Name.StartsWith(':')||!data.Name.EndsWith(":$DATA",StringComparison.Ordinal)||data.Name.Contains('\\')||data.Name.Contains('/'))throw new IOException("附加流名称无效。");
                using var input=File.OpenRead(source+data.Name);using var output=new FileStream(destination+data.Name,FileMode.CreateNew,FileAccess.Write,FileShare.None);input.CopyTo(output);output.Flush(true);
            }while(FindNextStreamW(handle,out data));
            var last=Marshal.GetLastWin32Error();if(last!=38)throw new IOException("读取快捷方式附加流未完成。",new System.ComponentModel.Win32Exception(last));
        }
        finally{FindClose(handle);}
    }
}
