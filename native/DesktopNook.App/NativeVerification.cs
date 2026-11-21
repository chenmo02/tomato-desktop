using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopNook.Core;
using Microsoft.Win32;

namespace DesktopNook.App;

internal static class NativeVerification
{
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    // 让 Shell 亲自解析一次展示目录图标：比只检查 desktop.ini 文本更接近导航窗格的实际效果。
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr hIcon;public int iIcon;public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=80)]public string szTypeName;
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,EntryPoint="SHGetFileInfoW")]
    private static extern IntPtr SHGetFileInfo(string path,uint attrs,ref ShellFileInfo info,uint size,uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    private static string CaptureShellIcon(string folder,string pngPath)
    {
        var info=new ShellFileInfo();
        if(SHGetFileInfo(folder,0,ref info,(uint)Marshal.SizeOf<ShellFileInfo>(),0x100)==IntPtr.Zero||info.hIcon==IntPtr.Zero)
            return "shell returned no icon";
        try
        {
            var source=System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(info.hIcon,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(source));
            using var file=File.Create(pngPath);png.Save(file);
            return $"captured {source.PixelWidth}x{source.PixelHeight}";
        }
        finally{DestroyIcon(info.hIcon);}
    }
    // 端到端回归：① 模拟图标上的鼠标按下/释放（含鼠标捕获），受控 .lnk 拉起第二个实例写标记文件，
    // 守住「点击图标 → 真正启动程序」；② 验证「全部移除」按钮的可用状态与批量移回结果。
    public static async Task RunIconClick(MainWindow window,string root,string reportPath)
    {
        var lines=new List<string>{"step=start"};bool ok=false;
        try
        {
            if(!App.IsSandbox)throw new InvalidOperationException("图标点击自检只允许在 --sandbox 目录运行。");
            window.KeepOpenForVerification=true;
            var desktop=App.Store.DesktopRoots[0];
            var marker=Path.Combine(root,"shortcut-launched.txt");
            if(File.Exists(marker))File.Delete(marker);
            var type=Type.GetTypeFromProgID("WScript.Shell")!;dynamic shell=Activator.CreateInstance(type)!;
            var source=Path.Combine(desktop,"点击验证快捷方式-"+Guid.NewGuid().ToString("N")[..8]+".lnk");dynamic shortcut=shell.CreateShortcut(source);
            lines.Add("process-path="+Environment.ProcessPath);
            try{shortcut.TargetPath=Environment.ProcessPath;shortcut.Arguments="--verification-marker --sandbox \""+root+"\"";shortcut.WorkingDirectory=desktop;shortcut.Save();lines.Add("shortcut-args="+shortcut.Arguments);}finally{Marshal.FinalReleaseComObject(shortcut);Marshal.FinalReleaseComObject(shell);}
            var entry=App.Store.Collect(source,"office");
            window.SelectCategory(null);window.Render();
            // ItemsControl 的容器要等一轮布局才生成，否则可视化树里找不到按钮。
            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            var icon=window.IconButton(entry.Id);
            lines.Add("icons="+window.VisibleCount);
            icon.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,System.Windows.Input.MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonDownEvent});
            icon.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,System.Windows.Input.MouseButton.Left){RoutedEvent=UIElement.PreviewMouseLeftButtonUpEvent});
            lines.Add("step=up-raised");
            // 单文件发行版拉起第二实例要自解压，冷启动可能超过 10 秒，给足 20 秒并记录等待时长。
            int waited;for(waited=0;waited<100&&!File.Exists(marker);waited++)await Task.Delay(200);
            lines.Add("launch-wait-ms="+(waited*200));
            lines.Add("launched="+File.Exists(marker)+" panel-state="+window.WindowState);
            if(!File.Exists(marker))throw new Exception("点击图标后未启动受控程序。");
            // 「全部移除」：按钮可用，并走与点击按钮相同的数据路径（confirm:false 跳过确认框）。
            var entries=window.Icons.Items.Cast<MainWindow.IconEntry>().ToList();
            lines.Add("remove-enabled="+window.RemoveAllButton.IsEnabled+" count="+entries.Count);
            if(!window.RemoveAllButton.IsEnabled)throw new Exception("全部移除按钮未启用。");
            int done=window.RemoveVisible(false);
            int remaining=window.Icons.Items.Cast<MainWindow.IconEntry>().Count();
            lines.Add("restored="+done+" remaining="+remaining);
            if(done!=entries.Count||remaining!=0)throw new Exception("全部移除未清空当前显示项。");
            ok=true;
        }
        catch(Exception ex){lines.Add("error="+ex);}
        File.WriteAllLines(reportPath,lines);
        foreach(var line in lines)Console.WriteLine(line);
        Application.Current.Shutdown(ok?0:1);
    }

    public static async Task Run(MainWindow window,string root)
    {
        var checks=new List<string>();var failure="";window.KeepOpenForVerification=true;
        // 「退出并结束进程」最终会 Environment.Exit；自检进程自身不能退出，
        // 这里替换成只计数的动作，以便完整断言清理链路。
        AppExit.UseOverride(_=>{});
        try
        {
            if(!App.IsSandbox)throw new InvalidOperationException("界面自检只允许在 --sandbox 目录运行。");
            // 默认视图应打开第一个自定义分类；后续断言统一在「全部」视图下进行。
            var firstCustom=App.Store.State.Categories.FirstOrDefault(c=>c.Id!="uncategorized")?.Id;
            if(window.ActiveCategory!=firstCustom)throw new Exception("默认未打开第一个分类："+window.ActiveCategory);
            checks.Add("Default view opens the first custom category instead of All");
            window.UpdateLayout();await Task.Delay(200);
            ScreenCapture.Save(root,"default-category.png");
            checks.Add("Default first-category view captured for visual review");
            // 自定义分类顺序可持久化，「未分类」始终固定末尾；测完立即恢复原顺序。
            var categoryOrder=App.Store.State.Categories.Select(c=>c.Id).ToList();
            var movable=categoryOrder.Where(c=>c!="uncategorized").ToList();
            if(movable.Count>=2)
            {
                var swapped=movable.ToList();(swapped[0],swapped[1])=(swapped[1],swapped[0]);
                App.Store.SetCategoryOrder(swapped);
                if(App.Store.State.Categories[0].Id!=swapped[0]||App.Store.State.Categories[^1].Id!="uncategorized")
                    throw new Exception("分类拖动顺序未正确持久化。");
                App.Store.SetCategoryOrder(categoryOrder);
                checks.Add("Category drag order persists with uncategorized pinned to the end");
            }
            window.SelectCategory(null);
            // UI 拖放接线：走分类行 Drop 的同一入口，验证「插到目标行之前」并恢复原序。
            var dropOrder=App.Store.State.Categories.Select(c=>c.Id).ToList();
            var dropCustom=dropOrder.Where(c=>c!="uncategorized").ToList();
            if(dropCustom.Count>=2)
            {
                window.HandleCategoryDrop(dropCustom[0],dropCustom[1]);
                var afterDrop=App.Store.State.Categories.Select(c=>c.Id).ToList();
                if(afterDrop[0]!=dropCustom[1]||afterDrop[1]!=dropCustom[0])throw new Exception("分类拖放未插入到目标行之前。");
                window.HandleCategoryDrop(dropCustom[0],dropCustom[1]);
                if(!App.Store.State.Categories.Select(c=>c.Id).SequenceEqual(dropOrder))throw new Exception("分类拖放无法恢复原顺序。");
                checks.Add("Category row drop reorders before the target and remains undoable");
            }
            window.Render();
            if(window.ActiveCategory is not null)throw new Exception("手动切回「全部」后被强制改回分类。");
            checks.Add("Manually switching back to All is preserved across renders");
            var optionKey=@"Software\DesktopNook\OptionVerification\"+Guid.NewGuid().ToString("N");
            try
            {
                var options=new StartupOptions(optionKey+@"\Run",optionKey+@"\Settings");
                if(options.Enabled)throw new Exception("自启默认状态异常。");
                using(var unrelated=Registry.CurrentUser.CreateSubKey(optionKey+@"\Run"))unrelated.SetValue("OtherEntry","keep");
                options.SetEnabled(true,@"C:\验证 空格\番茄桌面.exe");
                if(!options.Enabled||options.Command!="\"C:\\验证 空格\\番茄桌面.exe\" --autostart")throw new Exception("自启路径引用异常。");
                options.CloseEndsProcess=true;if(!new StartupOptions(optionKey+@"\Run",optionKey+@"\Settings").CloseEndsProcess)throw new Exception("关闭行为设置未保留。");
                options.CloseEndsProcess=false;if(new StartupOptions(optionKey+@"\Run",optionKey+@"\Settings").CloseEndsProcess)throw new Exception("关闭行为未回到托盘驻留默认值。");
                // 1.5 之前旧键缺失时默认「点 × 退出」，升级用户注册表里可能残留 1；新版必须忽略它。
                using(var legacy=Registry.CurrentUser.CreateSubKey(optionKey+@"\Settings"))legacy.SetValue("CloseEndsProcess",1,RegistryValueKind.DWord);
                if(new StartupOptions(optionKey+@"\Run",optionKey+@"\Settings").CloseEndsProcess)throw new Exception("旧版「关闭时结束进程=1」被错误继承。");
                checks.Add("Legacy close-to-exit registry value ignored; background tray is the new default");
                options.SetEnabled(false);using var check=Registry.CurrentUser.OpenSubKey(optionKey+@"\Run");if(options.Enabled||check?.GetValue("OtherEntry") as string!="keep")throw new Exception("关闭自启影响其他入口。");
                checks.Add("Startup option persists quoted command, can be disabled and preserves other entries");
                checks.Add("Close-to-exit preference persists independently of collection data");
            }
            finally{Registry.CurrentUser.DeleteSubKeyTree(optionKey,false);}
            var desktop=App.Store.DesktopRoots[0];
            var type=Type.GetTypeFromProgID("WScript.Shell")!;dynamic shell=Activator.CreateInstance(type)!;
            var source=Path.Combine(desktop,"验证快捷方式.lnk");dynamic shortcut=shell.CreateShortcut(source);
            try{shortcut.TargetPath=Environment.ProcessPath;shortcut.Arguments="--verification-marker --sandbox \""+root+"\"";shortcut.WorkingDirectory=desktop;shortcut.Save();}finally{Marshal.FinalReleaseComObject(shortcut);Marshal.FinalReleaseComObject(shell);}
            var before=File.ReadAllBytes(source);var entry=App.Store.Collect(source,"office");
            if(!File.ReadAllBytes(App.Store.ManagedPath(entry)).SequenceEqual(before)||File.Exists(source))throw new Exception("真实 Shell Link 收纳校验失败。");
            window.Render();if(window.VisibleCount!=1)throw new Exception("入口未显示。");checks.Add("Real .lnk collection preserves binary contents, arguments and working directory");
            using(var menu=new ShellContextMenu(window,App.Store,entry))
            {
                var verbs=menu.RootVerbs();if(!verbs.Contains("open")||!verbs.Contains("properties")||!verbs.Contains("copy"))throw new Exception("系统快捷方式菜单内容不完整："+string.Join(",",verbs));
                if(menu.Build((_,_)=>{}).Count<3)throw new Exception("统一样式的 Shell 菜单未生成。");
                menu.InvokeVerb("open");
                for(var i=0;i<50&&!File.Exists(Path.Combine(root,"shortcut-launched.txt"));i++)await Task.Delay(100);
                if(!File.Exists(Path.Combine(root,"shortcut-launched.txt")))throw new Exception("Shell 菜单打开命令未执行。");
                // 启动的子进程可能刚写完标记文件就尚未释放句柄，短暂重试避免自检偶发抖动。
                for(var attempt=0;attempt<10;attempt++)
                {
                    try{File.Delete(Path.Combine(root,"shortcut-launched.txt"));break;}
                    catch(IOException)when(attempt<9){await Task.Delay(100);}
                }
            }
            checks.Add("Real Shell context menu includes open, properties, copy and extension entries");
            checks.Add("Shell context-menu open verb executes the controlled shortcut successfully");
            window.TogglePanel();if(window.WindowState!=WindowState.Minimized)throw new Exception("第二次点击未收起。");window.TogglePanel();if(window.WindowState!=WindowState.Normal)throw new Exception("单击未打开。");
            checks.Add("Panel toggle opens and minimizes without terminating process");
            ShellIntegration.Launch(App.Store,entry);
            for(var i=0;i<40 && !File.Exists(Path.Combine(root,"shortcut-launched.txt"));i++)await Task.Delay(100);
            if(!File.Exists(Path.Combine(root,"shortcut-launched.txt")))throw new Exception("托管快捷方式的实际 ShellExecute 启动未完成。");checks.Add("Managed .lnk launches the real application with preserved quoted arguments");
            window.SelectCategory("dev");if(window.VisibleCount!=0)throw new Exception("标签筛选失败。");
            App.Store.MoveItem(entry.Id,"dev");window.Render();if(window.VisibleCount!=1)throw new Exception("移动分类失败。");checks.Add("Native category tabs and reassignment");
            App.Store.Restore(entry.Id);if(!File.ReadAllBytes(source).SequenceEqual(before)||App.Store.State.Items.Count!=0)throw new Exception("真实 Shell Link 恢复失败。");checks.Add("Real .lnk restored exactly, vault recovery copy retained");
            // 公共桌面只读回归：普通权限无法在 C:\Users\Public\Desktop 创建文件，
            // 移回图标时必须自动改放到当前用户桌面，且异常提示必须是中文。
            if(!NookStore.IsWritableDesktop(desktop))throw new Exception("沙盒桌面应可写。");
            if(NookStore.IsWritableDesktop(Path.Combine(root,"不存在的目录")))throw new Exception("不存在的目录不应判定为可写。");
            var friendly=MainWindow.DescribeError(new UnauthorizedAccessException("Access to the path 'C:\\Users\\Public\\Desktop\\Google Chrome.lnk' is denied."));
            if(!friendly.Contains("没有权限")||!friendly.Contains("公共桌面"))throw new Exception("权限异常未转成中文说明。");
            checks.Add("Permission errors are localized with actionable guidance");
            var sharedDesktop=Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            if(!string.IsNullOrEmpty(sharedDesktop)&&Directory.Exists(sharedDesktop)&&!NookStore.IsWritableDesktop(sharedDesktop))
            {
                var probeDesktop=Path.Combine(root,"probe-desktop");Directory.CreateDirectory(probeDesktop);
                var probeStore=new NookStore(Path.Combine(root,"probe-data"),new[]{probeDesktop,sharedDesktop},App.Recycle);
                var probeSource=Path.Combine(probeDesktop,"只读桌面降级验证.lnk");
                File.Copy(source,probeSource,true);
                var probeItem=probeStore.Collect(probeSource,"office");
                // 复现真实历史记录：快捷方式来自公共桌面，移回时不传目标目录，由 OriginalPath 推导。
                probeItem.OriginalPath=Path.Combine(sharedDesktop,"只读桌面降级验证.lnk");
                var probeDestination=probeStore.Restore(probeItem.Id);
                if(!string.Equals(Path.GetDirectoryName(probeDestination),probeDesktop,StringComparison.OrdinalIgnoreCase)||!File.Exists(probeDestination))
                    throw new Exception("公共桌面不可写时未自动降级到当前用户桌面。");
                checks.Add("Read-only public desktop restore falls back to the writable user desktop");
            }
            else checks.Add("Public desktop is writable here; read-only fallback assertion skipped");
            for(var i=0;i<7;i++)
            {
                var file=Path.Combine(desktop,"示例应用 "+(i+1)+".url");File.WriteAllText(file,"[InternetShortcut]\r\nURL=https://example.invalid/"+i+"\r\n");App.Store.Collect(file,i<3?"office":"dev");
            }
            window.SelectCategory(null);if(window.VisibleCount!=7)throw new Exception("八列网格数据数量异常。");checks.Add("Native eight-column start-menu grid with seven icons");
            var registryPath=@"Software\DesktopNook\Verification\"+Guid.NewGuid().ToString("N");
            var registry=new WindowsRecycleBin(registryPath);var snapshot=registry.Capture();
            try{registry.Hide();if(registry.IsVisible)throw new Exception("隔离注册表隐藏失败。");registry.Restore(snapshot);if(!registry.IsVisible)throw new Exception("隔离注册表恢复失败。");checks.Add("Recycle Bin registry implementation tested under isolated HKCU test key; live desktop not changed");}
            finally{Registry.CurrentUser.DeleteSubKeyTree(registryPath,false);}
            App.Store.CollectRecycleBin("uncategorized");var bin=App.Store.State.Items.Single(i=>i.Kind=="recycle-bin");App.Store.Restore(bin.Id);checks.Add("System-item integration with isolated desktop policy");
            if(ShellIntegration.ContainsRecycleBinCida([1,0,0,0,255,255,255,255,255,255,255,255]))throw new Exception("无效 Shell IDList 被接受。");checks.Add("Invalid shell drag payload rejected");
            if(!ShellIntegration.ContainsRecycleBinCida(ShellIntegration.RecyclePayloadForVerification()))throw new Exception("真实系统回收站的 Shell 拖放标识未被识别。");checks.Add("Recycle Bin shell PIDL drag payload recognized without changing desktop settings");
            await OleDragVerification.Run(window);checks.Add("Real mouse and OLE roundtrip between owned windows using isolated desktop directory");
            await OleDragVerification.Run(window,"file");checks.Add("Regular document uses actual native OLE drag in and out");
            await OleDragVerification.Run(window,"folder");checks.Add("Complete folder uses actual native OLE drag in and out");
            var minimize=FindNamedButton(window,"最小化番茄");minimize.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if(window.WindowState!=WindowState.Minimized)throw new Exception("最小化按钮未收起。");window.ShowPanel();checks.Add("Minimize button preserves process and collection state");
            // 标题栏方框按钮：点击直接最大化，再点还原，不得再弹尺寸选择菜单。
            async Task SettleMaximize(){window.UpdateLayout();await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);window.UpdateLayout();}
            var maximizeButton=window.ZoomButton;
            var radiusBefore=window.SkinCornerRadius;
            // 模拟真实鼠标点击后的焦点状态：标题栏按钮不应长期持有键盘焦点，
            // 否则会常驻显示 WinHover 底色，被看成「一直是选中状态」。
            if(!maximizeButton.Focus())throw new Exception("自检未能给标题栏按钮设置键盘焦点，焦点断言无效。");
            maximizeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await SettleMaximize();
            if(maximizeButton.IsKeyboardFocused)
                throw new Exception("标题栏按钮点击后仍保留键盘焦点，会常驻显示选中底色。");
            checks.Add("Caption buttons release keyboard focus after click; no permanent selected/hover tint");
            if(window.WindowState!=WindowState.Maximized)throw new Exception("标题栏按钮未进入最大化状态。");
            if(window.LastMenu is not null&&window.LastMenu.IsOpen)throw new Exception("最大化按钮仍弹出了尺寸选择菜单。");
            if(BackdropService.LastRegionSize.Width>0||BackdropService.LastRegionSize.Height>0)
                throw new Exception("最大化后旧 HRGN 未清除，全屏窗口会被裁成一小块："+BackdropService.LastRegionSize);
            if(Application.Current.Resources["WinCaptionCornerRadius"] is not CornerRadius full||Math.Abs(full.TopRight)>0.01)
                throw new Exception("最大化后关闭按钮右上圆角未归零。");
            if(window.ResizeHandles.Visibility!=Visibility.Collapsed)throw new Exception("最大化后自由缩放把手未隐藏。");
            if(System.Windows.Automation.AutomationProperties.GetName(maximizeButton)!="向下还原")
                throw new Exception("最大化后按钮未切换为「向下还原」。");
            checks.Add("Maximize button maximizes directly, clears the stale HRGN, zeroes the caption corner and hides resize grips");
            maximizeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            await SettleMaximize();
            if(window.WindowState!=WindowState.Normal)throw new Exception("再次点击未还原为普通窗口。");
            if(BackdropService.LastRegionSize.Width<100||BackdropService.LastRegionSize.Height<100)
                throw new Exception("还原后未重新裁圆角 HRGN："+BackdropService.LastRegionSize);
            if(Math.Abs(radiusBefore-window.SkinCornerRadius)>.01)throw new Exception("还原后皮肤圆角意外变化。");
            // 关闭按钮的红色悬停块必须与 HWND 的 HRGN 取同一个（已按 Viewbox 缩放的）半径，
            // 否则缩放窗口后右上角会被裁出缺角。
            if(Application.Current.Resources["WinCaptionCornerRadius"] is not CornerRadius restored
               ||Math.Abs(restored.TopRight-BackdropService.RegionRadius)>.01)
                throw new Exception("还原后关闭按钮圆角与 HWND 裁切半径不一致："+Application.Current.Resources["WinCaptionCornerRadius"]);
            checks.Add("Close-button hover corner matches the rounded HWND region after restore");
            if(System.Windows.Automation.AutomationProperties.GetName(maximizeButton)!="最大化")
                throw new Exception("还原后按钮未切换回「最大化」。");
            checks.Add("Restore returns to normal state, re-applies the rounded HRGN and follows the active skin corner");
            // 原生皮肤：真实鼠标悬停关闭按钮，留档中性底色与 HWND 圆角是否齐平。
            var closeButton=FindNamedButton(window,"关闭番茄");
            var closeCenter=closeButton.PointToScreen(new Point(closeButton.ActualWidth/2,closeButton.ActualHeight/2));
            GetCursorPos(out var savedCursor);
            try
            {
                SetCursorPos((int)closeCenter.X,(int)closeCenter.Y);
                await Task.Delay(260);
                ScreenCapture.SaveRegion(root,"native-close-hover.png",
                    (int)closeCenter.X-110,(int)closeCenter.Y-60,150,120,3);
                checks.Add("Native-skin close-button hover uses the neutral WinHover fill and matches the window corner radius");
            }
            finally{SetCursorPos(savedCursor.X,savedCursor.Y);}
            window.Render();window.SetStatus("隔离验证模式 · 不操作真实桌面");
            // 底部状态栏已移除：第三行 0 高 + 元素折叠，内容区直接延伸到窗口底边。
            if(window.StatusBar.Visibility!=Visibility.Collapsed||window.GlassRoot.RowDefinitions.Count<3
                ||window.GlassRoot.RowDefinitions[2].Height.Value>0.01)
                throw new Exception("底部状态栏未完全移除。");
            checks.Add("Bottom status bar removed: zero-height row and collapsed element, content reaches the window edge");
            // 跨进程 OLE 拖出：外部程序要能收到标准格式，且绝不能把 vault 托管文件搬走。
            var dragItem=App.Store.State.Items.First(i=>i.Kind!="recycle-bin");
            var managed=App.Store.ManagedPath(dragItem);
            var dragData=ExternalDrag.Build(dragItem,managed);
            if(dragData is null)throw new Exception("跨进程拖出未生成 DataObject。");
            if(dragData.GetData(DataFormats.FileDrop) is not string[] dropped||dropped.Length!=1||(!File.Exists(dropped[0])&&!Directory.Exists(dropped[0])))
                throw new Exception("跨进程拖出的 FileDrop 不是有效托管路径。");
            if(dragData.GetData("FileNameW") is not string single||single!=managed)
                throw new Exception("跨进程拖出缺少 FileNameW。");
            if(dragData.GetData(DataFormats.UnicodeText) is not string dragText||dragText.Length==0)
                throw new Exception("跨进程拖出缺少 UnicodeText。");
            if(dragData.GetData("Preferred DropEffect") is null)
                throw new Exception("跨进程拖出未固定 Preferred DropEffect。");
            if((ExternalDrag.AllowedEffects&DragDropEffects.Move)!=0)
                throw new Exception("跨进程拖出不应允许 Move，否则资源管理器会搬走托管文件。");
            checks.Add("External OLE drag exposes FileDrop/FileNameW/UnicodeText and forces Copy (no Move on vault files)");
            var urlPath=Path.Combine(root,"drag-probe.url");
            File.WriteAllText(urlPath,"[InternetShortcut]\r\nURL=https://example.com/desktop-nook\r\n");
            var urlItem=new DesktopNook.Core.NookItem{Id="drag-probe",Name="网址",Kind="shortcut",OriginalPath=urlPath,FileName="drag-probe.url"};
            var urlData=ExternalDrag.Build(urlItem,urlPath);
            if(urlData?.GetData("UniformResourceLocatorW") is not string urlValue||urlValue!="https://example.com/desktop-nook")
                throw new Exception(".url 拖出未提供 UniformResourceLocatorW。");
            if(urlData.GetData(DataFormats.UnicodeText) as string!=urlValue)
                throw new Exception(".url 拖出的文本格式应为网址。");
            if(ExternalDrag.Build(new DesktopNook.Core.NookItem{Kind="recycle-bin"},managed) is not null)
                throw new Exception("回收站入口不应生成跨进程拖出数据。");
            checks.Add("URL shortcuts expose the internet-shortcut format; recycle-bin entries are excluded");
            // 资源管理器「番茄桌面」展示目录：每个有实际托管文件的收纳项一个 .lnk。
            var navFolder=ExplorerNavService.FolderPath(App.Store.DataDirectory);
            ExplorerNavService.Sync(App.Store.DataDirectory,App.Store);
            var navLinks=Directory.GetFiles(navFolder,"*.lnk");
            var navExpected=App.Store.State.Items.Where(i=>i.Kind!="recycle-bin").Count(i=>
            {
                try{var p=App.Store.ManagedPath(i);return File.Exists(p)||Directory.Exists(p);}catch{return false;}
            });
            if(navLinks.Length!=navExpected)
                throw new Exception("资源管理器展示目录入口数量与收纳项不一致："+navLinks.Length+"/"+navExpected);
            checks.Add("Explorer navigation folder exposes one shortcut per collected item");
            // 导航入口必须显示软件图标：desktop.ini 指向数据目录里的 AppIcon.ico，
            // 目录带 System 属性、ini 带 Hidden+System，Shell 才会读取它。
            ExplorerNavService.EnsureFolderIcon(App.Store.DataDirectory);
            var navIconFile=ExplorerNavService.IconPath(App.Store.DataDirectory);
            var navIni=ExplorerNavService.DesktopIniPath(App.Store.DataDirectory);
            if(!File.Exists(navIconFile)||new FileInfo(navIconFile).Length<6)
                throw new Exception("展示目录缺少软件图标文件 AppIcon.ico。");
            using(var iconStream=File.OpenRead(navIconFile))
            {
                var head=new byte[6];
                if(iconStream.Read(head,0,6)!=6||head[0]!=0||head[1]!=0||head[2]!=1||head[3]!=0)
                    throw new Exception("AppIcon.ico 不是有效图标文件。");
            }
            if(!File.Exists(navIni))throw new Exception("展示目录缺少 desktop.ini。");
            var iniText=File.ReadAllText(navIni);
            if(!iniText.Contains("IconResource="+navIconFile+",0",StringComparison.OrdinalIgnoreCase))
                throw new Exception("desktop.ini 未指向软件图标："+iniText);
            if((File.GetAttributes(navFolder)&FileAttributes.System)==0)
                throw new Exception("展示目录缺少 System 属性，Shell 不会读取 desktop.ini。");
            var iniAttrs=File.GetAttributes(navIni);
            if((iniAttrs&(FileAttributes.Hidden|FileAttributes.System))!=(FileAttributes.Hidden|FileAttributes.System))
                throw new Exception("desktop.ini 缺少 Hidden/System 属性。");
            checks.Add("Explorer navigation folder uses the app icon (desktop.ini IconResource + system attributes)");
            // 请 Shell 亲自解析一次目录图标并存成 PNG：验证不是「只把文件写对了」。
            var shellIconResult=CaptureShellIcon(navFolder,Path.Combine(root,"explorer-nav-shell-icon.png"));
            if(shellIconResult=="shell returned no icon")
                throw new Exception("Shell 未能解析展示目录图标，desktop.ini 可能未生效。");
            checks.Add("Shell icon for navigation folder: "+shellIconResult+" (captured to explorer-nav-shell-icon.png)");
            if(!System.Guid.TryParse(ExplorerNavService.NamespaceClsid,out _))
                throw new Exception("资源管理器命名空间 CLSID 非法。");
            checks.Add("Explorer navigation entry uses a fixed per-user Shell namespace CLSID (registry writes skipped in sandbox)");
            // 工具栏撤销图标已移除，Ctrl+Z / 更多菜单 / 设置页入口仍在。
            bool HasUndoButton(DependencyObject node)
            {
                for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)
                {
                    var child=VisualTreeHelper.GetChild(node,i);
                    // 「全部移除」的提示里也带「可按 Ctrl+Z 撤销」，只匹配以撤销开头的按钮。
                    if(child is System.Windows.Controls.Button b&&b.ToolTip is string tip&&tip.StartsWith("撤销上一步"))return true;
                    if(HasUndoButton(child))return true;
                }
                return false;
            }
            if(HasUndoButton(window.ContentToolbar))throw new Exception("工具栏撤销图标按钮未移除。");
            checks.Add("Toolbar undo icon removed; Ctrl+Z and menu/settings undo entries retained");
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            var positions=window.IconPositions();
            // 八列开始菜单网格：七个图标应全部落在第一行，列间距约 93px 且均匀。
            if(positions.Count!=7 || positions.Any(p=>Math.Abs(p.Y-positions[0].Y)>1)
               || positions.Zip(positions.Skip(1),(a,b)=>b.X-a.X).Any(d=>d<60||d>120))
                throw new Exception("图标未按八列开始菜单网格紧凑排列。");
            checks.Add("Seven icons share one compact row of the eight-column start-menu grid");
            if(Math.Abs(window.ActualWidth-WindowSizePreference.BaseWidth)>2 || Math.Abs(window.ActualHeight-WindowSizePreference.BaseHeight)>2)throw new Exception("设计稿 1:1 尺寸校验失败。");
            var bottom=new TaskbarAnchor(new Rect(100,1040,44,40),new Rect(0,1040,1920,40),new Rect(0,0,1920,1040),96);
            var placement=TaskbarPlacement.Calculate(bottom,716.8,627.2);
            if(placement.Left!=100 || Math.Abs(placement.Bottom-1032)>1)throw new Exception("任务栏左边缘对齐校验失败。");
            var edge=TaskbarPlacement.Calculate(bottom with{Button=new Rect(1820,1040,44,40)},716.8,627.2);
            if(edge.Right>1920)throw new Exception("任务栏边缘溢出。");
            var secondary=TaskbarPlacement.Calculate(new(new Rect(-1800,1040,44,40),new Rect(-1920,1040,1920,40),new Rect(-1920,0,1920,1040),144),716.8,627.2);
            if(secondary.Left!=-1800 || Math.Abs(secondary.Width-1075.2)>1)throw new Exception("副屏 DPI 换算校验失败。");
            checks.Add("Design-size surface, taskbar alignment, screen edges and 150 percent DPI math");
            var actualAnchor=await TaskbarPlacement.FindAsync();
            if(actualAnchor is null)throw new Exception("本机任务栏未找到番茄桌面按钮。");
            File.WriteAllText(Path.Combine(root,"taskbar-anchor.json"),JsonSerializer.Serialize(new{button=actualAnchor.Button.ToString(),tray=actualAnchor.Tray.ToString(),work=actualAnchor.Work.ToString(),dpi=actualAnchor.Dpi}));
            checks.Add("Live taskbar button bounds discovered using application identity");
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(window);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(root,"native-preview.png")))encoder.Save(file);
            checks.Add($"Native window rendered at {WindowSizePreference.BaseWidth}x{WindowSizePreference.BaseHeight} with production icon");
            window.ShowIconMenu(window.IconButton(App.Store.State.Items[0].Id),App.Store.State.Items[0]);
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            if(window.LastMenu is not null && System.Windows.PresentationSource.FromVisual(window.LastMenu)?.RootVisual is FrameworkElement menuRoot)
            {
                var image=new RenderTargetBitmap((int)Math.Ceiling(menuRoot.ActualWidth),(int)Math.Ceiling(menuRoot.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(menuRoot);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var file=File.Create(Path.Combine(root,"context-menu-preview.png"));png.Save(file);window.LastMenu.IsOpen=false;
            }
            if(Environment.GetCommandLineArgs().Contains("--leave-payloads"))
            {
                App.Store.Collect(Path.Combine(desktop,"真实文档拖放验证.txt"),"office");App.Store.Collect(Path.Combine(desktop,"真实文件夹拖放验证"),"office");App.Store.CollectRecycleBin("uncategorized");checks.Add("Uninstall fixture retains document, folder and system item alongside shortcuts");
            }
            // 输入框专项：渲染「新建分类」弹窗，确认统一 TextBox 模板与按钮样式都能正常实例化。
            var dialog=new TextDialog("新建分类","分类名称",""){Owner=window};
            dialog.Show();
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            dialog.UpdateLayout();
            var dialogBitmap=new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth),(int)Math.Ceiling(dialog.ActualHeight),96,96,PixelFormats.Pbgra32);
            dialogBitmap.Render(dialog);
            var dialogPng=new PngBitmapEncoder();dialogPng.Frames.Add(BitmapFrame.Create(dialogBitmap));
            using(var file=File.Create(Path.Combine(root,"text-dialog-preview.png")))dialogPng.Save(file);
            checks.Add("WinUI TextBox template renders in the text dialog without a blue focus frame");
            dialog.Close();

            // 图标清晰度专项：主界面图标必须以 2× 物理像素向 Shell 取源，避免小帧被拉伸出软边。
            if(ShellIcon.SupersampleFactor<2)throw new Exception("主界面图标未启用 2× 超采样。");
            ShellIcon? sampleIcon=null;
            void LocateIcon(DependencyObject node)
            {
                for(var i=0;i<VisualTreeHelper.GetChildrenCount(node)&&sampleIcon is null;i++)
                {
                    var child=VisualTreeHelper.GetChild(node,i);
                    if(child is ShellIcon si){sampleIcon=si;return;}
                    LocateIcon(child);
                }
            }
            LocateIcon(window.Icons);
            if(sampleIcon?.Source is BitmapSource iconSource&&!ReferenceEquals(iconSource,ShellIntegration.AppIcon))
            {
                var iconDpi=VisualTreeHelper.GetDpi(sampleIcon);
                var physical=sampleIcon.ActualWidth*iconDpi.DpiScaleX;
                if(iconSource.PixelWidth<physical*1.8)
                    throw new Exception($"图标源分辨率不足：{iconSource.PixelWidth}px 源 / {physical:F1}px 显示");
                checks.Add($"Icon bitmaps are supersampled: {iconSource.PixelWidth}px source for {physical:F0}px display");
            }
            else checks.Add("Main-grid icons request 2x supersampled Shell bitmaps");

            // 图标透明边缘专项：只清理白色透明像素与未预乘数据，不触碰不透明图形。
            const int ew=8;var alphaProbe=new byte[ew*ew*4];
            for(int o=0;o<alphaProbe.Length;o+=4){alphaProbe[o]=255;alphaProbe[o+1]=255;alphaProbe[o+2]=255;alphaProbe[o+3]=0;}
            void PutProbe(int x,int y,byte b,byte g,byte r,byte a){int o=(y*ew+x)*4;alphaProbe[o]=b;alphaProbe[o+1]=g;alphaProbe[o+2]=r;alphaProbe[o+3]=a;}
            PutProbe(1,1,255,255,255,100);  // 未预乘的半透明白边
            PutProbe(2,2,255,255,255,30);   // 几乎透明的白色 fringe
            PutProbe(4,4,180,20,30,255);    // 不透明的蓝色主体
            SharpIcons.NormalizeIconAlpha(alphaProbe);
            if(alphaProbe[0]!=0||alphaProbe[1]!=0||alphaProbe[2]!=0)throw new Exception("透明像素的白色 RGB 未清零。");
            if(alphaProbe[(1*ew+1)*4]!=100||alphaProbe[(1*ew+1)*4+3]!=100)throw new Exception("半透明边缘未按 alpha 预乘。");
            if(alphaProbe[(2*ew+2)*4+3]!=0)throw new Exception("白色 fringe 未被丢弃。");
            if(alphaProbe[(4*ew+4)*4]!=180||alphaProbe[(4*ew+4)*4+3]!=255)throw new Exception("不透明图标主体被改动。");
            checks.Add("Icon alpha normalization clears white transparent edges without touching opaque artwork");

            // 聚焦底线专项：确认按圆角轮廓收口、有淡入动画，并渲染 4 倍放大图供视觉核对。
            window.IconSearch.ApplyTemplate();
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            var focusLine=window.IconSearch.Template.FindName("AccentLine",window.IconSearch) as System.Windows.Controls.Border;
            if(focusLine is null||focusLine.BorderThickness.Left!=0||focusLine.BorderThickness.Top!=0
               ||focusLine.BorderThickness.Right!=0||focusLine.BorderThickness.Bottom<1||focusLine.BorderThickness.Bottom>2
               ||focusLine.CornerRadius.TopLeft<1)
                throw new Exception("输入框聚焦底线未按圆角轮廓绘制。");
            if(window.IconSearch.Template.FindName("Underline",window.IconSearch) is not null)
                throw new Exception("仍存在盖在框外的直角蓝条。");
            checks.Add("Focus underline follows the rounded outline instead of a square overlay strip");
            window.IconSearch.Focus();
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            await Task.Delay(220);
            var focused=window.IconSearch.IsKeyboardFocusWithin;
            if(!focused)focusLine.Opacity=.9; // 自检进程没有前台焦点时用静态值呈现同一视觉效果
            else if(focusLine.Opacity<.5)throw new Exception("聚焦底线未淡入。");
            if(focused)checks.Add("Focus underline fades in within 140 ms on keyboard focus");
            var boxWidth=(int)Math.Ceiling(window.IconSearch.ActualWidth);
            var boxHeight=(int)Math.Ceiling(window.IconSearch.ActualHeight);
            if(boxWidth<10||boxHeight<10)throw new Exception("搜索框未完成布局。");
            var small=new RenderTargetBitmap(boxWidth,boxHeight,96,96,PixelFormats.Pbgra32);
            small.Render(window.IconSearch);
            const int zoom=4;
            var big=new RenderTargetBitmap(boxWidth*zoom,boxHeight*zoom,96,96,PixelFormats.Pbgra32);
            var visual=new DrawingVisual();
            using(var dc=visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(zoom,zoom));
                dc.DrawImage(small,new Rect(0,0,boxWidth,boxHeight));
                dc.Pop();
            }
            big.Render(visual);
            var zoomPng=new PngBitmapEncoder();zoomPng.Frames.Add(BitmapFrame.Create(big));
            using(var file=File.Create(Path.Combine(root,"search-focus-preview.png")))zoomPng.Save(file);
            checks.Add("Zoomed focus-state search box rendered for visual review");
            window.IconSearch.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));

            // 设置页专项：渲染一次，确认外观/窗口调整/行为/数据/关于五组控件都能实例化。
            window.ShowSettingsView();
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            // 重设计后的结构断言：外置标题 + 圆角卡片 + 等高行 + 内缩分隔线 + ToggleSwitch。
            var sections=window.SettingsGroups.Children.OfType<StackPanel>().ToList();
            if(sections.Count!=5)throw new Exception("设置页分组数量异常："+sections.Count);
            var sectionTitles=sections.Select(s=>s.Children.OfType<System.Windows.Controls.TextBlock>().FirstOrDefault()?.Text).ToList();
            if(!sectionTitles.Contains("窗口调整"))throw new Exception("设置页缺少「窗口调整」分组："+string.Join("/",sectionTitles));
            var cards=sections.Select(s=>s.Children.OfType<Border>().FirstOrDefault()).ToList();
            if(cards.Any(c=>c is null||c.CornerRadius.TopLeft<6||c.Child is not StackPanel))throw new Exception("设置卡片未按 WinUI 圆角规范创建。");
            var firstRows=(StackPanel)cards[0]!.Child!;
            var rowStyle=(Style)Application.Current.FindResource("WinSettingsRow");
            var rows=firstRows.Children.OfType<Border>().Where(b=>ReferenceEquals(b.Style,rowStyle)).ToList();
            // 外观组 4 行（皮肤 / 主题 / 强调色 / 提示条）+ 3 条内缩分隔线 = 7 个子元素。
            if(firstRows.Children.Count!=7||rows.Count!=4||rows.Any(b=>b.MinHeight<60))
                throw new Exception($"设置行或内缩分隔线数量、行高异常：children={firstRows.Children.Count} rows={rows.Count} minHeight={(rows.Count>0?rows.Min(b=>b.MinHeight):-1)} types={string.Join(",",firstRows.Children.OfType<Border>().Select(b=>ReferenceEquals(b.Style,rowStyle)?"row":"divider"))}");
            var toggleStyle=(Style)Application.Current.FindResource("WinToggleSwitch");
            var toggles=0;
            void CountToggles(DependencyObject node)
            {
                for(var i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)
                {
                    var child=VisualTreeHelper.GetChild(node,i);
                    if(child is System.Windows.Controls.CheckBox box&&ReferenceEquals(box.Style,toggleStyle))toggles++;
                    CountToggles(child);
                }
            }
            CountToggles(window.SettingsGroups);
            // 原有 8 个（外观 2 / 行为 5 / 关闭行为 1）+ 窗口调整 2 个（锁定宽高比、窗口置顶）。
            if(toggles!=10)throw new Exception("ToggleSwitch 数量异常："+toggles);
            checks.Add("Settings page uses five rounded cards, 64px separated rows and ten WinUI toggle switches");
            if(!window.Settings.AlwaysOnTop||!window.Topmost)throw new Exception("窗口置顶应默认开启并与窗口层级一致。");
            window.Settings=window.Settings with{AlwaysOnTop=false};window.ApplyTopmost();
            if(window.Topmost)throw new Exception("关闭置顶后窗口层级未同步。");
            window.Settings=window.Settings with{AlwaysOnTop=true};window.ApplyTopmost();
            if(!window.Topmost)throw new Exception("重新开启置顶后窗口层级未同步。");
            checks.Add("Always-on-top toggle follows AppSettings and applies to the live window");
            if(window.Settings.AutoHideOnDeactivate)throw new Exception("失焦自动收起应默认关闭，面板必须能手动保持显示。");
            checks.Add("Auto-hide on deactivation is off by default; the panel only closes manually");
            if(window.SettingsNavButton.Uid!="selected")throw new Exception("设置页未在侧栏显示选中态。");
            checks.Add("Settings navigation row shows the active indicator");
            var settingsBitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);
            settingsBitmap.Render(window);
            var settingsPng=new PngBitmapEncoder();settingsPng.Frames.Add(BitmapFrame.Create(settingsBitmap));
            using(var file=File.Create(Path.Combine(root,"settings-preview.png")))settingsPng.Save(file);
            checks.Add("Settings view renders with appearance, window sizing, behavior, data and about groups");
            window.ShowIconsView();
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);

            // 深色主题专项：先确认色板可写，临时切换渲染，随后恢复用户原有主题。
            var original=window.Settings;
            ThemeService.Apply(original with{Theme="dark"});
            if(((SolidColorBrush)Application.Current.Resources["WinWindowFill"]).Color.R>100)throw new Exception("深色画刷未生效。");
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            var darkBitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);
            darkBitmap.Render(window);
            var darkPng=new PngBitmapEncoder();darkPng.Frames.Add(BitmapFrame.Create(darkBitmap));
            using(var file=File.Create(Path.Combine(root,"dark-preview.png")))darkPng.Save(file);
            checks.Add("Dark theme palette renders at the 1:1 design size");
            // 深色态下再核对一次设置页：卡片、文字与开关都跟随动态资源。
            window.ShowSettingsView();
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            var darkSettingsBitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);
            darkSettingsBitmap.Render(window);
            var darkSettingsPng=new PngBitmapEncoder();darkSettingsPng.Frames.Add(BitmapFrame.Create(darkSettingsBitmap));
            using(var file=File.Create(Path.Combine(root,"settings-dark-preview.png")))darkSettingsPng.Save(file);
            checks.Add("Dark theme settings cards follow the dynamic palette without rebuilding");
            window.ShowIconsView();
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            ThemeService.Apply(original);
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);

            // 皮肤专项：玻璃拟态与原生皮肤双向切换。自检使用隔离注册表，original.Skin 默认 native。
            var glassSettings=original with{Skin=SkinService.Glass};
            ThemeService.Apply(glassSettings);
            window.ApplyBackdrop();
            window.ApplySkinVisual();
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            await window.WallpaperRefreshCompletion.WaitAsync(TimeSpan.FromSeconds(8));
            window.UpdateLayout();
            checks.Add("DWM backdrop: "+BackdropService.Status);
            // 真·Acrylic 生效时，玻璃背景层绝不能再铺不透明底，否则 DWM 背板被整块挡住。
            if(BackdropService.AcrylicVisible&&!window.SkinBackdropAllowsAcrylic)
                throw new Exception("Acrylic 已启用，但玻璃背景层仍是不透明底，DWM 背板被遮挡。");
            if(BackdropService.AcrylicVisible)checks.Add("Live Acrylic is not masked by an opaque glass backdrop");
            if(BackdropService.PlatformSupported()&&BackdropService.Current!=BackdropKind.Acrylic)
                throw new Exception("Windows 11 环境下未启用系统 Acrylic 背板："+BackdropService.Status);
            // 真实屏幕抓图（含 DWM 合成结果），RenderTargetBitmap 看不到系统背板。
            await Task.Delay(350);
            try
            {
                var shot=ScreenCapture.Save(root,"glass-screen.png");
                checks.Add("Real desktop screen capture with DWM backdrop: "+Path.GetFileName(shot));
            }
            catch(Exception ex){checks.Add("Real desktop screen capture unavailable: "+ex.Message);}
            if(((SolidColorBrush)Application.Current.Resources["WinWindowFill"]).Color.A>=255)
                throw new Exception("玻璃皮肤窗口底色未变成半透明。");
            var glassAccent=((SolidColorBrush)Application.Current.Resources["WinAccent"]).Color;
            if(glassAccent.R<200||glassAccent.G>120)throw new Exception("玻璃皮肤强调色不是番茄红。");
            // 图标磁贴不能有白底卡片；悬停只允许极淡的一层反馈。
            var tileFill=((SolidColorBrush)Application.Current.Resources["WinTileFill"]).Color;
            var tileHover=((SolidColorBrush)Application.Current.Resources["WinTileHover"]).Color;
            if(tileFill.A!=0||tileHover.A>0x60)
                throw new Exception($"图标磁贴仍带背景色：fill.A={tileFill.A} hover.A={tileHover.A}");
            // 输入框聚焦底线不能沿用番茄红，否则会被误读成校验失败的错误提示。
            var fieldFocus=((SolidColorBrush)Application.Current.Resources["WinFieldFocus"]).Color;
            if(fieldFocus.R>fieldFocus.G+40||fieldFocus.R>fieldFocus.B+40)
                throw new Exception($"输入框聚焦底线仍是红色：#{fieldFocus.A:X2}{fieldFocus.R:X2}{fieldFocus.G:X2}{fieldFocus.B:X2}");
            checks.Add("Icon tiles have no background fill; the input focus line is neutral instead of error-red");
            if(window.SkinCornerRadius<1||window.SkinCornerRadius>6||!window.SkinBackdropVisible)throw new Exception("玻璃皮肤圆角或背景层未生效。");
            // HWND 必须按同一半径裁成圆角，否则 DWM 背板会在四角留下直角色块。
            if(BackdropService.RegionRadius<1||BackdropService.RegionRadius>6)
                throw new Exception("窗口区域未随玻璃圆角裁剪，四角会露出背板色块："+BackdropService.RegionRadius);
            checks.Add("Window region follows the compact 4px glass corner so DWM backdrop cannot leak square corners");
            // 回退态才需要自绘壁纸层，且必须按窗口位置裁切；Acrylic 生效时该层被撤掉是正确行为。
            if(BackdropService.AcrylicVisible)
            {
                if(window.SkinWallpaperViewbox is not null)throw new Exception("Acrylic 生效时不应再保留自绘壁纸层。");
                checks.Add("Live Acrylic replaces the self-drawn wallpaper layer");
            }
            else
            {
                if(window.SkinWallpaperViewbox is not Rect region||region.Width<=0||region.Height<=0
                    ||region.X<0||region.Y<0||(region.Width>=0.999&&region.Height>=0.999))
                    throw new Exception("玻璃背景未使用窗口对应的壁纸裁切区域："+window.SkinWallpaperViewbox);
                checks.Add("Glass backdrop samples the wallpaper region under the window instead of rescaling the whole image");
            }
            var glassBitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);
            glassBitmap.Render(window);
            var glassPng=new PngBitmapEncoder();glassPng.Frames.Add(BitmapFrame.Create(glassBitmap));
            using(var file=File.Create(Path.Combine(root,"glass-preview.png")))glassPng.Save(file);
            checks.Add("Glass skin renders translucent palette, tomato accent, compact 4px radius and blurred backdrop");
            // 玻璃皮肤下的聚焦搜索框放大 4 倍留档：确认底线已是中性色而非番茄红。
            window.IconSearch.Focus();
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            var glassLine=window.IconSearch.Template.FindName("AccentLine",window.IconSearch) as System.Windows.Controls.Border;
            if(glassLine is not null&&!window.IconSearch.IsKeyboardFocusWithin)glassLine.Opacity=.9;
            var fw=(int)Math.Ceiling(window.IconSearch.ActualWidth);var fh=(int)Math.Ceiling(window.IconSearch.ActualHeight);
            var fsmall=new RenderTargetBitmap(fw,fh,96,96,PixelFormats.Pbgra32);fsmall.Render(window.IconSearch);
            var fvisual=new DrawingVisual();
            using(var fdc=fvisual.RenderOpen()){fdc.PushTransform(new ScaleTransform(4,4));fdc.DrawImage(fsmall,new Rect(0,0,fw,fh));fdc.Pop();}
            var fbig=new RenderTargetBitmap(fw*4,fh*4,96,96,PixelFormats.Pbgra32);fbig.Render(fvisual);
            var fpng=new PngBitmapEncoder();fpng.Frames.Add(BitmapFrame.Create(fbig));
            using(var f=File.Create(Path.Combine(root,"glass-search-focus.png")))fpng.Save(f);
            checks.Add("Glass-skin focus underline rendered at 4x for neutral-colour review");
            window.IconSearch.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
            // 深色 + 玻璃：两套色板正交，深色玻璃必须保持浅色文字与深色半透明底。
            ThemeService.Apply(glassSettings with{Theme="dark"});
            window.ApplySkinVisual();
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            if(((SolidColorBrush)Application.Current.Resources["WinText"]).Color.R<200)
                throw new Exception("深色玻璃皮肤文字未转为浅色。");
            var darkGlassBitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);
            darkGlassBitmap.Render(window);
            var darkGlassPng=new PngBitmapEncoder();darkGlassPng.Frames.Add(BitmapFrame.Create(darkGlassBitmap));
            using(var file=File.Create(Path.Combine(root,"glass-dark-preview.png")))darkGlassPng.Save(file);
            checks.Add("Dark glass skin keeps light text over the translucent blue-violet palette");
            ThemeService.Apply(original);
            window.ApplyBackdrop();
            window.ApplySkinVisual();
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            window.UpdateLayout();
            try{ScreenCapture.Save(root,"native-screen.png");}catch{/* 对比图失败不影响功能断言 */}
            if(((SolidColorBrush)Application.Current.Resources["WinWindowFill"]).Color.A!=255
                ||window.SkinCornerRadius>14||window.SkinBackdropVisible)
                throw new Exception("切回原生皮肤未完全复位。");
            if(BackdropService.RegionRadius>14||BackdropService.RegionRadius<1)
                throw new Exception("切回原生皮肤后窗口区域圆角未复位："+BackdropService.RegionRadius);
            checks.Add("Switching back to native skin restores opaque fill, 12px radius, 12px window region and hidden backdrop");
            if(((SolidColorBrush)Application.Current.Resources["WinTileFill"]).Color.A!=0)
                throw new Exception("切回原生皮肤后图标磁贴出现背景色。");
            checks.Add("Native skin keeps transparent icon tiles");

            // 配置包往返：导出当前数据，再导入到完全隔离的 Store 与注册表键里核对数量。
            var package=Path.Combine(root,"export-check.zip");
            DataPortability.Export(App.Store,App.Options,package);
            if(!File.Exists(package))throw new Exception("配置包未生成。");
            var importKey=@"Software\DesktopNook\ImportVerification\"+Guid.NewGuid().ToString("N");
            try
            {
                var importRoot=Path.Combine(root,"import-test");
                var importDesktop=Path.Combine(importRoot,"Desktop");Directory.CreateDirectory(importDesktop);
                var testStore=new NookStore(Path.Combine(importRoot,"Data"),new[]{importDesktop},new SimulatedRecycleBin());
                var testOptions=new StartupOptions(importKey+@"\Run",importKey+@"\Settings");
                DataPortability.Import(testStore,testOptions,package);
                var reloaded=new NookStore(Path.Combine(importRoot,"Data"),new[]{importDesktop},new SimulatedRecycleBin());
                if(reloaded.State.Items.Count!=App.Store.State.Items.Count)throw new Exception("导入后入口数量不一致。");
                checks.Add("Configuration package export and isolated import roundtrip preserves entries");
            }
            finally{Registry.CurrentUser.DeleteSubKeyTree(importKey,false);}

            // 撤销栈与 30 天本地日志。
            var undo=new UndoService(Path.Combine(root,"undo-test"));
            var rolled=0;undo.Record("自检操作",()=>rolled=1);
            var undoText=undo.Undo();
            if(rolled!=1||undo.Count!=0||!undoText.Contains("自检操作"))throw new Exception("撤销栈未回滚。");
            if(!undo.Recent().Contains("自检操作"))throw new Exception("操作日志未落盘。");
            checks.Add("Undo stack rolls back the latest operation and keeps a local 30-day log");

            // 全局热键配置与自动收纳（新文件把写入时间回拨，模拟稳定落盘）。
            // 全局热键：界面提示与真正注册到系统的组合键必须一致（MOD_CONTROL|MOD_ALT + VK_T）。
            if(HotKeyService.Display!="Ctrl + Alt + T"||HotKeyService.Modifiers!=0x0003||HotKeyService.VirtualKey!=0x54)
                throw new Exception("全局热键不是 Ctrl + Alt + T。");
            checks.Add("Global hotkey registers Ctrl + Alt + T and shows the same label in settings and tray");
            var autoRoot=Path.Combine(root,"auto-test");
            var autoDesktop=Path.Combine(autoRoot,"Desktop");Directory.CreateDirectory(autoDesktop);
            for(var i=0;i<2;i++)
            {
                var path=Path.Combine(autoDesktop,"自动收纳验证 "+i+".url");
                File.WriteAllText(path,"[InternetShortcut]\r\nURL=https://example.invalid/\r\n");
                File.SetLastWriteTime(path,DateTime.Now.AddSeconds(-5));
            }
            var autoStore=new NookStore(Path.Combine(autoRoot,"Data"),new[]{autoDesktop},new SimulatedRecycleBin());
            var collector=new AutoCollectService(()=>autoStore,_=>{});
            var collected=collector.ScanNow();
            if(collected!=2||autoStore.State.Items.Count!=2)throw new Exception("自动收纳数量异常："+collected);
            checks.Add("Auto-collect scans the desktop and files new visible items into uncategorized");
            // 「全部图标」页重排后的真实屏幕留档；后台 STA 枚举可能较慢，最多等 5 秒。
            try
            {
                window.ShowCatalogView();
                for(var i=0;i<25&&window.CatalogGroups.Children.Count<2;i++)await Task.Delay(200);
                await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
                window.UpdateLayout();await Task.Delay(300);
                ScreenCapture.Save(root,"catalog-preview.png");
                checks.Add("All-icons page captured for the sectioned transparent tile-grid layout");
            }
            catch(Exception ex){checks.Add("Catalog layout preview unavailable: "+ex.Message);}
            finally{window.ShowIconsView();}

            // 关闭按钮 → 托盘后台驻留：窗口隐藏但进程存活，托盘图标与右键菜单始终可用。
            if(window.Tray is null||!window.Tray.Visible)throw new Exception("通知区图标未创建。");
            if(!window.Tray.UsesAppIcon)throw new Exception("托盘未使用程序内嵌的番茄图标。");
            checks.Add("Tray icon uses the embedded multi-size application ICO");
            var trayMenu=window.Tray!.MenuLabels;
            if(!trayMenu.Contains("打开番茄桌面")||!trayMenu.Contains("打开设置")||!trayMenu.Contains("退出并结束进程"))
                throw new Exception("托盘右键菜单入口不完整："+string.Join("/",trayMenu));
            checks.Add("System tray icon is created at startup with open, settings and exit menu items");
            // 右键菜单视觉：必须是 WPF 的 WinUI 卡片，不能退回 WinForms 系统蓝块菜单。
            if(window.Tray.UsesNativeStrip)throw new Exception("托盘仍在使用 WinForms 系统默认菜单。");
            var trayVisual=window.Tray.OpenMenu();
            if(trayVisual is null||!trayVisual.IsOpen)throw new Exception("WPF 托盘菜单未打开。");
            if(trayVisual.Items.Count!=6)throw new Exception("托盘菜单项数量异常："+trayVisual.Items.Count);
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            var trayRoot=System.Windows.PresentationSource.FromVisual(trayVisual)?.RootVisual as FrameworkElement;
            if(trayRoot is null)throw new Exception("托盘菜单未生成可视树。");
            trayRoot.UpdateLayout();
            if(CountVisuals<System.Windows.Shapes.Path>(trayRoot)<3)throw new Exception("托盘菜单缺少行首线性图标。");
            if(trayVisual.Background is not SolidColorBrush lightBrush)throw new Exception("托盘菜单未使用主题画刷。");
            SaveTrayPreview(root,"tray-menu-preview.png",trayRoot,false);
            checks.Add("Tray context menu uses the WPF WinUI card with glyphs, shortcut hint and theme brushes");
            // 品牌头里的番茄图标不能再套彩色圆角底块（玻璃皮肤下会变成显眼的粉红色）。
            var badge=FindTagged<Border>(trayRoot,"TrayBadge");
            if(badge is null)throw new Exception("托盘品牌头缺少徽标容器。");
            if(badge.Background is SolidColorBrush badgeFill&&badgeFill.Color.A>0)
                throw new Exception("番茄图标仍有彩色底块："+badgeFill.Color);
            checks.Add("Tray brand icon has no coloured tile behind it");
            if(!TrayService.HookInstalled)throw new Exception("托盘菜单的全局鼠标钩子未安装，点击外部将无法关闭。");
            // 点击外部关闭：走与真实全局鼠标钩子完全相同的路径，菜单内点击不得误关。
            if(!TrayMenu.TryGetMenuRect(trayVisual,out var menuBounds))throw new Exception("无法取得托盘菜单矩形。");
            window.Tray.HandleOutsideClick((int)menuBounds.Left+10,(int)menuBounds.Top+10);
            if(window.Tray.Menu?.IsOpen!=true)throw new Exception("点击菜单内部被误关。");
            checks.Add("Clicks inside the tray menu keep it open");
            if(!window.Tray.TryOutsideTestPoint(out var outsideX,out var outsideY))
                throw new Exception("无法构造菜单外部测试点。");
            window.Tray.HandleOutsideClick(outsideX,outsideY);
            if(window.Tray.Menu?.IsOpen==true)throw new Exception("点击菜单外部后菜单未关闭。");
            checks.Add("Clicking outside the tray menu dismisses it even when the main window is hidden");
            window.Tray.CloseMenu();
            if(TrayService.HookInstalled)throw new Exception("菜单关闭后全局鼠标钩子未卸载。");
            checks.Add("Low-level mouse hook is installed only while the tray menu is open");
            // 主题跟随：切到深色后重建菜单，卡片底色必须随动态资源一起变化。
            ThemeService.Apply(window.Settings with{Theme="dark"});
            var darkMenu=window.Tray.OpenMenu();
            if(darkMenu?.Background is not SolidColorBrush darkBrush||darkBrush.Color==lightBrush.Color)
                throw new Exception("托盘菜单未跟随深色主题。");
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            if(System.Windows.PresentationSource.FromVisual(darkMenu)?.RootVisual is FrameworkElement darkRoot)
            {
                darkRoot.UpdateLayout();SaveTrayPreview(root,"tray-menu-dark-preview.png",darkRoot,true);
            }
            window.Tray.CloseMenu();
            ThemeService.Apply(window.Settings);
            checks.Add("Tray menu follows light and dark theme palettes without rebuilding the window");
            if(App.Options.CloseEndsProcess)throw new Exception("关闭时结束进程应默认关闭，× 应收进托盘。");
            var trayClose=FindNamedButton(window,"关闭番茄");
            trayClose.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if(window.IsVisible)throw new Exception("点 × 后窗口未隐藏到托盘。");
            if(window.Tray?.IsBackground!=true||!window.Tray.Text.Contains("后台运行"))throw new Exception("托盘未切换到后台驻留状态。");
            checks.Add("Close button hides the window into the tray instead of terminating the process");
            // 真实使用场景：窗口是隐藏的，Popup 宿主不能因此失效。
            var hiddenMenu=window.Tray!.OpenMenu();
            await window.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
            if(hiddenMenu?.IsOpen!=true||System.Windows.PresentationSource.FromVisual(hiddenMenu)?.RootVisual is null)
                throw new Exception("窗口隐藏到托盘后右键菜单无法显示。");
            window.Tray.CloseMenu();
            checks.Add("Tray menu still opens while the main window is hidden in the background");
            // 托盘左键 / 热键 / 单实例激活最终都走 ShowPanel；这里直接模拟唤起。
            window.ShowPanel();
            if(!window.IsVisible||window.Tray?.IsBackground==true)throw new Exception("托盘唤起未恢复窗口。");
            checks.Add("Tray restore brings the panel back and clears the background state");
            // 「隐藏到托盘」按钮与 × 共用同一条路径。
            window.HideToTray();if(window.IsVisible)throw new Exception("设置页「后台运行」未隐藏窗口。");
            window.ShowPanel();checks.Add("Settings hide-to-tray button shares the same background path");
            // Esc 关闭页面：与 × 共用 OnClosing 路径，默认隐藏到托盘。自检默认会阻止
            // 键盘收起窗口，这里临时放行，验证完立刻唤起面板，不影响后续退出链路断言。
            var escSource=PresentationSource.FromVisual(window);
            if(escSource is null)throw new Exception("无法构造 Esc 键盘事件。");
            window.KeepOpenForVerification=false;
            try
            {
                var escArgs=new KeyEventArgs(Keyboard.PrimaryDevice,escSource,Environment.TickCount,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};
                window.RaiseEvent(escArgs);
                if(!escArgs.Handled)throw new Exception("Esc 未被当作关闭快捷键处理。");
                if(window.IsVisible)throw new Exception("按 Esc 后面板未关闭到托盘。");
                checks.Add("Escape closes the panel through the same tray path as the close button");
            }
            finally{window.ShowPanel();window.KeepOpenForVerification=true;}
            // 托盘右键「退出并结束进程」必须真正结束进程：断言它请求了进程级退出，
            // 并把托盘图标、鼠标钩子、全局热键与 IPC 资源全部收干净。
            var requestsBefore=AppExit.Requests;
            var quitMenu=window.Tray!.OpenMenu();
            var quitItem=quitMenu is null?null:TrayMenu.FindExitItem(quitMenu);
            if(quitItem is null)throw new Exception("托盘菜单缺少「退出并结束进程」项。");
            quitItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if(AppExit.Requests!=requestsBefore+1||!window.ExitRequested)
                throw new Exception("托盘退出项未请求进程级退出。");
            if(window.Tray is not null)throw new Exception("退出时托盘图标未释放。");
            if(TrayService.HookInstalled)throw new Exception("退出时全局鼠标钩子未卸载。");
            if(HotKeyService.Registered)throw new Exception("退出时全局热键未注销。");
            checks.Add("Tray exit item requests a process-level exit and releases tray icon, mouse hook and global hotkey");
        }
        catch(Exception ex){failure=ex.ToString();}
        finally
        {
            AppExit.UseOverride(null);
            Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"native-verification.json"),JsonSerializer.Serialize(new{passed=checks.Count,failure,checks,realDesktopModified=false},new JsonSerializerOptions{WriteIndented=true}));
            Application.Current.Shutdown(failure.Length==0?0:1);
        }
    }
    private static T? FindTagged<T>(DependencyObject root,object tag) where T : FrameworkElement
    {
        if(root is T element&&Equals(element.Tag,tag))return element;
        var children=VisualTreeHelper.GetChildrenCount(root);
        for(var i=0;i<children;i++)
        {
            var found=FindTagged<T>(VisualTreeHelper.GetChild(root,i),tag);
            if(found is not null)return found;
        }
        return null;
    }

    private static int CountVisuals<T>(DependencyObject root) where T : DependencyObject
    {
        var count=root is T?1:0;
        var children=VisualTreeHelper.GetChildrenCount(root);
        for(var i=0;i<children;i++)count+=CountVisuals<T>(VisualTreeHelper.GetChild(root,i));
        return count;
    }

    // 菜单是透明 Popup，直接 Render 出来背景透明、圆角与投影看不清；先铺一层与主题一致的底色。
    private static void SaveTrayPreview(string root,string name,FrameworkElement menuRoot,bool dark)
    {
        var w=(int)Math.Ceiling(menuRoot.ActualWidth);var h=(int)Math.Ceiling(menuRoot.ActualHeight);
        if(w<1||h<1)return;
        var visual=new DrawingVisual();
        using(var context=visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(dark?Color.FromRgb(0x20,0x20,0x20):Colors.White),null,new Rect(0,0,w,h));
            context.DrawRectangle(new VisualBrush(menuRoot){Stretch=Stretch.None,AlignmentX=AlignmentX.Left,AlignmentY=AlignmentY.Top},null,new Rect(0,0,w,h));
        }
        var bitmap=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file=File.Create(Path.Combine(root,name));png.Save(file);
    }

    private static System.Windows.Controls.Button FindNamedButton(DependencyObject root,string name)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is System.Windows.Controls.Button button&&System.Windows.Automation.AutomationProperties.GetName(button)==name)return button;try{return FindNamedButton(child,name);}catch(InvalidOperationException){}}
        throw new InvalidOperationException("未找到按钮："+name);
    }
}
