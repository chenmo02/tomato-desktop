using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DesktopNook.App;

// 全局热键：Ctrl + Alt + T 唤起/收起面板。只注册一个系统级组合键，
// 不与 Win 键组合，避免与系统保留快捷键冲突；被其它程序占用时返回 false 由界面提示。
internal static class HotKeyService
{
    private const int HotKeyId = 0x4E4F4F;
    private const uint WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001, ModControl = 0x0002;
    private const uint VkT = 0x54;
    public const string Display = "Ctrl + Alt + T";

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    private static IntPtr _hwnd;
    private static HwndSource? _source;
    private static Action? _pressed;

    public static bool Register(Window window, Action pressed)
    {
        Unregister();
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        _source = HwndSource.FromHwnd(hwnd);
        if (_source is null) return false;
        _hwnd = hwnd;
        _pressed = pressed;
        _source.AddHook(Hook);
        if (!RegisterHotKey(hwnd, HotKeyId, ModControl | ModAlt, VkT))
        {
            Unregister();
            return false;
        }
        return true;
    }

    public static void Unregister()
    {
        if (_hwnd != IntPtr.Zero) { UnregisterHotKey(_hwnd, HotKeyId); _hwnd = IntPtr.Zero; }
        if (_source is not null) { _source.RemoveHook(Hook); _source = null; }
        _pressed = null;
    }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt64() == HotKeyId)
        {
            handled = true;
            _pressed?.Invoke();
        }
        return IntPtr.Zero;
    }
}
