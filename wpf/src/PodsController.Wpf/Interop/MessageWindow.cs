using System.Runtime.InteropServices;

using PodsController.Core;

namespace PodsController.Interop;

/// <summary>
/// 隐藏的 Win32 消息窗口，独立线程 + 自己的消息循环。
///
/// 托盘图标、全局热键、会话解锁通知、电源唤醒广播都挂在这个窗口上，
/// 不依赖 WinUI 的消息泵，行为与 Python 版 Qt 的事件循环解耦一致。
/// 只创建不显示（WS_POPUP 且从不 ShowWindow），不会出现在任务栏。
/// </summary>
public sealed class MessageWindow : IDisposable
{
    private static readonly string ClassName = $"VivoPodsWinUI.HiddenWindow.{Environment.ProcessId}";
    private const int TrayIconId = 1;
    private static readonly uint TaskbarCreatedMessage = Native.RegisterWindowMessage("TaskbarCreated");

    private Thread? _thread;
    private Native.WndProcDelegate? _wndProc; // 必须用字段持有，否则会被 GC 回收
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly uint _trayMessage;

    public IntPtr Handle { get; private set; } = IntPtr.Zero;

    public uint TrayMessage => _trayMessage;

    public event Action? TrayLeftDown;
    public event Action? TrayLeftClick;
    public event Action? TrayRightClick;
    public event Action? TaskbarCreated;
    public event Action<int>? HotkeyPressed;
    public event Action<int>? SessionChanged;
    public event Action? PowerResumed;

    public MessageWindow(uint baseMessage = Native.WM_TRAYICON)
    {
        _trayMessage = baseMessage;
    }

    public bool Start()
    {
        if (_thread is not null) return Handle != IntPtr.Zero;

        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "win32-message-window" };
        _thread.Start();
        _ready.Wait(3000);
        return Handle != IntPtr.Zero;
    }

    private void ThreadMain()
    {
        try
        {
            _wndProc = WndProc;
            var hInstance = Native.GetModuleHandle(null);
            var wndClass = new Native.WndClassEx
            {
                cbSize = (uint)Marshal.SizeOf<Native.WndClassEx>(),
                Style = 0,
                WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = hInstance,
                hIcon = IntPtr.Zero,
                hCursor = IntPtr.Zero,
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = ClassName,
                hIconSm = IntPtr.Zero
            };

            var atom = Native.RegisterClassEx(ref wndClass);
            if (atom == 0)
            {
                AppLog.Runtime($"消息窗口注册失败 code={Marshal.GetLastWin32Error()}");
                return;
            }

            Handle = Native.CreateWindowEx(0, ClassName, "VivoPodsWinUI", Native.WS_POPUP,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            if (Handle == IntPtr.Zero)
            {
                AppLog.Runtime($"消息窗口创建失败 code={Marshal.GetLastWin32Error()}");
                return;
            }

            Native.WTSRegisterSessionNotification(Handle, Native.NOTIFY_FOR_THIS_SESSION);
        }
        catch (Exception ex)
        {
            AppLog.Runtime($"消息窗口创建失败：{ex.Message}");
            return;
        }
        finally
        {
            _ready.Set();
        }

        while (true)
        {
            var result = Native.GetMessage(out var msg, IntPtr.Zero, 0, 0);
            if (result == 0 || result == -1) break;
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == TaskbarCreatedMessage)
        {
            TaskbarCreated?.Invoke();
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case Native.WM_TRAYICON:
            {
                var notification = (uint)lParam & 0xFFFF;
                if (notification == Native.WM_LBUTTONDOWN) TrayLeftDown?.Invoke();
                else if (notification == Native.WM_LBUTTONUP) TrayLeftClick?.Invoke();
                else if (notification == Native.WM_RBUTTONUP) TrayRightClick?.Invoke();
                return IntPtr.Zero;
            }

            case Native.WM_WTSSESSION_CHANGE:
                SessionChanged?.Invoke((int)wParam);
                return IntPtr.Zero;

            case Native.WM_HOTKEY:
                HotkeyPressed?.Invoke((int)wParam);
                return IntPtr.Zero;

            case Native.WM_POWERBROADCAST:
                if ((uint)wParam == Native.PBT_APMRESUMEAUTOMATIC) PowerResumed?.Invoke();
                return IntPtr.Zero;

            case Native.WM_CLOSE:
                Native.DestroyWindow(hWnd);
                return IntPtr.Zero;

            case Native.WM_DESTROY:
                Native.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Stop()
    {
        try
        {
            if (Handle != IntPtr.Zero)
            {
                Native.WTSUnRegisterSessionNotification(Handle);
                Native.PostMessage(Handle, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch
        {
            // 忽略
        }
        try { _thread?.Join(1000); } catch { /* 忽略 */ }
        _thread = null;
        Handle = IntPtr.Zero;
    }

    public void Dispose() => Stop();
}
