using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopNook.App;

// 混合 DPI 多屏回归自检（--dpi-self-test，必须在 --sandbox 下运行）。
// 复现思路：真实换屏不好自动化，于是在同一 HWND 上模拟「DPI 变化后系统把窗口
// 物理矩形放大」这一步。旧代码只在 SizeChanged / DpiChanged 的同步阶段重裁 HRGN，
// 读到的还是旧矩形，HRGN 于是停在旧尺寸；新代码由 Win32 消息钩子延后重裁。
internal static class DpiVerification
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    private const uint WmDpiChanged = 0x02E0;
    private const uint SwpNoZOrderNoActivate = 0x0014;

    internal static async Task Run(MainWindow window, string root)
    {
        var checks = new List<string>();
        string failure = "";
        void Check(bool result, string name)
        {
            if (!result) throw new Exception(name);
            checks.Add(name);
        }
        async Task Settle()
        {
            // ContextIdle 低于 Background：等它执行完，说明前面排队的重裁刷新已跑完。
            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await Task.Delay(120);
            window.UpdateLayout();
        }
        Size PhysicalWindowSize(IntPtr hwnd)
        {
            if (!GetWindowRect(hwnd, out var rect)) return new Size(-1, -1);
            return new Size(rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
        try
        {
            if (!App.IsSandbox) throw new Exception("DPI 自检只能在隔离目录运行。");
            window.KeepOpenForVerification = true;
            var hwnd = new WindowInteropHelper(window).Handle;
            Check(hwnd != IntPtr.Zero, "自检窗口句柄就绪");
            await Settle();

            // 1) 启动后的 HRGN 必须覆盖整个物理窗口。
            window.ApplyWindowRegion();
            var initial = PhysicalWindowSize(hwnd);
            var region = BackdropService.LastRegionSize;
            Check(Math.Abs(region.Width - initial.Width) <= 1 && Math.Abs(region.Height - initial.Height) <= 1,
                $"初始圆角区域覆盖整个窗口（窗 {initial.Width:F0}×{initial.Height:F0}，区域 {region.Width:F0}×{region.Height:F0}）");
            Check(BackdropService.RegionRadius > 0, "圆角区域已生效而非直角回退");

            // 2) 壁纸对齐用的坐标系必须是「虚拟屏幕物理像素」且窗口矩形落在其中。
            var screen = window.DiagnosticScreenPixelSize();
            var screenRect = window.DiagnosticWindowScreenRect();
            Check(screen.Width > 0 && screen.Height > 0
                  && screen.Width >= GetSystemMetrics(78) - 1 && screen.Height >= GetSystemMetrics(79) - 1,
                "壁纸铺放尺寸取整个虚拟屏幕的物理像素");
            Check(screenRect.Width > 0 && screenRect.Height > 0
                  && screenRect.X >= -1 && screenRect.Y >= -1
                  && screenRect.Right <= screen.Width + 1 && screenRect.Bottom <= screen.Height + 1,
                $"窗口屏幕矩形与虚拟屏幕同一坐标系（窗 {screenRect}，虚拟屏 {screen.Width:F0}×{screen.Height:F0}）");

            // 3) 归一化裁切：左侧扩展屏（虚拟原点为负）与右侧主屏应取到壁纸的不同片段。
            var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
            var virtualScreen = new Size(4480, 1440);
            var left = SkinService.ComputeWallpaperViewbox(image, new Rect(0, 0, 800, 600), virtualScreen, false);
            var right = SkinService.ComputeWallpaperViewbox(image, new Rect(3000, 0, 800, 600), virtualScreen, false);
            var stretched = SkinService.ComputeWallpaperViewbox(image, new Rect(0, 0, 800, 600), virtualScreen, true);
            Check(left is not null, "扩展屏负坐标归一化后得到裁切区域");
            if (left is not Rect a) throw new Exception("扩展屏负坐标归一化后得到裁切区域");
            Check(a.X >= 0 && a.Y >= 0 && a.Width > 0 && a.Width <= 1 && a.Height > 0 && a.Height <= 1,
                "扩展屏负坐标归一化后裁切区域合法");
            Check(right is Rect b && b.X > a.X, "不同屏幕上的窗口取到不同壁纸片段");
            Check(stretched is Rect s && s.Width > 0 && s.Height > 0, "拉伸壁纸样式同样得到合法裁切区域");

            // 4) 模拟系统把窗口物理矩形放大 1.5 倍（100% → 150% DPI 的结果），
            //    并补发 WM_DPICHANGED。HRGN 必须跟着长大，内容层也要铺满新窗口。
            var before = MainWindow.DpiLayoutRefreshCount;
            var start = PhysicalWindowSize(hwnd);
            GetWindowRect(hwnd, out var placement);
            var grown = new Size(Math.Round(start.Width * 1.5), Math.Round(start.Height * 1.5));
            var dpi = GetDpiForWindow(hwnd);
            if (dpi == 0) dpi = 96;
            // 新的 DPI 与物理矩形同比例放大：WPF 的 DIP 尺寸应当保持不变，
            // 于是 SizeChanged 不会触发，正是旧代码漏掉 HRGN 重裁的那条路径。
            var newDpi = (uint)Math.Round(dpi * 1.5);
            var suggested = new NativeRect
            {
                Left = placement.Left, Top = placement.Top,
                Right = placement.Left + (int)grown.Width, Bottom = placement.Top + (int)grown.Height
            };
            var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeRect>());
            try
            {
                Marshal.StructureToPtr(suggested, buffer, false);
                SendMessage(hwnd, WmDpiChanged, (IntPtr)(int)(newDpi | (newDpi << 16)), buffer);
            }
            finally { Marshal.FreeHGlobal(buffer); }
            await Settle();

            Check(MainWindow.DpiLayoutRefreshCount > before, "WM_DPICHANGED 触发延后的布局与区域刷新");
            var actual = PhysicalWindowSize(hwnd);
            var refreshedRegion = BackdropService.LastRegionSize;
            Check(Math.Abs(refreshedRegion.Width - actual.Width) <= 1 && Math.Abs(refreshedRegion.Height - actual.Height) <= 1,
                $"DPI 缩放后 HRGN 随物理窗口重裁（窗 {actual.Width:F0}×{actual.Height:F0}，区域 {refreshedRegion.Width:F0}×{refreshedRegion.Height:F0}）");
            // WPF 在部分版本不会主动把 HWND 调到 suggested rect；此时由自检直接
            // 补上这一步，再验证 HRGN 依然会跟随（覆盖反向拖回的情形）。
            if (actual.Width <= start.Width + 10)
            {
                Check(SetWindowPos(hwnd, IntPtr.Zero, placement.Left, placement.Top, (int)grown.Width, (int)grown.Height, SwpNoZOrderNoActivate),
                    "模拟 DPI 缩放后的窗口物理矩形放大");
                await Settle();
                actual = PhysicalWindowSize(hwnd);
                refreshedRegion = BackdropService.LastRegionSize;
                Check(Math.Abs(refreshedRegion.Width - actual.Width) <= 1 && Math.Abs(refreshedRegion.Height - actual.Height) <= 1,
                    "窗口物理矩形变化后 HRGN 同步重裁");
            }
            Check(refreshedRegion.Width > region.Width + 10, "HRGN 不是停留在旧 DPI 的尺寸");
            Check(Math.Abs(window.Surface.Width - window.ActualWidth) < 2 && Math.Abs(window.Surface.Height - window.ActualHeight) < 2,
                "内容层 Surface 跟随窗口逻辑尺寸重排");
            var clip = window.DiagnosticGlassClipRect();
            Check(clip.Width > window.Surface.Width * .95 && clip.Height > window.Surface.Height * .95,
                $"圆角裁剪覆盖整块内容层（clip {clip.Width:F0}×{clip.Height:F0}）");

            // 5) 缩回原尺寸后同样要跟上，覆盖「外接屏拖回笔记本」的反向路径。
            Check(SetWindowPos(hwnd, IntPtr.Zero, placement.Left, placement.Top, (int)start.Width, (int)start.Height, SwpNoZOrderNoActivate),
                "窗口缩回原物理尺寸");
            await Settle();
            var restored = PhysicalWindowSize(hwnd);
            var restoredRegion = BackdropService.LastRegionSize;
            Check(Math.Abs(restoredRegion.Width - restored.Width) <= 1 && Math.Abs(restoredRegion.Height - restored.Height) <= 1,
                "反向拖回后 HRGN 同步缩回");
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            File.WriteAllText(Path.Combine(root, "dpi-verification.json"),
                JsonSerializer.Serialize(new { passed = checks.Count, success = failure == "", failure, checks },
                    new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown(failure == "" ? 0 : 1);
        }
    }
}
