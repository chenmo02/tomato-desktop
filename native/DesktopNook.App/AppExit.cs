using System;

namespace DesktopNook.App;

// 「退出并结束进程」必须是进程级硬退出。
// Application.Shutdown() 只是请求 WPF 结束 Dispatcher 循环：托盘常驻时窗口已隐藏、
// 弹出菜单 / 消息钩子 / 前台线程仍可能让进程继续挂着（任务栏看不到窗口，但
// DesktopNook.exe 仍在，甚至继续锁住 bin 目录里的程序集，导致下次编译失败）。
// 所有真正的退出路径统一收口到这里，保证最后一定走到 Environment.Exit。
internal static class AppExit
{
    private static Action<int>? _override;

    // 自检用：计数 + 可替换的退出动作，让 UI 自检能在不结束自身进程的前提下
    // 断言「点退出确实请求了进程级退出」。
    internal static int Requests { get; private set; }
    internal static bool IsOverridden => _override is not null;
    internal static void UseOverride(Action<int>? handler) => _override = handler;

    internal static void Exit(int code = 0)
    {
        Requests++;
        if (_override is not null) { _override(code); return; }
        Environment.Exit(code);
    }
}
