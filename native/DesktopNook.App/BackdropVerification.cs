using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopNook.App;

// 启动态背板自检（--backdrop-self-test，必须在 --sandbox 下运行）。
// 与 --ui-self-test 的区别：后者会显式调用 ApplyBackdrop() 再断言，因此无法暴露
// 「刚启动时还不透明、进设置切换一次 UI 才透明」这类初始化时序缺陷。
// 本自检在任何人为干预之前读取窗口状态。
internal static class BackdropVerification
{
    internal static async Task Run(MainWindow window, string root)
    {
        var checks = new List<string>();
        string failure = "";
        void Check(bool result, string name)
        {
            if (!result) throw new Exception(name);
            checks.Add(name);
        }
        var diagnostics = new Dictionary<string, object?>
        {
            ["statusAtSourceInitialized"] = MainWindow.BackdropStatusAtSourceInitialized,
            ["kindAtSourceInitialized"] = MainWindow.BackdropKindAtSourceInitialized.ToString()
        };
        try
        {
            if (!App.IsSandbox) throw new Exception("背板自检只能在隔离目录运行。");
            window.KeepOpenForVerification = true;
            // Background 优先于 ContextIdle：等到这里，构造函数里排队的
            // RefreshGlassWallpaper 已经执行完，看到的是真正的启动态。
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await Task.Delay(600);
            window.UpdateLayout();
            // RenderTargetBitmap 看不到 DWM 背板，必须抓真实屏幕才能确认是否透出壁纸。
            // 裁出窗口矩形便于逐张比对：RenderTargetBitmap 看不到 DWM 背板，
            // 只有真实屏幕抓图能证明「是否真的透出壁纸」。
            string Capture(string tag)
            {
                var capture = ScreenCapture.Save(root, "backdrop-" + tag + ".png");
                var area = window.DiagnosticWindowScreenRect();
                var full = new BitmapImage();
                full.BeginInit();
                full.UriSource = new Uri(capture, UriKind.Absolute);
                full.CacheOption = BitmapCacheOption.OnLoad;
                full.EndInit();
                var x = Math.Clamp((int)area.X, 0, full.PixelWidth - 1);
                var y = Math.Clamp((int)area.Y, 0, full.PixelHeight - 1);
                var w = Math.Clamp((int)area.Width, 1, full.PixelWidth - x);
                var h = Math.Clamp((int)area.Height, 1, full.PixelHeight - y);
                var cropped = new CroppedBitmap(full, new Int32Rect(x, y, w, h));
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(cropped));
                using var file = File.Create(Path.Combine(root, "backdrop-" + tag + "-crop.png"));
                encoder.Save(file);
                return capture;
            }
            try { diagnostics["coldStartCapture"] = Capture("cold-start"); }
            catch (Exception ex) { diagnostics["captureError"] = ex.Message; }
            diagnostics["backdropSettleCount"] = MainWindow.BackdropSettleCount;

            var hwnd = new WindowInteropHelper(window).Handle;
            var target = HwndSource.FromHwnd(hwnd)?.CompositionTarget;
            var glass = ThemeService.IsGlass;
            var platform = BackdropService.PlatformSupported();
            var transparency = BackdropService.SystemTransparencyEnabled();
            var acrylic = BackdropService.AcrylicVisible;
            var expected = glass ? BackdropKind.Acrylic : BackdropKind.Mica;
            var backdropCanWork = platform && (!glass || transparency);

            diagnostics["glassSkin"] = glass;
            diagnostics["platformSupported"] = platform;
            diagnostics["systemTransparency"] = transparency;
            diagnostics["status"] = BackdropService.Status;
            diagnostics["current"] = BackdropService.Current.ToString();
            diagnostics["acrylicVisible"] = acrylic;
            diagnostics["compositionTargetBackground"] = target?.BackgroundColor.ToString();
            diagnostics["winWindowFill"] = (Application.Current.Resources["WinWindowFill"] as SolidColorBrush)?.Color.ToString();
            diagnostics["winNavFill"] = (Application.Current.Resources["WinNavFill"] as SolidColorBrush)?.Color.ToString();
            diagnostics["storedSkin"] = App.Options.ReadSettings().Skin;
            diagnostics["skinBackdropVisible"] = window.SkinBackdropVisible;
            diagnostics["skinBackdropAllowsAcrylic"] = window.SkinBackdropAllowsAcrylic;
            diagnostics["wallpaperViewbox"] = window.SkinWallpaperViewbox?.ToString();

            if (backdropCanWork)
            {
                Check(BackdropService.Current == expected,
                    $"启动后 DWM 背板已生效（期望 {expected}，实际 {BackdropService.Current}；{BackdropService.Status}）");
                Check(target is not null && target.BackgroundColor.A == 0,
                    $"WPF 清屏色为透明，背板可见（实际 {target?.BackgroundColor}）");
            }
            else
            {
                diagnostics["skipped"] = "系统不支持 DWM 背板或透明效果关闭，只验证回退层";
                checks.Add("系统不满足背板前提，跳过 DWM 断言");
            }

            Check(MainWindow.BackdropSettleCount >= 1,
                "窗口首帧完成后已重新声明 DWM 背板并收敛背景层（无需手动切 UI）");

            if (glass)
            {
                // 核心断言：自绘背景层必须与 DWM 状态一致。启动时若背板声明失败，
                // 这里会留下一层不透明渐变，把后续生效的 Acrylic 完全盖住。
                Check(window.SkinBackdropAllowsAcrylic == acrylic,
                    $"自绘背景层与 Acrylic 状态一致（acrylic={acrylic}，层透明={window.SkinBackdropAllowsAcrylic}）");
                if (acrylic)
                    Check(window.SkinWallpaperViewbox is null, "Acrylic 生效时未铺自绘壁纸层");
                else
                    Check(window.SkinWallpaperViewbox is not null, "回退态使用按窗口位置对齐的壁纸层");
                Check(MainWindow.BackdropKindAtSourceInitialized == BackdropService.Current,
                    $"SourceInitialized 阶段一次声明成功，无需用户手动切换（初始 {MainWindow.BackdropKindAtSourceInitialized} / {MainWindow.BackdropStatusAtSourceInitialized}，最终 {BackdropService.Current}）");
            }
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            File.WriteAllText(Path.Combine(root, "backdrop-verification.json"),
                JsonSerializer.Serialize(new
                {
                    passed = checks.Count,
                    success = failure == "",
                    failure,
                    checks,
                    diagnostics
                }, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown(failure == "" ? 0 : 1);
        }
    }
}
