using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using DesktopNook.Core;

namespace DesktopNook.App;

public partial class App : Application
{
    private Mutex? _mutex; private EventWaitHandle? _activate; private EventWaitHandle? _stop; private bool _ownsMutex;
    private RegisteredWaitHandle? _stopWait,_activateWait;
    public static NookStore Store { get; private set; } = null!;
    public static IRecycleBin Recycle { get; private set; } = null!;
    public static bool IsSandbox { get; private set; }
    internal static StartupOptions Options {get;private set;}=new();
    public static void ReloadStore() => Store = new NookStore(Store.DataDirectory,Store.DesktopRoots,Recycle);
    private void OnStartup(object sender, StartupEventArgs e)
    {
        try
        {
            var onlineTest=Array.IndexOf(e.Args,"--verify-online-update");if(onlineTest>=0){Shutdown(Task.Run(()=>OnlineUpdateVerification.Run(e.Args[onlineTest+1],e.Args[onlineTest+2],e.Args.Contains("--live"))).GetAwaiter().GetResult());return;}
            var catalogCheck=Array.IndexOf(e.Args,"--verify-catalog");if(catalogCheck>=0){Shutdown(CatalogVerification.Run(e.Args[catalogCheck+1]));return;}
            var resourceCheck=Array.IndexOf(e.Args,"--resource-self-test");if(resourceCheck>=0){Shutdown(ResourceVerification.Run(e.Args[resourceCheck+1]));return;}
            var contrastCheck=Array.IndexOf(e.Args,"--verify-button-contrast");if(contrastCheck>=0){Shutdown(ButtonContrastVerification.Run(e.Args[contrastCheck+1]));return;}
            var iconCheck=Array.IndexOf(e.Args,"--verify-icons");if(iconCheck>=0){Shutdown(IconVerification.Run(e.Args[iconCheck+1],e.Args.Skip(iconCheck+2)));return;}
            var inventory=Array.IndexOf(e.Args,"--collection-inventory");if(inventory>=0){Shutdown(CollectionRecovery.Run(e.Args[inventory+1],e.Args.Contains("--apply")));return;}
            var updateTest=Array.IndexOf(e.Args,"--verify-update");if(updateTest>=0){Shutdown(UpdateVerification.Run(e.Args[updateTest+1],e.Args[updateTest+2],e.Args[updateTest+3]));return;}
            var applyIndex=Array.IndexOf(e.Args,"--apply-update");if(applyIndex>=0){Shutdown(LocalUpdater.Apply(e.Args[applyIndex+1]));return;}
            if(e.Args.Contains("--verification-marker") && Array.IndexOf(e.Args,"--sandbox") is var markerIndex && markerIndex>=0)
            {
                File.WriteAllText(Path.Combine(Path.GetFullPath(e.Args[markerIndex+1]),"shortcut-launched.txt"),"DesktopNook shortcut launch verified");Shutdown();return;
            }
            ShellIntegration.SetAppId("DesktopNook.DesktopNook");
            var payloadIndex=Array.IndexOf(e.Args,"--verify-payloads");if(payloadIndex>=0){Shutdown(PayloadVerification.Run(Path.GetFullPath(e.Args[payloadIndex+1])));return;}
            var liveIndex=Array.IndexOf(e.Args,"--live-verify");
            if(liveIndex>=0){var result=LiveVerification.Run(Path.GetFullPath(e.Args[liveIndex+1]));Shutdown(result);return;}
            var sandboxIndex = Array.IndexOf(e.Args, "--sandbox");
            IsSandbox = sandboxIndex >= 0;
            var root = IsSandbox ? Path.GetFullPath(e.Args[sandboxIndex + 1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopNook");
            var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..20];
            if(IsSandbox)Options=new StartupOptions(@"Software\DesktopNook\Sandbox\"+signature+@"\Run",@"Software\DesktopNook\Sandbox\"+signature+@"\Settings");
            // 安装器用 ExecWait 无限等待维护进程；Shell COM 在资源管理器繁忙时可能永久阻塞。
            // 这里给每条维护命令一个进程级硬超时，保证安装 / 卸载一定能收尾。
            var maintenance=e.Args.FirstOrDefault(a=>a.StartsWith("--maintenance="));
            if(maintenance is not null)
            {
                var budget=maintenance=="--maintenance=shortcut"?15000:30000;
                var finished=new ManualResetEventSlim(false);
                var watchdog=new Thread(()=>{if(!finished.Wait(budget))Environment.Exit(7);}){IsBackground=true};
                watchdog.Start();
                var code=Maintenance.Run(e.Args,root,signature,IsSandbox);
                finished.Set();
                Shutdown(code);return;
            }
            _mutex = new Mutex(true, "Local\\DesktopNook-" + signature, out _ownsMutex);
            _activate = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\DesktopNook-Activate-" + signature);
            if (!_ownsMutex) { if(!e.Args.Contains("--autostart"))_activate.Set(); Shutdown(); return; }
            var desktops = IsSandbox ? new[] { Path.Combine(root, "Desktop") } : new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) };
            if (IsSandbox) Directory.CreateDirectory(desktops[0]);
            Recycle = IsSandbox ? new SandboxRecycle(root) : new WindowsRecycleBin();
            Store = new NookStore(IsSandbox ? Path.Combine(root, "Data") : root, desktops, Recycle);
            // 背板启动自检固定以玻璃皮肤冷启动：默认皮肤是 native，那样测不到
            // 「Acrylic 声明失败 → 自绘不透明渐变层盖住背板」这条路径。
            if(IsSandbox&&e.Args.Contains("--backdrop-self-test"))
                Options.WriteSettings(Options.ReadSettings() with{Skin=SkinService.Glass});
            var window = new MainWindow(); MainWindow = window;
            window.KeepOpenForVerification=IsSandbox&&e.Args.Contains("--keep-open-verification");
            if(e.Args.Contains("--autostart")){window.ShowActivated=false;window.WindowState=WindowState.Minimized;}
            window.Show();
            File.WriteAllLines(Path.Combine(root,"instance.pid"),[Environment.ProcessId.ToString(),Environment.ProcessPath??""]);
            _stop=new EventWaitHandle(false,EventResetMode.AutoReset,"Local\\DesktopNook-Stop-"+signature);
            _stopWait=ThreadPool.RegisterWaitForSingleObject(_stop,(_,_)=>Dispatcher.BeginInvoke(()=>window.QuitForMaintenance()),null,Timeout.Infinite,false);
            _activateWait=ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => Dispatcher.BeginInvoke(() => window.TogglePanel()), null, Timeout.Infinite, false);
            var readyIndex=Array.IndexOf(e.Args,"--update-ready");if(readyIndex>=0)Dispatcher.BeginInvoke(()=>File.WriteAllText(e.Args[readyIndex+1],System.Text.Json.JsonSerializer.Serialize(new{version=System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!.ToString(),pid=Environment.ProcessId})),System.Windows.Threading.DispatcherPriority.Loaded);
            DispatcherUnhandledException += (_, args) => { args.Handled = true; window.ShowFailure(args.Exception); };
            var catalogUi=Array.IndexOf(e.Args,"--verify-catalog-ui");
            if(catalogUi>=0)Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(async()=>await CatalogVerification.RunUi(window,Path.GetFullPath(e.Args[catalogUi+1]))));
            var iconClick=Array.IndexOf(e.Args,"--verify-icon-click");
            if(iconClick>=0)Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(async()=>await NativeVerification.RunIconClick(window,root,Path.GetFullPath(e.Args[iconClick+1]))));
            if (e.Args.Contains("--ui-self-test")) Dispatcher.BeginInvoke(async () => await NativeVerification.Run(window, root), System.Windows.Threading.DispatcherPriority.Loaded);
            if (e.Args.Contains("--size-self-test")) Dispatcher.BeginInvoke(async () => await SizingVerification.Run(window, root), System.Windows.Threading.DispatcherPriority.Loaded);
            if (e.Args.Contains("--dpi-self-test")) Dispatcher.BeginInvoke(async () => await DpiVerification.Run(window, root), System.Windows.Threading.DispatcherPriority.Loaded);
            if (e.Args.Contains("--backdrop-self-test")) Dispatcher.BeginInvoke(async () => await BackdropVerification.Run(window, root), System.Windows.Threading.DispatcherPriority.Background);
        }
        catch (Exception ex) { MessageBox.Show("番茄桌面暂时无法打开。原文件和数据均保留。\n\n" + ex.Message, "番茄桌面", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        // 注销两个 IPC 等待，避免退出时仍挂在线程池的等待队列上。
        _stopWait?.Unregister(null);_activateWait?.Unregister(null);
        _stop?.Dispose();_activate?.Dispose();
        if(_ownsMutex)_mutex?.ReleaseMutex();_mutex?.Dispose();
        base.OnExit(e);
    }
}

internal sealed class SandboxRecycle(string root) : IRecycleBin
{
    private string PathName => Path.Combine(root, "recycle-hidden.test");
    public bool IsVisible => !File.Exists(PathName);
    public Dictionary<string,int?> Capture() => new() { ["NewStartPanel"] = IsVisible ? 0 : 1, ["ClassicStartMenu"] = null };
    public void Hide() => File.WriteAllText(PathName,"test-only");
    public void Restore(Dictionary<string,int?> snapshot) { if(snapshot["NewStartPanel"] == 1) Hide(); else File.Delete(PathName); }
}
