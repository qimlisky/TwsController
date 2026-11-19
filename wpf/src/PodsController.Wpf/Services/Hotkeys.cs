using PodsController.Core;
using PodsController.Interop;

namespace PodsController.Services;

/// <summary>
/// 全局热键（对应 Python 版 ui/hotkey.py）。
///
///   Ctrl + Alt + V  唤出 / 收起浮窗   —— RegisterHotKey（挂在消息窗口上）
///   Win + A         系统控制中心弹出后叠加浮窗 —— 低级键盘钩子（可关闭）
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int ToggleHotkeyId = 1;
    private const uint VkToggle = 0x56; // Ctrl+Alt+V

    private IntPtr _window;
    private IntPtr _hook;
    private Native.KeyboardHookDelegate? _hookProc;
    private bool _registered;

    public bool FollowWinA { get; set; } = true;

    public bool HotkeyRegistered { get; private set; }

    public bool KeyboardHookInstalled => _hook != IntPtr.Zero;

    public string? LastError { get; private set; }

    public event Action? WinAPressed;
    public event Action? TogglePressed;

    /// <summary>在消息窗口上注册 Ctrl+Alt+V。</summary>
    public bool Install(IntPtr window)
    {
        _window = window;
        try
        {
            _registered = Native.RegisterHotKey(window, ToggleHotkeyId,
                Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, VkToggle);
            HotkeyRegistered = _registered;
            if (!_registered)
            {
                LastError = "RegisterHotKey 失败（可能被其它程序占用 Ctrl+Alt+V）";
                AppLog.Runtime($"热键注册失败：{LastError}");
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppLog.Runtime($"热键注册异常：{ex.Message}");
        }

        try
        {
            _hookProc = HookCallback;
            // 低级键盘钩子：回调在安装线程的消息循环里执行（WinUI 的 UI 线程有消息泵）
            _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _hookProc, IntPtr.Zero, 0);
            if (_hook == IntPtr.Zero)
            {
                LastError = "键盘钩子安装失败（Win+A 跟随不可用）";
                AppLog.Runtime(LastError);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppLog.Runtime($"键盘钩子异常：{ex.Message}");
        }

        return HotkeyRegistered;
    }

    public void Uninstall()
    {
        try
        {
            if (_registered && _window != IntPtr.Zero)
            {
                Native.UnregisterHotKey(_window, ToggleHotkeyId);
            }
        }
        catch
        {
            // 忽略
        }
        _registered = false;
        HotkeyRegistered = false;

        try
        {
            if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        }
        catch
        {
            // 忽略
        }
        _hook = IntPtr.Zero;
        _hookProc = null;
    }

    public void Dispose() => Uninstall();

    public string StatusText()
    {
        return $"hotkey_registered={HotkeyRegistered}, " +
               $"keyboard_hook_installed={KeyboardHookInstalled}, " +
               $"follow_win_a={FollowWinA}, last_error={LastError}";
    }

    // ------------------------------------------------------------------ //
    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 &&
                (wParam == (IntPtr)Native.WM_KEYDOWN || wParam == (IntPtr)Native.WM_SYSKEYDOWN))
            {
                var info = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.KbdLlHookStruct>(lParam);

                // RegisterHotKey 被别的程序占用时，用低级键盘钩子兜底 Ctrl+Alt+V。
                if (!_registered && info.VkCode == Native.VK_V &&
                    (Native.GetAsyncKeyState(Native.VK_CONTROL) & 0x8000) != 0 &&
                    (Native.GetAsyncKeyState(Native.VK_MENU) & 0x8000) != 0)
                {
                    TogglePressed?.Invoke();
                    return (IntPtr)1;
                }

                if (FollowWinA &&
                    info.VkCode == Native.VK_A &&
                    ((Native.GetAsyncKeyState(Native.VK_LWIN) & 0x8000) != 0 ||
                     (Native.GetAsyncKeyState(Native.VK_RWIN) & 0x8000) != 0))
                {
                    WinAPressed?.Invoke();
                    return (IntPtr)1; // 吞掉，避免 Win+A 再触发系统动作
                }
            }
        }
        catch
        {
            // 钩子里绝不能抛异常
        }

        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
