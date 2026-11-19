using System.IO;

using System.Windows;
using System.Windows.Threading;

using PodsController.Core;
using PodsController.Interop;
using PodsController.Services;
using PodsController.UI;

namespace PodsController;

/// <summary>
/// 应用宿主，逐段对应 Python 版 app.py 的 VivoPodsApp：
/// 单实例 → 托盘 → 浮窗 → 全局热键 → 控制中心跟随 → 睡眠 / 解锁 → 自动识别
/// → 启动策略 → 桌面快捷方式 → 心跳日志。
/// </summary>
public sealed class AppHost
{
    private readonly string[] _argv;

    private SettingsStore _settings = null!;
    private DeviceHub _hub = null!;
    private FlyoutWindow _flyout = null!;
    private MessageWindow _messageWindow = null!;
    private TrayIcon _tray = null!;
    private HotkeyManager _hotkeys = null!;
    private ControlCenterWatcher _ccWatch = null!;
    private AutoConnectManager _auto = null!;
    private DispatcherTimer _heartbeat = null!;

    private Dispatcher? _ui;
    private bool _ccRunning;
    private bool _shuttingDown;
    private bool _autoShowPending;
    private DateTime _lastResume = DateTime.MinValue;
    private List<DeviceInfo> _lastSnapshot = new();

    public AppHost(string[] argv)
    {
        _argv = argv;
    }

    public DeviceHub Hub => _hub;

