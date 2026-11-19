using System.IO;

using PodsController.Core;
using PodsController.Interop;

namespace PodsController.Services;

/// <summary>托盘菜单构建时用到的当前状态（每次右键都重新问一次）。</summary>
public sealed class TrayMenuState
{
    public bool AutoDetect { get; init; } = true;
    public bool DarkTheme { get; init; }
    public bool Startup { get; init; }
    public bool FollowWinA { get; init; }
    public bool FollowControlCenter { get; init; }
    public string DeviceName { get; init; } = "";

    /// <summary>关闭按钮行为：hide = 收起到托盘，quit = 直接退出（Python 版同名菜单项）。</summary>
    public string CloseAction { get; init; } = "tray";
}

/// <summary>
/// 系统托盘图标 + 右键菜单（对应 Python 版 ui/tray.py 的 TrayController）。
///
/// 桌面框架（WPF / WinUI）都没有 QSystemTrayIcon 这类控件，因此直接用 Shell_NotifyIcon，
/// 回调消息挂在 MessageWindow 上；休眠唤醒后可以 Remount 重挂图标，深浅色切换时换图标配色。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int CmdShow = 100;
    private const int CmdAutoDetect = 101;
    private const int CmdDemoVivo = 110;
    private const int CmdDemoHuawei = 111;
    private const int CmdDemoOppo = 112;
    private const int CmdDark = 120;
    private const int CmdStartup = 121;
    private const int CmdFollowWinA = 122;
    private const int CmdFollowControlCenter = 123;
    private const int CmdCloseHide = 124;
    private const int CmdCloseQuit = 125;
    private const int CmdQuit = 130;

    private readonly MessageWindow _window;
    private IntPtr _icon;
    private bool _added;
    private string _iconFile = "";
    private string _tooltip = AppMeta.AppName;

    public TrayIcon(MessageWindow window)
    {
        _window = window;
        _window.TrayLeftClick += () => ShowRequested?.Invoke();
        _window.TrayRightClick += ShowMenu;
    }

    public Func<TrayMenuState>? StateProvider { get; set; }

    public event Action? ShowRequested;
    public event Action<bool>? AutoDetectToggled;
    public event Action<Vendor>? DemoRequested;
    public event Action? DarkThemeToggled;
    public event Action<bool>? StartupToggled;
    public event Action<bool>? FollowWinAToggled;
    public event Action<bool>? FollowControlCenterToggled;
    /// <summary>关闭按钮行为切换（true = 收起到托盘，false = 直接退出）。</summary>
    public event Action<bool>? CloseActionToggled;
    public event Action? QuitRequested;

    // ------------------------------------------------------------------ //
    public bool Add()
    {
        if (_window.Handle == IntPtr.Zero) return false;

        if (_iconFile.Length == 0) _iconFile = AppPaths.IconPath;
        _icon = LoadIcon(_iconFile);
        var data = BuildData();
        data.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP;
        _added = Native.Shell_NotifyIcon(Native.NIM_ADD, ref data);
        if (!_added)
        {
            AppLog.Runtime("托盘图标添加失败（可能被系统隐藏）");
        }
        return _added;
    }

    public void Remove()
    {
        if (!_added) return;
        var data = BuildData();
        Native.Shell_NotifyIcon(Native.NIM_DELETE, ref data);
        _added = false;
    }

    /// <summary>睡醒 / 解锁后重挂（Windows 会重建通知区域，旧图标会「消失」）。</summary>
    public void Remount()
    {
        Remove();
        Add();
    }

    public void SetTooltip(string text)
    {
        _tooltip = text;
        if (!_added) return;
        var data = BuildData();
        data.uFlags = Native.NIF_TIP;
        Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
    }

    public void ShowBalloon(string title, string text)
    {
        if (!_added) return;
        var data = BuildData();
        data.uFlags = Native.NIF_INFO;
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = Native.NIIF_INFO;
        Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
    }

    // ------------------------------------------------------------------ //
    private void ShowMenu()
    {
        try
        {
            var state = StateProvider?.Invoke() ?? new TrayMenuState();

            var menu = Native.CreatePopupMenu();
            if (menu == IntPtr.Zero) return;

            Append(menu, CmdShow, state.DeviceName.Length > 0 ? $"显示面板（{state.DeviceName}）" : "显示面板");
            Append(menu, CmdAutoDetect, "自动识别已连接的耳机", state.AutoDetect);
            Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, "");

            Append(menu, CmdDemoVivo, "演示模式：vivo TWS");
            Append(menu, CmdDemoHuawei, "演示模式：华为 FreeBuds Pro 5");
            Append(menu, CmdDemoOppo, "演示模式：OPPO Enco X4");
            Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, "");

            Append(menu, CmdDark, "深色模式", state.DarkTheme);
            Append(menu, CmdStartup, "开机自启动", state.Startup);
            Append(menu, CmdFollowWinA, "跟随 Win+A", state.FollowWinA);
            Append(menu, CmdFollowControlCenter, "跟随系统控制中心", state.FollowControlCenter);
            Append(menu, CmdCloseHide, "关闭按钮：最小化到托盘（后台常驻）", state.CloseAction != "exit");
            Append(menu, CmdCloseQuit, "关闭按钮：直接退出程序", state.CloseAction == "exit");
            Native.AppendMenu(menu, Native.MF_SEPARATOR, 0, "");
            Append(menu, CmdQuit, "退出");

            Native.SetForegroundWindow(_window.Handle);

            Native.GetCursorPos(out var point);
            var command = Native.TrackPopupMenu(menu, Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD,
                point.X, point.Y, 0, _window.Handle, IntPtr.Zero);
            Native.DestroyMenu(menu);

            Dispatch((int)command, state);
        }
        catch (Exception ex)
        {
            AppLog.Runtime($"托盘菜单失败：{ex.Message}");
        }
    }

    private static void Append(IntPtr menu, int id, string text, bool? check = null)
    {
        var flags = Native.MF_STRING;
        if (check is true) flags |= Native.MF_CHECKED;
        Native.AppendMenu(menu, flags, (uint)id, text);
    }

    private void Dispatch(int command, TrayMenuState state)
    {
        switch (command)
        {
            case CmdShow: ShowRequested?.Invoke(); break;
            case CmdAutoDetect: AutoDetectToggled?.Invoke(!state.AutoDetect); break;
            case CmdDemoVivo: DemoRequested?.Invoke(Vendor.Vivo); break;
            case CmdDemoHuawei: DemoRequested?.Invoke(Vendor.Huawei); break;
            case CmdDemoOppo: DemoRequested?.Invoke(Vendor.Oppo); break;
            case CmdDark: DarkThemeToggled?.Invoke(); break;
            case CmdStartup: StartupToggled?.Invoke(!state.Startup); break;
            case CmdFollowWinA: FollowWinAToggled?.Invoke(!state.FollowWinA); break;
            case CmdFollowControlCenter: FollowControlCenterToggled?.Invoke(!state.FollowControlCenter); break;
            case CmdCloseHide: CloseActionToggled?.Invoke(true); break;
            case CmdCloseQuit: CloseActionToggled?.Invoke(false); break;
            case CmdQuit: QuitRequested?.Invoke(); break;
        }
    }

    private Native.NotifyIconData BuildData()
    {
        var data = new Native.NotifyIconData
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.NotifyIconData>(),
            hWnd = _window.Handle,
            uID = 1,
            uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP,
            uCallbackMessage = _window.TrayMessage,
            hIcon = _icon,
            szTip = _tooltip,
            szInfo = "",
            szInfoTitle = "",
            guidItem = Guid.Empty,
            hBalloonIcon = IntPtr.Zero
        };
        return data;
    }

    /// <summary>
    /// 跟随深浅色换托盘图标（Python 版任务 C2：主题切换时刷新托盘图标颜色）。
    /// 深色任务栏用亮色耳机图，浅色任务栏用灰色耳机图；文件缺失时回退 app.ico。
    /// </summary>
    public void SetTheme(bool dark)
    {
        var file = Path.Combine(AppPaths.AssetsDir, dark ? "app_dark.ico" : "app_light.ico");
        if (!File.Exists(file)) file = AppPaths.IconPath;
        if (string.Equals(_iconFile, file, StringComparison.OrdinalIgnoreCase)) return;

        _iconFile = file;
        var previous = _icon;
        _icon = LoadIcon(file);

        if (_added && _icon != IntPtr.Zero)
        {
            var data = BuildData();
            data.uFlags = Native.NIF_ICON;
            Native.Shell_NotifyIcon(Native.NIM_MODIFY, ref data);
        }

        if (previous != IntPtr.Zero)
        {
            try { Native.DestroyIcon(previous); } catch { /* 忽略 */ }
        }
    }

    private static IntPtr LoadIcon(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return Native.LoadImage(IntPtr.Zero, path, Native.IMAGE_ICON,
                    32, 32, Native.LR_LOADFROMFILE);
            }
        }
        catch (Exception ex)
        {
            AppLog.Runtime($"托盘图标加载失败（{path}）：{ex.Message}");
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Remove();
        if (_icon != IntPtr.Zero)
        {
            try { Native.DestroyIcon(_icon); } catch { /* 忽略 */ }
            _icon = IntPtr.Zero;
        }
    }
}
