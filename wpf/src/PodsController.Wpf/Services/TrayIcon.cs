using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;

using Microsoft.Win32;

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
    private ContextMenu? _openMenu;
    private DispatcherTimer? _menuWatch;

    public TrayIcon(MessageWindow window)
    {
        _window = window;
        _window.TrayLeftClick += () => ShowRequested?.Invoke();
        _window.TrayRightClick += ShowMenu;
    }

    public Func<TrayMenuState>? StateProvider { get; set; }

    public bool IsAdded => _added;

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
        if (_window.Handle == IntPtr.Zero)
        {
            AppLog.Runtime("托盘图标添加失败：消息窗口句柄为空");
            return false;
        }

        if (_iconFile.Length == 0) _iconFile = AppPaths.IconPath;
        var previous = _icon;
        _icon = LoadIcon(_iconFile);
        if (_icon == IntPtr.Zero && !string.Equals(_iconFile, AppPaths.IconPath, StringComparison.OrdinalIgnoreCase))
        {
            _iconFile = AppPaths.IconPath;
            _icon = LoadIcon(_iconFile);
        }
        if (previous != IntPtr.Zero && previous != _icon)
        {
            try { Native.DestroyIcon(previous); } catch { /* 忽略 */ }
        }
        var data = BuildData();
        data.uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP;
        _added = Native.Shell_NotifyIcon(Native.NIM_ADD, ref data);
        if (!_added)
        {
            AppLog.Runtime($"托盘图标添加失败（可能被系统隐藏）code={Marshal.GetLastWin32Error()}");
        }
        else
        {
            SchedulePromote();
        }
        return _added;
    }

    /// <summary>
    /// Win11 新托盘图标默认进「隐藏的图标」溢出区，应用没有官方 API 强制常显。
    /// 这里做 best-effort：等 Explorer 建好 NotifyIconSettings 项后，把本 exe 对应项的
    /// IsPromoted 置 1（等同于用户在「其他系统托盘图标」里打开开关）。
    /// 找不到项或系统策略禁止时不强行创建，避免写坏用户的任务栏设置。
    /// </summary>
    private void SchedulePromote()
    {
        Task.Run(async () =>
        {
            foreach (var delay in new[] { 1200, 5000 })
            {
                await Task.Delay(delay).ConfigureAwait(false);
                try
                {
                    var changed = TryPromote();
                    if (changed > 0)
                    {
                        AppLog.Taskbar($"已请求托盘图标常显（匹配 {changed} 项）");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Taskbar($"托盘常显设置失败：{ex.Message}");
                    return;
                }
            }

            AppLog.Taskbar("未找到托盘常显项，Win11 需在「其他系统托盘图标」里手动打开");
        });
    }

    private static int TryPromote()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return 0;
        exe = Path.GetFullPath(exe);

        using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
        if (root is null) return 0;

        var changed = 0;
        foreach (var name in root.GetSubKeyNames())
        {
            try
            {
                using var key = root.OpenSubKey(name, writable: true);
                if (key is null) continue;
                if (key.GetValue("ExecutablePath") is not string path) continue;
                path = path.Trim().Trim('"');
                if (path.Length == 0) continue;
                if (!string.Equals(Path.GetFullPath(path), exe, StringComparison.OrdinalIgnoreCase)) continue;

                var current = key.GetValue("IsPromoted") as int? ?? 0;
                if (current == 1) return Math.Max(changed, 1);
                key.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                changed++;
            }
            catch
            {
                // 单个项不可写就跳过，不影响其它项
            }
        }

        return changed;
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
            var app = Application.Current;
            if (app is null) return;
            app.Dispatcher.BeginInvoke(new Action(() => OpenMenu(state)));
        }
        catch (Exception ex)
        {
            AppLog.Runtime($"托盘菜单失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 托盘右键菜单改用 WPF ContextMenu（复用 App.xaml 里的圆角菜单样式），
    /// 不再使用 Win32 原生菜单，避免复古灰底。
    /// </summary>
    private void OpenMenu(TrayMenuState state)
    {
        try { _openMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false); } catch { /* 忽略 */ }

        var menu = new ContextMenu();

        menu.Items.Add(TrayItem(
            state.DeviceName.Length > 0 ? $"显示面板（{state.DeviceName}）" : "显示面板",
            () => ShowRequested?.Invoke()));

        menu.Items.Add(TrayItem("自动识别已连接的耳机",
            () => AutoDetectToggled?.Invoke(!state.AutoDetect), state.AutoDetect));
        menu.Items.Add(new Separator());

        var demo = new MenuItem { Header = "演示模式" };
        demo.Items.Add(TrayItem("vivo TWS", () => DemoRequested?.Invoke(Vendor.Vivo)));
        demo.Items.Add(TrayItem("华为 FreeBuds Pro 5", () => DemoRequested?.Invoke(Vendor.Huawei)));
        demo.Items.Add(TrayItem("OPPO Enco X4", () => DemoRequested?.Invoke(Vendor.Oppo)));
        menu.Items.Add(demo);
        menu.Items.Add(new Separator());

        menu.Items.Add(TrayItem("深色模式", () => DarkThemeToggled?.Invoke(), state.DarkTheme));
        menu.Items.Add(TrayItem("开机自启动", () => StartupToggled?.Invoke(!state.Startup), state.Startup));
        menu.Items.Add(TrayItem("跟随 Win+A", () => FollowWinAToggled?.Invoke(!state.FollowWinA), state.FollowWinA));
        menu.Items.Add(TrayItem("跟随系统控制中心",
            () => FollowControlCenterToggled?.Invoke(!state.FollowControlCenter), state.FollowControlCenter));

        var closeMenu = new MenuItem { Header = "关闭按钮行为" };
        closeMenu.Items.Add(TrayItem("最小化到托盘（后台常驻）",
            () => CloseActionToggled?.Invoke(true), state.CloseAction != "exit"));
        closeMenu.Items.Add(TrayItem("直接退出程序",
            () => CloseActionToggled?.Invoke(false), state.CloseAction == "exit"));
        menu.Items.Add(closeMenu);
        menu.Items.Add(new Separator());

        menu.Items.Add(TrayItem("退出", () => QuitRequested?.Invoke()));

        try { Native.SetForegroundWindow(_window.Handle); } catch { /* 忽略 */ }
        _openMenu = menu;
        menu.StaysOpen = false;
        menu.Opened += (_, _) =>
        {
            ActivatePopup(menu);
            StartOutsideWatch(menu);
        };
        menu.Closed += (_, _) =>
        {
            StopOutsideWatch();
            if (ReferenceEquals(_openMenu, menu)) _openMenu = null;
        };
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>WPF Popup 从托盘图标打开时经常没有前台激活，先激活它，外部点击才会走系统关闭逻辑。</summary>
    private static void ActivatePopup(ContextMenu menu)
    {
        try
        {
            if (PresentationSource.FromVisual(menu) is HwndSource source && source.Handle != IntPtr.Zero)
            {
                Native.SetForegroundWindow(source.Handle);
            }
        }
        catch
        {
            // 忽略：还有下面的轮询兜底
        }
    }

    /// <summary>
    /// 兜底：托盘右键菜单不是从窗口内打开，部分系统上点别处不会自动关。
    /// 菜单打开期间轻量轮询鼠标键，点到菜单矩形外就关掉；点菜单内不干扰命中。
    /// </summary>
    private void StartOutsideWatch(ContextMenu menu)
    {
        StopOutsideWatch();
        var timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        timer.Tick += (_, _) =>
        {
            if (!menu.IsOpen)
            {
                StopOutsideWatch();
                return;
            }
            if (!AnyMouseDown()) return;
            if (!CursorInsideAny(menu)) menu.IsOpen = false;
        };
        _menuWatch = timer;
        timer.Start();
    }

    private void StopOutsideWatch()
    {
        if (_menuWatch is null) return;
        try { _menuWatch.Stop(); } catch { /* 忽略 */ }
        _menuWatch = null;
    }

    private static bool AnyMouseDown() =>
        (Native.GetAsyncKeyState(0x01) & 0x8000) != 0 ||   // 左键
        (Native.GetAsyncKeyState(0x02) & 0x8000) != 0 ||   // 右键
        (Native.GetAsyncKeyState(0x04) & 0x8000) != 0;     // 中键

    private static bool CursorInsideAny(ContextMenu menu)
    {
        if (CursorInside(menu)) return true;
        // 子菜单是独立 Popup，光标可能已经离开主菜单矩形，不能误关。
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.IsSubmenuOpen && CursorInsidePopup(item)) return true;
        }
        return false;
    }

    private static bool CursorInsidePopup(MenuItem item)
    {
        try
        {
            if (item.Template?.FindName("PART_Popup", item) is not Popup popup) return false;
            if (popup.Child is not FrameworkElement child) return false;
            if (PresentationSource.FromVisual(child) is not HwndSource source || source.Handle == IntPtr.Zero) return false;
            if (!Native.GetCursorPos(out var cursor) || !Native.GetWindowRect(source.Handle, out var rect)) return false;
            return cursor.X >= rect.Left && cursor.X <= rect.Right &&
                   cursor.Y >= rect.Top && cursor.Y <= rect.Bottom;
        }
        catch
        {
            return false;
        }
    }

    private static bool CursorInside(ContextMenu menu)
    {
        if (!Native.GetCursorPos(out var cursor)) return true;
        try
        {
            // 用 Popup 的实际窗口矩形判断，避免 PointToScreen 与 125% DPI 的换算误差。
            if (PresentationSource.FromVisual(menu) is HwndSource source && source.Handle != IntPtr.Zero &&
                Native.GetWindowRect(source.Handle, out var rect))
            {
                return cursor.X >= rect.Left && cursor.X <= rect.Right &&
                       cursor.Y >= rect.Top && cursor.Y <= rect.Bottom;
            }

            var topLeft = menu.PointToScreen(new System.Windows.Point(0, 0));
            var bottomRight = menu.PointToScreen(new System.Windows.Point(menu.ActualWidth, menu.ActualHeight));
            return cursor.X >= topLeft.X && cursor.X <= bottomRight.X &&
                   cursor.Y >= topLeft.Y && cursor.Y <= bottomRight.Y;
        }
        catch
        {
            return true;
        }
    }

    private static MenuItem TrayItem(string header, Action action, bool? check = null)
    {
        var item = new MenuItem { Header = header };
        if (check is not null)
        {
            item.IsCheckable = true;
            item.IsChecked = check.Value;
            item.StaysOpenOnClick = true;
        }
        item.Click += (_, _) => action();
        return item;
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
        var themed = Path.Combine(AppPaths.AssetsDir, dark ? "app_dark.ico" : "app_light.ico");
        // 早期生成的主题图标只有 32×32、700 多字节，任务栏里几乎看不见；
        // 小于 4KB 时直接用 app.ico，保证托盘图标清晰可见。
        var file = (File.Exists(themed) && new FileInfo(themed).Length >= 4096)
            ? themed
            : AppPaths.IconPath;
        if (string.Equals(_iconFile, file, StringComparison.OrdinalIgnoreCase) && _icon != IntPtr.Zero) return;

        var loaded = LoadIcon(file);
        if (loaded == IntPtr.Zero && !string.Equals(file, AppPaths.IconPath, StringComparison.OrdinalIgnoreCase))
        {
            file = AppPaths.IconPath;
            loaded = LoadIcon(file);
        }
        if (loaded == IntPtr.Zero) return;

        _iconFile = file;
        var previous = _icon;
        _icon = loaded;

        if (_added)
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
        StopOutsideWatch();
        try { _openMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false); } catch { /* 忽略 */ }
        Remove();
        if (_icon != IntPtr.Zero)
        {
            try { Native.DestroyIcon(_icon); } catch { /* 忽略 */ }
            _icon = IntPtr.Zero;
        }
    }
}