    // ------------------------------------------------------------------ //
    public void Start()
    {
        _ui = Application.Current.Dispatcher;

        // ---- 常驻保护 ----
        AppPaths.EnsureLocalDir();
        try { if (File.Exists(AppPaths.CleanExitFlag)) File.Delete(AppPaths.CleanExitFlag); } catch { /* 忽略 */ }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Crash($"未处理异常：{e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Crash($"未观察的任务异常：{e.Exception}");
            e.SetObserved();
        };

        AppLog.Runtime($"启动 pid={Environment.ProcessId} version={AppMeta.AppVersion} " +
                       $"argv={string.Join(" ", _argv)}");
        AppLog.Flush();   // 启动日志立即落盘，后续崩溃 / 退出也有据可查

        _settings = new SettingsStore();
        // 默认：点 X 回托盘小图标，不退出程序；只有显式选过「直接退出」才退出。
        if (!_settings.Contains("close_action"))
        {
            _settings.Set("close_action", "tray");
        }
        else
        {
            var closeAction = _settings.Get("close_action", "tray");
            if (!string.Equals(closeAction, "tray", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(closeAction, "exit", StringComparison.OrdinalIgnoreCase))
            {
                _settings.Set("close_action", "tray");
            }
        }
        AppLog.ControlCenterLogEnabled = _settings.GetBool("ccwatch_log", true);

        // ---- 主题（深色 / 浅色）----
        ThemeManager.Apply(_settings.GetBool("dark", true));

        // ---- 隐藏消息窗口（托盘 / 热键 / 会话通知都挂在它上面）----
        _messageWindow = new MessageWindow();
        if (!_messageWindow.Start()) AppLog.Runtime("消息窗口创建失败：托盘与热键将不可用");

        // ---- 设备控制器 / 浮窗 ----
        _hub = new DeviceHub(action => _ui.BeginInvoke(action));
        _flyout = new FlyoutWindow(_hub, _settings)
        {
            DeviceProvider = ListCandidateDevices
        };
        _flyout.ApplyThemeState(_settings.GetBool("dark", true));

        // ---- 托盘 ----
        _tray = new TrayIcon(_messageWindow) { StateProvider = BuildTrayState };
        _tray.Add();
        _tray.SetTheme(ThemeManager.IsDark);
        // Explorer 登录初期可能还没准备好，NIM_ADD 会失败；补两次重挂，确保图标驻留托盘。
        After(2000, EnsureTray);
        After(8000, EnsureTray);

        // ---- 全局热键 ----
        _hotkeys = new HotkeyManager { FollowWinA = _settings.GetBool("follow_win_a", true) };
        _hotkeys.Install(_messageWindow.Handle);
        _hotkeys.WinAPressed += () =>
        {
            if (_hotkeys.FollowWinA) OnUi(() => _flyout.ShowAfterWinA());
        };
        // RegisterHotKey 被占用时，钩子兜底的 Ctrl+Alt+V。
        _hotkeys.TogglePressed += () => OnUi(() => _flyout.ToggleFlyout());

        // ---- 控制中心跟随 ----
        _ccWatch = new ControlCenterWatcher { LogEnabled = _settings.GetBool("ccwatch_log", true) };
        _ccWatch.Opened += () => OnUi(() => _flyout.ShowFlyout("cc"));
        _ccWatch.Closed += () => OnUi(() => _flyout.HideFlyout("cc"));
        _ccRunning = _settings.GetBool("follow_control_center", false);
        if (_ccRunning) _ccWatch.Start();

        // ---- 睡眠 / 锁屏 ----
        _messageWindow.PowerResumed += () => OnUi(() => AfterResume("睡眠唤醒", resetLink: true));
        _messageWindow.SessionChanged += reason =>
        {
            if (reason == Native.WTS_SESSION_UNLOCK) OnUi(() => AfterResume("解锁", resetLink: false));
        };
        _messageWindow.HotkeyPressed += id =>
        {
            if (id == 1) OnUi(() => _flyout.ToggleFlyout());
        };
        _messageWindow.TrayLeftDown += () => OnUi(() => _flyout.NotifyTrayClickDown());
        _messageWindow.TaskbarCreated += () => OnUi(() =>
        {
            AppLog.Taskbar("Explorer 重建任务栏，重挂托盘图标");
            _tray?.Remount();
        });

        // ---- 自动识别 ----
        var demoRequested = _argv.Any(a => a.StartsWith("--demo", StringComparison.OrdinalIgnoreCase));
        _auto = new AutoConnectManager(_hub, _settings) { AutoShow = _settings.GetBool("auto_show", false) };
        _auto.Hint += hint => OnUi(() => _flyout.SetDetectHint(hint));
        _auto.SnapshotChanged += devices => OnUi(() => OnAutoSnapshot(devices));
        _auto.AutoConnected += info => OnUi(() => OnAutoConnected(info));
        if (!demoRequested) _auto.Start();

        // ---- 事件接线 ----
        WireFlyout();
        WireTray();

        _hub.StateChanged += UpdateTrayTip;
        _hub.ConnectedChanged += OnConnectedChanged;

        // ---- 启动策略 ----
        Bootstrap();

        // ---- 心跳 / 快捷方式 / 单实例激活 ----
        _heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        _heartbeat.Tick += (_, _) => AppLog.Heartbeat();
        _heartbeat.Start();
        AppLog.Heartbeat();

        After(1200, () => DesktopShortcut.Ensure(_settings));
        SingleInstance.Listen(() => OnUi(() => _flyout.ShowFlyout("second-instance")));

        Task.Run(() =>
        {
            try { BluetoothEnumerator.ListPaired(); } catch { /* 忽略 */ }
        });

        UpdateTrayTip();
        AppLog.Runtime($"热键状态：{_hotkeys.StatusText()}");
    }

    // ------------------------------------------------------------------ //
    private void Bootstrap()
    {
        var vendor = Vendor.Unknown;
        foreach (var arg in _argv)
        {
            if (arg.Equals("--demo", StringComparison.OrdinalIgnoreCase))
            {
                vendor = Vendor.Vivo;
            }
            else if (arg.StartsWith("--demo=", StringComparison.OrdinalIgnoreCase))
            {
                var value = arg["--demo=".Length..].Trim().ToLowerInvariant();
                vendor = value switch
                {
                    "huawei" or "hw" or "honor" => Vendor.Huawei,
                    "oppo" or "oneplus" or "realme" => Vendor.Oppo,
                    "redmi" or "xiaomi" or "mi" => Vendor.Redmi,
                    _ => Vendor.Vivo
                };
            }
        }

        if (vendor != Vendor.Unknown)
        {
            _hub.ConnectDemo(vendor);
            _flyout.ShowFlyout("demo");
            return;
        }

        After(900, BootstrapFallback);
        // 启动后如果一直驻留托盘，做一次空闲工作集回收；用户已经打开面板则不回收。
        MemoryTrim.Schedule(() => !_flyout.IsVisible, 12000);
    }

    private void BootstrapFallback()
    {
        if (_hub.Info is not null) return;
        if (!_settings.GetBool("auto_connect_last", false))
        {
            AppLog.Runtime("启动自动连接上次设备：未开启，驻留托盘等待选择");
            return;
        }

        var mac = _settings.Get("last_device_mac");
        var name = _settings.Get("last_device_name");
        var vendor = VendorNames.FromKey(_settings.Get("last_device_vendor"));
        if (vendor == Vendor.Unknown && name.Length > 0)
        {
            DeviceCatalog.TryDetect(name, out vendor, out _);
        }

        if (mac.Length > 0 && name.Length > 0 && DeviceCatalog.IsSupported(name))
        {
            AppLog.Runtime($"回退到上次设备：{name}（{mac}）");
            _hub.ConnectDevice(new DeviceInfo(name, mac, vendor != Vendor.Unknown ? vendor : Vendor.Huawei));
        }
        else
        {
            // 默认驻留托盘：首次启动不弹面板，用户点托盘图标或 Ctrl+Alt+V 再打开。
            AppLog.Runtime("没有上次设备，驻留托盘等待选择");
        }
    }

    // ------------------------------------------------------------------ //
    private void OnAutoConnected(DeviceInfo info)
    {
        if (!DeviceCatalog.IsSupported(info.Name))
        {
            AppLog.Runtime($"[auto] 忽略未支持设备：{info.Name}（不弹窗）");
            return;
        }

        AppLog.Runtime($"[auto] 自动接管：{info.Name}（{VendorNames.Label(info.Vendor)}）");
        // 先连上再弹窗：避免第一帧显示“未连接/已断开”的小窗，连上后直接展开成完整窗口。
        _autoShowPending = _auto.AutoShow;
        _hub.ConnectDevice(info);
    }

    /// <summary>托盘 / 齿轮菜单「选择设备」：列出已配对且受支持的耳机。</summary>
    private List<DeviceInfo> ListCandidateDevices()
    {
        try
        {
            var paired = BluetoothEnumerator.ListSupportedPaired();
            if (paired.Count > 0) return paired;
        }
        catch (Exception ex)
        {
            AppLog.Runtime($"枚举已配对设备失败：{ex.Message}");
        }
        return _lastSnapshot;
    }

    private void OnAutoSnapshot(List<DeviceInfo> devices)
    {
        _lastSnapshot = devices;
        if (devices.Count > 0)
        {
            AppLog.Taskbar("[auto] 检测到已连接耳机：" +
                string.Join("、", devices.Select(d => $"{d.Name}({d.Mac})")));
        }
    }

    private void OnConnectedChanged(bool connected)
    {
        var info = _hub.Info;
        if (connected && info is not null && !info.IsDemo)
        {
            _settings.Set("last_device_mac", info.Mac);
            _settings.Set("last_device_name", info.Name);
            _settings.Set("last_device_vendor", VendorNames.ToKey(info.Vendor));
            try
            {
                AppLog.Runtime($"连接 {info.Name}（{info.Mac}）vendor={VendorNames.ToKey(info.Vendor)} " +
                               $"features={_hub.Features}");
            }
            catch
            {
                // 忽略
            }
        }
        _flyout.SetDetectHint("");
        UpdateTrayTip();

        if (connected && _autoShowPending)
        {
            _autoShowPending = false;
            _flyout.ShowFlyout("auto");
        }
        else if (!connected)
        {
            // 设备断开 1 分钟后，如果面板仍隐藏且没有重新连上，再做一次空闲回收。
            // 断开瞬间不回收，避免用户正在看断开提示时触发 GC。
            After(60000, () =>
            {
                if (_shuttingDown || _flyout.IsVisible || _hub.State?.Connected == true) return;
                MemoryTrim.Schedule(null, 0);
            });
        }
    }

    // ------------------------------------------------------------------ //
    private void AfterResume(string reason, bool resetLink)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastResume).TotalSeconds < 8) return;
        _lastResume = now;

        AppLog.Runtime($"{reason}：开始恢复后台挂载");

        try { _tray.Remount(); } catch (Exception ex) { AppLog.Runtime($"{reason}：重挂托盘失败：{ex.Message}"); }
        try { _auto.NotifyResume(); } catch (Exception ex) { AppLog.Runtime($"{reason}：重置自动识别失败：{ex.Message}"); }

        var info = _hub.Info;
        if (info is null || info.IsDemo || !resetLink) return;
        if (!DeviceCatalog.IsSupported(info.Name)) return;

        AppLog.Runtime($"{reason}：重置链路并重连 {info.Name}（{info.Mac}）");
        try { _hub.Disconnect(); } catch { /* 忽略 */ }

        After(1200, () =>
        {
            var current = _hub.Info;
            if (current is not null &&
                !string.Equals(current.PlainMac, info.PlainMac, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _hub.ConnectDevice(info);
        });
    }

    // ------------------------------------------------------------------ //
    private void WireFlyout()
    {
        _flyout.DemoRequested += vendor => ConnectDemo(vendor);
        _flyout.AutoDetectToggled += enabled =>
        {
            _auto.SetEnabled(enabled);
            _settings.Set("auto_detect", enabled);
        };
        _flyout.AutoShowToggled += enabled => _auto.AutoShow = enabled;
        _flyout.DarkThemeToggled += enabled => SetDark(enabled);
        _flyout.StartupToggled += SetStartup;
        _flyout.FollowControlCenterToggled += SetControlCenterFollow;
        _flyout.ReconnectRequested += () =>
        {
            var info = _hub.Info;
            if (info is not null) _hub.ConnectDevice(info);
        };
        _flyout.DeviceSelected += info =>
        {
            _hub.ConnectDevice(info);
            _flyout.HideFlyout("select");
        };
        _flyout.QuitRequested += Shutdown;
    }

    private void WireTray()
    {
        _tray.ShowRequested += () => OnUi(() => _flyout.ToggleFlyoutFromTray());
        _tray.DemoRequested += vendor => OnUi(() => ConnectDemo(vendor));
        _tray.AutoDetectToggled += enabled => OnUi(() =>
        {
            _auto.SetEnabled(enabled);
            _settings.Set("auto_detect", enabled);
            _flyout.SetDetectHint(enabled ? "" : "自动识别已关闭");
        });
        _tray.DarkThemeToggled += () => OnUi(() => SetDark(!_settings.GetBool("dark", true)));
        _tray.StartupToggled += enabled => OnUi(() => SetStartup(enabled));
        _tray.FollowWinAToggled += enabled => OnUi(() =>
        {
            _hotkeys.FollowWinA = enabled;
            _settings.Set("follow_win_a", enabled);
        });
        _tray.FollowControlCenterToggled += enabled => OnUi(() => SetControlCenterFollow(enabled));
        _tray.CloseActionToggled += minimizeToTray => OnUi(() =>
        {
            _settings.Set("close_action", minimizeToTray ? "tray" : "exit");
            _flyout.SyncCloseActionState();
        });
        _tray.QuitRequested += () => OnUi(Shutdown);
    }

    private void SetDark(bool dark)
    {
        _settings.Set("dark", dark);
        ThemeManager.Apply(dark);
        _flyout.ApplyThemeState(dark);
        try { _tray?.SetTheme(dark); } catch (Exception ex) { AppLog.Runtime($"托盘图标换色失败：{ex.Message}"); }
        AppLog.Runtime($"主题切换：{(dark ? "深色" : "浅色")}");
    }

    private void SetStartup(bool enabled)
    {
        if (StartupRegistration.SetEnabled(enabled))
        {
            AppLog.Runtime($"开机自启动：{enabled}");
        }
        else
        {
            AppLog.Runtime($"开机自启动设置未生效：{enabled}");
        }
    }

    private void ConnectDemo(Vendor vendor)
    {
        _hub.ConnectDemo(vendor);
        _flyout.ShowFlyout("demo");
    }

    private void SetControlCenterFollow(bool enabled)
    {
        _ccRunning = enabled;
        _settings.Set("follow_control_center", enabled);
        try
        {
            if (enabled) _ccWatch.Start();
            else _ccWatch.Stop();
        }
        catch (Exception ex)
        {
            AppLog.Runtime($"控制中心跟随切换失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------------ //
    private TrayMenuState BuildTrayState() => new()
    {
        AutoDetect = _auto?.Enabled ?? true,
        DarkTheme = _settings?.GetBool("dark", true) ?? false,
        CloseAction = _settings?.Get("close_action", "tray") ?? "tray",
        Startup = StartupRegistration.IsEnabled(),
        FollowWinA = _hotkeys?.FollowWinA ?? true,
        FollowControlCenter = _ccRunning,
        DeviceName = _hub?.Info?.Name ?? ""
    };

    /// <summary>托盘图标没加成功时重挂一次（Explorer 未就绪 / 被系统刷新）。</summary>
    private void EnsureTray()
    {
        try
        {
            if (_tray is null || _tray.IsAdded) return;
            _tray.Remount();
            if (_tray.IsAdded) AppLog.Taskbar("托盘图标重挂成功");
            else AppLog.Taskbar("托盘图标仍不可用");
        }
        catch (Exception ex)
        {
            AppLog.Runtime($"托盘重挂失败：{ex.Message}");
        }
    }

    private void UpdateTrayTip()
    {
        try
        {
            var state = _hub?.State;
            var info = _hub?.Info;
            var connected = state?.Connected == true;
            // 托盘提示只认「当前真的连上」的设备；断开后不要继续显示上次连接的设备名。
            var name = connected && info is not null
                ? DeviceCatalog.DisplayName(info.Name, info.Vendor) + (info.IsDemo ? "（演示）" : "")
                : AppMeta.AppName;

            var text = !connected
                ? AppMeta.AppName
                : $"{name}\n左 {Percent(state!.LeftPercent)} · 右 {Percent(state.RightPercent)} · 盒 {Percent(state.CasePercent)}";
            _tray?.SetTooltip(text);
        }
        catch
        {
            // 忽略
        }
    }

    private static string Percent(int value) => value >= 0 ? $"{value}%" : "—";

    // ------------------------------------------------------------------ //
    public void Shutdown()
    {
        // 退出必须回到 UI 线程执行，避免托盘 / 消息窗口线程直接 Shutdown 造成半退出状态。
        if (_ui is not null && !_ui.CheckAccess())
        {
            _ui.BeginInvoke(new Action(Shutdown));
            return;
        }

        if (_shuttingDown) return;
        _shuttingDown = true;

        AppLog.Runtime("用户退出");

        try
        {
            _settings.Flush();
            AppPaths.EnsureLocalDir();
            File.WriteAllText(AppPaths.CleanExitFlag, DateTime.Now.ToString("O"));
        }
        catch
        {
            // 忽略
        }

        try { _heartbeat?.Stop(); } catch { /* 忽略 */ }
        try { _auto?.Stop(); } catch { /* 忽略 */ }
        try { _ccWatch?.Stop(); } catch { /* 忽略 */ }
        try { _hotkeys?.Uninstall(); } catch { /* 忽略 */ }
        try { _hub?.Disconnect(); } catch { /* 忽略 */ }
        try { _tray?.Dispose(); } catch { /* 忽略 */ }
        try { _flyout?.PrepareShutdown(); } catch { /* 忽略 */ }
        try { _messageWindow?.Stop(); } catch { /* 忽略 */ }
        try { SingleInstance.Release(); } catch { /* 忽略 */ }

        AppLog.Flush();

        var app = Application.Current;
        if (app is not null && !app.Dispatcher.HasShutdownStarted) app.Shutdown();

        // 兜底：窗口 / 动画 / 第三方线程偶发拖住 Dispatcher 时，1.5 秒后强制结束进程。
        // 此时配置、日志、托盘图标都已清理完，不会丢数据。
        Task.Run(async () =>
        {
            await Task.Delay(1500).ConfigureAwait(false);
            Environment.Exit(0);
        });
    }

    // ------------------------------------------------------------------ //
    /// <summary>把任意线程的回调投到 UI 线程（消息窗口 / 自动识别线程用）。</summary>
    private void OnUi(Action action)
    {
        var dispatcher = _ui;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            try { action(); } catch (Exception ex) { AppLog.Runtime($"UI 回调失败：{ex.Message}"); }
            return;
        }

        dispatcher.BeginInvoke(new Action(() =>
        {
            try { action(); } catch (Exception ex) { AppLog.Runtime($"UI 回调失败：{ex.Message}"); }
        }));
    }

    // ------------------------------------------------------------------ //
    private void After(int milliseconds, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { action(); } catch (Exception ex) { AppLog.Runtime($"延时任务失败：{ex.Message}"); }
        };
        timer.Start();
    }
}
