using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopNook.App;

// 真实屏幕抓图：把屏幕 DC 拷贝成 32bpp DIB，再交给 WPF 编码成 PNG。
// RenderTargetBitmap 只渲染 WPF 可视树，看不到 DWM 合成的 Mica / Acrylic，
// 验证系统背板是否真正生效必须用这一路。仅用于界面自检。
internal static class ScreenCapture
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, uint rop);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    private const uint SrcCopy = 0x00CC0020;
    private const uint DibRgbColors = 0;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    // 只抓屏幕上的一块矩形（坐标为物理屏幕像素，可为负=左侧扩展屏）。
    // 供自检留档标题栏角落等局部细节；scale 会做整数倍最近邻放大便于肉眼核对。
    public static string SaveRegion(string directory, string fileName, int x, int y, int width, int height, int scale = 1)
    {
        if (width < 1 || height < 1) throw new InvalidOperationException("抓图区域无效。");
        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var previous = SelectObject(memoryDc, bitmap);
        try
        {
            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y, SrcCopy))
                throw new InvalidOperationException("BitBlt 局部抓屏失败。");
            var info = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width, Height = -height, Planes = 1, BitCount = 32, Compression = 0
            };
            var stride = width * 4;
            var pixels = new byte[stride * height];
            if (GetDIBits(memoryDc, bitmap, 0, (uint)height, pixels, ref info, DibRgbColors) == 0)
                throw new InvalidOperationException("GetDIBits 读取像素失败。");
            BitmapSource output = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            if (scale > 1)
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.PushTransform(new ScaleTransform(scale, scale));
                    dc.DrawImage(output, new Rect(0, 0, width, height));
                    dc.Pop();
                }
                var enlarged = new RenderTargetBitmap(width * scale, height * scale, 96, 96, PixelFormats.Pbgra32);
                enlarged.Render(visual);
                output = enlarged;
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(output));
            var path = Path.Combine(directory, fileName);
            using var file = File.Create(path);
            encoder.Save(file);
            return path;
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public static string Save(string directory, string fileName)
    {
        var left = GetSystemMetrics(SM_XVIRTUALSCREEN);
        var top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        var width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        var height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        if (width < 1 || height < 1) throw new InvalidOperationException("无法读取虚拟屏幕尺寸。");

        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var previous = SelectObject(memoryDc, bitmap);
        try
        {
            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, left, top, SrcCopy))
                throw new InvalidOperationException("BitBlt 抓屏失败。");

            // 负高度 = top-down DIB，首行即屏幕顶部，与 BitmapSource 的行序一致。
            var info = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width, Height = -height, Planes = 1, BitCount = 32, Compression = 0
            };
            var stride = width * 4;
            var pixels = new byte[stride * height];
            if (GetDIBits(memoryDc, bitmap, 0, (uint)height, pixels, ref info, DibRgbColors) == 0)
                throw new InvalidOperationException("GetDIBits 读取像素失败。");

            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            var path = Path.Combine(directory, fileName);
            using var file = File.Create(path);
            encoder.Save(file);
            return path;
        }
        finally
        {
            SelectObject(memoryDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
