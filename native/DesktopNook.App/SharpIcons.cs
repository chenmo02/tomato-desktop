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
                NormalizeIconAlpha(pixels);
                // exe 只带小尺寸图标帧时，Shell 不会放大图标，而是返回「请求尺寸 +
                // 一圈半透明描边 + 居中原始图标」的画布。2× 超采样把 44px 显示请求提到
                // 88px 后，WPF 缩回去就成了「灰框里一个小图标」；开始菜单取的是原生
                // 48px 帧，所以没有这个问题。裁掉描边与留白，把图标按原比例放大回
                // 请求尺寸，位图尺寸保持不变，缓存键和像素尺寸断言都不受影响。
                var box=SolidBounds(pixels,width,height);
                if(box.Width>0&&box.Height>0&&(box.Width<width*0.9||box.Height<height*0.9))pixels=RemoveIconFrame(pixels,width,height,box);
                var image=BitmapSource.Create(width,height,96,96,PixelFormats.Pbgra32,null,pixels,width*4);image.Freeze();return image;
            }finally{ReleaseDC(IntPtr.Zero,dc);}
        }
        catch{return null;}
        finally{if(bitmap!=IntPtr.Zero)DeleteObject(bitmap);if(factory is not null)Marshal.ReleaseComObject(factory);}
    }

    // alpha>=128 的实心范围。Shell 给小图标加的描边是 alpha 5~38 的半透明黑，会被排除。
    private static Int32Rect SolidBounds(byte[] pixels,int width,int height)
    {
        int left=width,top=height,right=-1,bottom=-1;
        for(int y=0;y<height;y++)
            for(int x=0;x<width;x++)
            {
                if(pixels[(y*width+x)*4+3]<128)continue;
                if(x<left)left=x;if(x>right)right=x;
                if(y<top)top=y;if(y>bottom)bottom=y;
            }
        return right<left?default:new Int32Rect(left,top,right-left+1,bottom-top+1);
    }
    // 把实心范围按原比例双线性放大到整张画布（居中），其余像素留全透明；
    // 数据在预乘 alpha 空间，插值不会产生黑边。
    private static byte[] RemoveIconFrame(byte[] pixels,int width,int height,Int32Rect box)
    {
        var scaled=new byte[width*height*4];
        double ratio=Math.Min((double)width/box.Width,(double)height/box.Height);
        int drawW=Math.Max(1,(int)Math.Round(box.Width*ratio)),drawH=Math.Max(1,(int)Math.Round(box.Height*ratio));
        int left=(width-drawW)/2,top=(height-drawH)/2;
        int maxX=box.X+box.Width-1,maxY=box.Y+box.Height-1;
        for(int y=0;y<drawH;y++)
        {
            double sy=box.Y+(y+0.5)*box.Height/drawH-0.5;
            int y0=Math.Clamp((int)Math.Floor(sy),box.Y,maxY),y1=Math.Min(y0+1,maxY);double fy=sy-y0;
            for(int x=0;x<drawW;x++)
            {
                double sx=box.X+(x+0.5)*box.Width/drawW-0.5;
                int x0=Math.Clamp((int)Math.Floor(sx),box.X,maxX),x1=Math.Min(x0+1,maxX);double fx=sx-x0;
                int dst=((top+y)*width+left+x)*4;
                int a00=(y0*width+x0)*4,a10=(y0*width+x1)*4,a01=(y1*width+x0)*4,a11=(y1*width+x1)*4;
                for(int c=0;c<4;c++)
                {
                    double v0=pixels[a00+c]+(pixels[a10+c]-pixels[a00+c])*fx;
                    double v1=pixels[a01+c]+(pixels[a11+c]-pixels[a01+c])*fx;
                    scaled[dst+c]=(byte)Math.Clamp((int)Math.Round(v0+(v1-v0)*fy),0,255);
                }
            }
        }
        return scaled;
    }

    // 只修正图标透明边缘的颜色数据，消除圆形图标外沿的白环，不改动任何可见图形。
    // Shell 返回的 32bpp DIB 未必预乘 alpha，透明像素的 RGB 常被留成白色；BitmapSource
    // 按 Pbgra32 解释时，这些白色会随 2× 超采样后的高质量缩小渗进半透明抗锯齿边缘。
    // 判据：预乘格式下任一通道都 <= a，若存在 0<a<255 且 RGB>a 即说明数据未预乘。
    internal static void NormalizeIconAlpha(byte[] pixels)
    {
        bool unpremultiplied=false;
        for(int o=0;o<pixels.Length;o+=4)
        {
            int a=pixels[o+3];
            if(a>0&&a<255&&(pixels[o]>a||pixels[o+1]>a||pixels[o+2]>a)){unpremultiplied=true;break;}
        }
        for(int o=0;o<pixels.Length;o+=4)
        {
            int a=pixels[o+3];
            if(a==0){pixels[o]=pixels[o+1]=pixels[o+2]=0;continue;}
            // 几乎透明、颜色又是白的 fringe 像素直接丢弃，它们只会贡献一圈白光晕。
            if(a<64)
            {
                int r=pixels[o+2]*255/a,g=pixels[o+1]*255/a,b=pixels[o]*255/a;
                if(r>=200&&g>=200&&b>=200){pixels[o]=pixels[o+1]=pixels[o+2]=pixels[o+3]=0;continue;}
            }
            if(!unpremultiplied||a==255)continue;
            pixels[o]=(byte)(pixels[o]*a/255);
            pixels[o+1]=(byte)(pixels[o+1]*a/255);
            pixels[o+2]=(byte)(pixels[o+2]*a/255);
        }
    }
}
