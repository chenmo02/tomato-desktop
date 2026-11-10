using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;

namespace DesktopNook.App;
internal static class SharpIcons
{
    [StructLayout(LayoutKind.Sequential)]private struct Size{public int X,Y;}
    [ComImport,Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface IImageFactory{[PreserveSig]int GetImage(Size size,uint flags,out IntPtr bitmap);}
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHCreateItemFromParsingName(string path,IntPtr bind,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out IImageFactory factory);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(IntPtr bitmap);
    [DllImport("gdi32.dll")]private static extern int GetDIBits(IntPtr dc,IntPtr bitmap,uint first,uint lines,byte[] pixels,ref BitmapInfo info,uint usage);
    [DllImport("user32.dll")]private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")]private static extern int ReleaseDC(IntPtr window,IntPtr dc);
    [StructLayout(LayoutKind.Sequential)]private struct BitmapInfo{public uint Size;public int Width,Height;public ushort Planes,Bits;public uint Compression,ImageSize;public int X,Y;public uint Colors,Important;}
    // Store app shortcuts often have no executable TargetPath. Resolve their
    // AppsFolder identity before asking Shell for an icon (the .lnk is generic).
    internal static string ResolveIconPath(string path)
    {
        if(!path.EndsWith(".lnk",StringComparison.OrdinalIgnoreCase))return path;
        object? shell=null,folder=null,item=null;
        try
        {
            shell=Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            folder=((dynamic)shell!).NameSpace(Path.GetDirectoryName(path));
            if(folder is null)return path;
            item=((dynamic)folder).ParseName(Path.GetFileName(path));
            if(item is null)return path;
            string target=Convert.ToString(((dynamic)item).ExtendedProperty("System.Link.TargetParsingPath"))??"";
            if(target.StartsWith("shell:AppsFolder\\",StringComparison.OrdinalIgnoreCase))return target;
            if(target.Contains('!')&&!target.Contains(':')&&!target.Contains('\\'))return "shell:AppsFolder\\"+target;
        }
        catch { }
        finally { foreach(var com in new[]{item,folder,shell})if(com is not null&&Marshal.IsComObject(com))Marshal.FinalReleaseComObject(com); }
        return path;
    }
    public static ImageSource? Load(string path,int pixels=96)
    {
        var resolved=ResolveIconPath(path);
        return LoadShellImage(resolved,pixels) ?? (resolved!=path?LoadShellImage(path,pixels):null);
    }
    // 图标库一次要加载上百个条目：这些路径本身就能被 Shell 直接解析，
    // 跳过 ResolveIconPath 的 Shell.Application COM 创建，避免队列堆积。
    internal static ImageSource? LoadFast(string path,int pixels=96)=>LoadShellImage(path,pixels);
    private static ImageSource? LoadShellImage(string path,int requestedPixels)
    {
        IImageFactory? factory=null;var bitmap=IntPtr.Zero;
        try
        {
            var iid=typeof(IImageFactory).GUID;if(SHCreateItemFromParsingName(path,IntPtr.Zero,ref iid,out factory)<0)return null;
            // 1 BIGGERSIZEOK + 4 ICONONLY + 0x100 SCALEUP：允许 Shell 返回更大的原始帧，
            // 帧偏小时也由 Shell 先放大到请求尺寸，避免出现「32px 图标被拉伸成 44px」的软边。
            if(factory.GetImage(new(){X=requestedPixels,Y=requestedPixels},0x105,out bitmap)<0||bitmap==IntPtr.Zero)return null;
            var dc=GetDC(IntPtr.Zero);try
            {
                var info=new BitmapInfo{Size=40};if(GetDIBits(dc,bitmap,0,0,[],ref info,0)==0)return null;
                var width=info.Width;var height=Math.Abs(info.Height);if(width is <=0 or >1024||height is <=0 or >1024)return null;
                info=new(){Size=40,Width=width,Height=-height,Planes=1,Bits=32};var pixels=new byte[width*height*4];
                if(GetDIBits(dc,bitmap,0,(uint)height,pixels,ref info,0)==0)return null;
                var alpha=false;for(var i=3;i<pixels.Length;i+=4)if(pixels[i]!=0){alpha=true;break;}
                if(!alpha)for(var i=3;i<pixels.Length;i+=4)pixels[i]=255;
                var image=BitmapSource.Create(width,height,96,96,PixelFormats.Pbgra32,null,pixels,width*4);image.Freeze();return image;
            }finally{ReleaseDC(IntPtr.Zero,dc);}
        }
        catch{return null;}
        finally{if(bitmap!=IntPtr.Zero)DeleteObject(bitmap);if(factory is not null)Marshal.ReleaseComObject(factory);}
    }
}
