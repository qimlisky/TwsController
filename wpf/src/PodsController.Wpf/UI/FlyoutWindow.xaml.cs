using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using PodsController.Core;
using PodsController.Interop;

namespace PodsController.UI;

/// <summary>
/// 浮窗（1:1 对齐 Python 版 ui/flyout.py）：
///   * 372 宽，右下角贴边 EDGE_GAP=8，圆角 10，根边距 8；高度按内容自适应；
///   * 显示：从屏幕下方滑入 + 淡入 300ms OutQuart；隐藏反向滑出后 Hide；
///   * 折叠：高度 200ms OutQuart 收放，底边锚定，紧凑电量 + 紧凑降噪；
///   * 自动弹出 5s 收回；点面板外收回；设置菜单顺序与文案同 Python。
/// </summary>
public partial class FlyoutWindow : Window
{
    private const double PanelWidth = 372;
    private const double PanelHeight = 540;
    private const double CollapsedMinHeight = 180;
    private const double ExpandedMinHeight = 280;
    private const double EdgeGap = 8;
    private const double BottomGap = 16;
    private const int SlideMs = 300;
    private const int CollapseMs = 200;
    private const int ToastMs = 2600;

    // 设备图片只解码一次，之后复用同一张已 Freeze 的位图，避免每次刷新都读盘 / 重新解码。
    private static readonly Dictionary<string, BitmapImage?> ImageCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly DeviceHub _hub;
    private readonly SettingsStore _settings;

    private DispatcherTimer? _outsideTimer;
    private DispatcherTimer? _autoHideTimer;
    private DispatcherTimer? _toastTimer;

    private bool _suppress;
    private bool _pinned;
    private bool _collapsed;
    private bool _adaptivePending;
    private DateTime _trayClickDownAt = DateTime.MinValue;
    private DateTime _lastHiddenAt = DateTime.MinValue;
    private bool _autoShown;
    private bool _autoHideArmed;
    private bool _menuOpen;
    private bool _allowClose;
    private bool _animating;
    private bool _findRinging;
    private bool _findBusy;
    private bool _windowShownOnce;
    private string _detectHint = "";
    private DateTime _shownAt = DateTime.MinValue;
    private int _lastCasePercent = -2;
    private List<EqOption> _renderedEq = new();

    private ContextMenu? _settingsMenu;
    private MenuItem? _miAutoDetect;
    private MenuItem? _miAutoShow;
    private MenuItem? _miAutoHide;
    private MenuItem? _miDark;
    private MenuItem? _miDevices;
    private MenuItem? _miCloseTray;
    private MenuItem? _miCloseExit;
    private MenuItem? _miFollowCC;

    public FlyoutWindow(DeviceHub hub, SettingsStore settings)
    {
        _hub = hub;
        _settings = settings;

        InitializeComponent();

        Width = PanelWidth;
        Height = PanelHeight;

        _pinned = _settings.GetBool("pin", false);
        _collapsed = _settings.GetBool("collapsed", false);
        UpdatePinVisual();

        ApplyStaticTexts();
        BuildSettingsMenu();

        _hub.StateChanged += Refresh;
        _hub.SessionChanged += OnSessionChanged;

        CreateTimers();

        Loaded += (_, _) =>
        {
            if (!_windowShownOnce) PositionWindow();
            Refresh();
        };
        Closing += OnClosingHandler;
        PreviewMouseDown += (_, _) => ResetAutoHide();
        SizeChanged += (_, _) =>
        {
            if (!_animating) AnchorToBottom();
        };

        Refresh();
    }

    // ------------------------------------------------------------------ //
    // 对外接口（AppHost / 托盘 / 热键调用）
    // ------------------------------------------------------------------ //
    public Func<List<DeviceInfo>>? DeviceProvider { get; set; }

    public event Action<Vendor>? DemoRequested;
    public event Action<bool>? AutoDetectToggled;
    public event Action<bool>? AutoShowToggled;
    public event Action<bool>? DarkThemeToggled;
    public event Action<bool>? FollowControlCenterToggled;
    public event Action? QuitRequested;
    public event Action? ReconnectRequested;
    public event Action<DeviceInfo>? DeviceSelected;

    public bool IsPinned => _pinned;

    public void ShowFlyout(string source = "manual")
    {
        // 取消尚未结束的滑出 / 折叠动画，避免旧的 Completed 把窗口又藏起来
        _animating = false;
        BeginAnimation(TopProperty, null);
        BeginAnimation(OpacityProperty, null);

        _autoShown = string.Equals(source, "auto", StringComparison.OrdinalIgnoreCase);
        _autoHideArmed = false;
        _shownAt = DateTime.UtcNow;

        if (IsVisible)
        {
            _animating = true;
            var anchor = WorkArea().Bottom - Height - BottomGap;
            AnimateTop(Top, anchor, SlideMs, FinishShowAnimation);
            BeginAnimation(OpacityProperty, new DoubleAnimation(Opacity, 1, TimeSpan.FromMilliseconds(SlideMs))
            {
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
            });
            Activate();
            if (_autoShown && _hub.State?.Connected != true) StopAutoHide();
            else ResetAutoHide();
            return;
        }

        // 先按当前折叠状态定高度，再算贴边位置，避免滑入终点偏差
        ApplyCompactLayout(_collapsed, animate: false, reposition: false);

        // 从屏幕下方滑入：起始位置在任务栏下沿，透明度 0
        var work = WorkArea();
        Left = work.Right - Width - EdgeGap;
        Top = work.Bottom;
        Opacity = 0;

        _windowShownOnce = true;
        _animating = true;
        Show();
        Activate();
        Topmost = true;

        var targetTop = work.Bottom - Height - BottomGap;
        AnimateTop(Top, targetTop, SlideMs, FinishShowAnimation);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(SlideMs))
        {
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
        });

        if (_autoShown && _settings.GetBool("auto_hide", true) && !_pinned
            && _hub.State?.Connected == true) StartAutoHide();
        else StopAutoHide();

        Refresh();
    }

    public void HideFlyout(string reason = "manual")
    {
        _autoHideArmed = false;
        StopAutoHide();
        if (!IsVisible) return;
        _lastHiddenAt = DateTime.UtcNow;

        _animating = true;
        var work = WorkArea();
        AnimateTop(Top, work.Bottom, SlideMs, () =>
        {
            Hide();
            Opacity = 1;
            _animating = false;
            // 收起后如果 3 秒内没有被重新打开，做一次空闲内存回收。
            MemoryTrim.Schedule(() => !IsVisible);
        });
        BeginAnimation(OpacityProperty, new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(SlideMs))
        {
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
        });

        if (!string.IsNullOrEmpty(reason)) AppLog.Runtime($"浮窗收起（{reason}）");
    }

    public void ToggleFlyout()
    {
        if (IsVisible && Opacity > 0.5) HideFlyout("hotkey");
        else ShowFlyout("manual");
    }

    /// <summary>托盘左键：第一次打开，再次点击关闭；不要走“点外面自动收起”的路径。</summary>
    public void ToggleFlyoutFromTray()
    {
        _trayClickDownAt = DateTime.UtcNow;
        // 同一次托盘点击可能已经先被 outside 检测收起；短窗口内视为“这次是要关闭”，
        // 不要又把它重新打开。
        var recentlyHidden = (DateTime.UtcNow - _lastHiddenAt).TotalMilliseconds < 350;
        if (IsVisible || recentlyHidden)
        {
            HideFlyout("tray");
            return;
        }
        ShowFlyout("tray");
    }

    /// <summary>托盘左键按下时调用，给 outside 检测一个短冷却，避免同一次点击先收后开。</summary>
    public void NotifyTrayClickDown() => _trayClickDownAt = DateTime.UtcNow;

    public void ShowAfterWinA() => ShowFlyout("win+a");

    public void SetDetectHint(string hint)
    {
        _detectHint = hint ?? "";
        DetectText.Text = string.IsNullOrEmpty(_detectHint) ? "正在识别…" : _detectHint;
        if (_hub.State?.Connected != true)
        {
            StatusText.Text = string.IsNullOrEmpty(_detectHint) ? "未连接" : _detectHint;
        }
    }

    /// <summary>Toast（对应 Python flyout.toast，2600ms）。</summary>
    public void Notify(string message)
    {
        ToastText.Text = message ?? "";
        ToastBorder.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        _toastTimer?.Stop();
        if (!string.IsNullOrEmpty(message)) _toastTimer?.Start();
    }

    public void ApplyThemeState(bool dark)
    {
        if (_miDark is not null) _miDark.IsChecked = dark;
        try { AboutIcon.Source = LoadImage(dark ? "app_icon.png" : "app_icon_light.png"); }
        catch { /* 忽略 */ }
    }

    public void SyncCloseActionState()
    {
        var tray = CloseToTray();
        if (_miCloseTray is not null) _miCloseTray.IsChecked = tray;
        if (_miCloseExit is not null) _miCloseExit.IsChecked = !tray;
    }

    // ------------------------------------------------------------------ //
    // 窗口形态
    // ------------------------------------------------------------------ //
    private static Rect WorkArea() => SystemParameters.WorkArea;

    private void PositionWindow()
    {
        try
        {
            var work = WorkArea();
            var left = work.Right - Width - EdgeGap;
            var top = work.Bottom - Height - BottomGap;
            Left = Math.Max(work.Left, left);
            Top = Math.Max(work.Top, top);
        }
        catch (Exception ex)
        {
            AppLog.Runtime($"浮窗定位失败：{ex.Message}");
        }
    }

    /// <summary>以屏幕工作区底边为基准向上定位，保证底部内容始终可见。</summary>
    private void AnchorToBottom()
    {
        try
        {
            if (!IsVisible || double.IsNaN(Top)) return;
            var work = WorkArea();
            Top = Math.Max(work.Top, work.Bottom - Height - BottomGap);
        }
        catch
        {
            // 定位失败时保留当前位置
        }
    }

    private void FinishShowAnimation()
    {
        _animating = false;
        AnchorToBottom();
        ScheduleAdaptiveHeight();
    }

    private void AnimateTop(double from, double to, int ms, Action? completed = null)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
        };
        if (completed is not null)
        {
            animation.Completed += (_, _) => completed();
        }
        BeginAnimation(TopProperty, animation);
    }

    private void OnClosingHandler(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        HideFlyout("close");
    }

    /// <summary>真正退出程序时调用：放行关闭，避免 Closing 把退出拦住。</summary>
    public void AllowClose() => _allowClose = true;

    /// <summary>退出前立即停掉所有 UI 定时器 / 动画，不再走 300ms 淡出。</summary>
    public void PrepareShutdown()
    {
        _allowClose = true;
        try { _outsideTimer?.Stop(); } catch { /* 忽略 */ }
        try { _autoHideTimer?.Stop(); } catch { /* 忽略 */ }
        try { _toastTimer?.Stop(); } catch { /* 忽略 */ }
        try
        {
            BeginAnimation(TopProperty, null);
            BeginAnimation(OpacityProperty, null);
            BeginAnimation(HeightProperty, null);
        }
        catch
        {
            // 忽略
        }
        try { Hide(); } catch { /* 忽略 */ }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1) return;
        try { DragMove(); }
        catch { /* 拖动异常忽略 */ }
    }

    private void FoldButton_Click(object sender, RoutedEventArgs e) => SetCollapsed(!_collapsed);

    private void SetCollapsed(bool collapsed)
    {
        _collapsed = collapsed;
        _settings.Set("collapsed", collapsed);
        // 先按真实能力位刷新可见分区，再按实际内容高度收放，避免展开时先撑高再回缩。
        Refresh();
        ApplyCompactLayout(collapsed, animate: true);
    }

    /// <summary>折叠 / 展开的布局与高度动画（200ms OutQuart，底边锚定）。</summary>
    private void ApplyCompactLayout(bool collapsed, bool animate, bool reposition = true)
    {
        BatteryTitle.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        NoiseTitle.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        NoiseSection.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        // 折叠态只保留电量 / 紧凑降噪；其余分区由 Refresh 按连接状态和能力位控制。
        CompactNoiseSeg.Visibility = collapsed && CompactNoiseAvailable() ? Visibility.Visible : Visibility.Collapsed;

        ApplyPillMetrics(collapsed);

        FoldIcon.Text = collapsed ? "\uE70D" : "\uE70E";

        var target = MeasureWindowHeight(collapsed);
        var work = WorkArea();
        var targetTop = work.Bottom - target - BottomGap;

        if (animate && IsVisible)
        {
            _animating = true;
            var topAnim = new DoubleAnimation(Top, targetTop, TimeSpan.FromMilliseconds(CollapseMs))
            {
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
            };
            var heightAnim = new DoubleAnimation(Height, target, TimeSpan.FromMilliseconds(CollapseMs))
            {
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
            };
            heightAnim.Completed += (_, _) =>
            {
                Height = target;
                Top = targetTop;
                _animating = false;
                ScheduleAdaptiveHeight();
            };
            BeginAnimation(TopProperty, topAnim);
            BeginAnimation(HeightProperty, heightAnim);
        }
        else
        {
            Height = target;
            if (reposition) PositionWindow();
        }

        ContentScroll.VerticalScrollBarVisibility = collapsed
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
    }

    private bool CompactNoiseAvailable()
    {
        var state = _hub.State;
        return state?.Connected == true && (state.Features?.Visible(Feature.Noise) ?? false);
    }

    private void ApplyPillMetrics(bool compact)
    {
        var pillHeight = compact ? 48.0 : 66.0;
        LeftPill.Height = pillHeight;
        RightPill.Height = pillHeight;
        CasePill.Height = pillHeight;
        LeftPill.Padding = compact ? new Thickness(8, 4, 8, 4) : new Thickness(8, 6, 8, 6);
        RightPill.Padding = LeftPill.Padding;
        CasePill.Padding = LeftPill.Padding;

        LeftBattery.Width = RightBattery.Width = CaseBattery.Width = compact ? 28 : 34;
        LeftBattery.Height = RightBattery.Height = CaseBattery.Height = compact ? 10 : 12;

        LeftPercentText.FontSize = RightPercentText.FontSize = CasePercentText.FontSize = compact ? 13.5 : 16;
        LeftName.FontSize = RightName.FontSize = CaseName.FontSize = compact ? 10 : 10.5;

        var captionVisibility = compact ? Visibility.Collapsed : Visibility.Visible;
        LeftCaption.Visibility = RightCaption.Visibility = CaseCaption.Visibility = captionVisibility;
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        _settings.Set("pin", _pinned);
        UpdatePinVisual();
    }

    private void UpdatePinVisual()
    {
        PinButton.Opacity = _pinned ? 1.0 : 0.55;
        PinButton.ToolTip = _pinned ? "取消固定" : "固定浮窗";
        PinIcon.SetResourceReference(TextBlock.ForegroundProperty,
            _pinned ? "AccentBrush" : "TextBrush");
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CloseToTray())
        {
            QuitRequested?.Invoke();
            return;
        }
        HideFlyout("close");
    }

    private bool CloseToTray()
    {
        var value = _settings.Get("close_action", "tray");
        return !string.Equals(value, "exit", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(value, "quit", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ //
    // 计时器：点外部收回 / 自动收起 / Toast
    // ------------------------------------------------------------------ //
    private void CreateTimers()
    {
        _outsideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _outsideTimer.Tick += (_, _) => CheckOutside();
        _outsideTimer.Start();

        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5000) };
        _autoHideTimer.Tick += (_, _) =>
        {
            _autoHideTimer?.Stop();
            if (IsVisible && _autoShown && !_pinned) HideFlyout("auto-hide");
        };

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ToastMs) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer?.Stop();
            ToastBorder.Visibility = Visibility.Collapsed;
        };
    }

    private void CheckOutside()
    {
        if (!IsVisible || _pinned || _menuOpen || _animating) return;
        if ((DateTime.UtcNow - _shownAt).TotalMilliseconds < 800) return;
        if ((DateTime.UtcNow - _trayClickDownAt).TotalMilliseconds < 700) return;

        try
        {
            var foreground = Native.GetForegroundWindow();
            if (foreground == IntPtr.Zero) return;

            var hwnd = new WindowInteropHelper(this).Handle;
            if (foreground == hwnd) return;

            Native.GetWindowThreadProcessId(foreground, out var pid);
            if ((int)pid == Environment.ProcessId) return;

            HideFlyout("outside");
        }
        catch
        {
            // 忽略
        }
    }

    private void StartAutoHide()
    {
        _autoHideTimer?.Stop();
        _autoHideTimer?.Start();
    }

    private void StopAutoHide() => _autoHideTimer?.Stop();

    private void ResetAutoHide()
    {
        if (IsVisible && _autoShown && _settings.GetBool("auto_hide", true) && !_pinned) StartAutoHide();
    }

    // ------------------------------------------------------------------ //
    // 界面刷新
    // ------------------------------------------------------------------ //
    private void OnSessionChanged()
    {
        UpdateDeviceImage();
        Refresh();
    }

    private void ApplyStaticTexts()
    {
        AboutVersionText.Text = AppMeta.VersionText;
        AboutAuthorText.Text = AppMeta.AuthorText;
        AboutCreditText.Text = "红米 / 小米协议参考：FallenLeeee / MiBudsController（MIT License）";
        AboutIcon.Source = LoadImage("app_icon.png");
        DisconnectPhoto.Source = LoadImage("pods.png");
        UpdateDeviceImage();
    }

    private void UpdateDeviceImage()
    {
        var name = _hub.Info?.Name ?? "";
        var file = name switch
        {
            var n when n.Contains("FreeClip", StringComparison.OrdinalIgnoreCase) => "freeclip2.png",
            var n when n.Contains("FreeBuds", StringComparison.OrdinalIgnoreCase) => "freebuds5pro.png",
            var n when n.Contains("HUAWEI", StringComparison.OrdinalIgnoreCase) => "freebuds5pro.png",
            var n when n.Contains("Enco", StringComparison.OrdinalIgnoreCase) => "oppo_enco_x4.png",
            var n when n.Contains("OPPO", StringComparison.OrdinalIgnoreCase) => "oppo_enco_x4.png",
            var n when n.Contains("OnePlus", StringComparison.OrdinalIgnoreCase) => "oppo_enco_x4.png",
            var n when n.Contains("realme", StringComparison.OrdinalIgnoreCase) => "oppo_enco_x4.png",
            _ => "pods.png"
        };
        TitleIcon.Source = LoadImage(file);
    }

    private static BitmapImage? LoadImage(string fileName)
    {
        if (ImageCache.TryGetValue(fileName, out var cached)) return cached;

        BitmapImage? bitmap = null;
        try
        {
            var path = Path.Combine(AppPaths.AssetsDir, fileName);
            if (File.Exists(path))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                // 界面里最大只显示到 72px，按 160px 解码，避免把 512px 原图常驻内存。
                image.DecodePixelWidth = 160;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                bitmap = image;
            }
        }
        catch
        {
            bitmap = null;
        }

        ImageCache[fileName] = bitmap;
        return bitmap;
    }

    private void Refresh()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(Refresh));
            return;
        }

        _suppress = true;
        try
        {
            var state = _hub.State;
            var info = _hub.Info;
            var connected = state?.Connected == true;
            var features = state?.Features ?? FeatureSet.Empty;
            var deviceStatus = state?.Status ?? "";
            var waitingForConnection = info is not null && !connected;

            DeviceNameText.Text = info is null
                ? AppMeta.AppName
                : ShortDeviceName(DeviceCatalog.DisplayName(info.Name, info.Vendor)) + (info.IsDemo ? "（演示）" : "");

            var wear = connected ? WearDescription(state!) : "";
            WearText.Text = wear;
            WearText.Visibility = string.IsNullOrEmpty(wear) ? Visibility.Collapsed : Visibility.Visible;
            StatusText.Text = connected
                ? (string.IsNullOrWhiteSpace(deviceStatus) ? "已连接" : deviceStatus)
                : waitingForConnection
                    ? (string.IsNullOrWhiteSpace(deviceStatus) ? "正在连接…" : deviceStatus)
                    : (string.IsNullOrEmpty(_detectHint) ? "未连接" : _detectHint);

            EmptyTitle.Text = waitingForConnection
                ? (string.IsNullOrWhiteSpace(deviceStatus) ? "正在连接…" : deviceStatus)
                : "未连接到耳机";
            ConnectButton.Visibility = waitingForConnection && deviceStatus.Contains("连接中", StringComparison.Ordinal)
                ? Visibility.Collapsed
                : Visibility.Visible;

            EmptySection.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
            ContentScroll.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            FoldButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;

            // 自动弹出的窗口，等真正连上后再重新给满 5 秒，避免连接成功瞬间就收起。
            if (connected && _autoShown && !_pinned && !_autoHideArmed)
            {
                _autoHideArmed = true;
                ResetAutoHide();
            }

            // 电量
            var left = state?.LeftPercent ?? -1;
            var right = state?.RightPercent ?? -1;
            var casePercent = state?.CasePercent ?? -1;

            LeftPercentText.Text = PercentText(left);
            RightPercentText.Text = PercentText(right);
            CasePercentText.Text = PercentText(casePercent);
            LeftBattery.Percent = left;
            RightBattery.Percent = right;
            CaseBattery.Percent = casePercent;
            LeftBolt.Visibility = left >= 0 && state!.LeftCharging ? Visibility.Visible : Visibility.Collapsed;
            RightBolt.Visibility = right >= 0 && state!.RightCharging ? Visibility.Visible : Visibility.Collapsed;
            CaseBolt.Visibility = casePercent >= 0 && state!.CaseCharging ? Visibility.Visible : Visibility.Collapsed;

            if (casePercent >= 0 && casePercent != _lastCasePercent)
            {
                _lastCasePercent = casePercent;
                _settings.Set("case_battery_updated_at", DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            }
            CaseCaption.Text = FormatCaseCaption(_settings.Get("case_battery_updated_at", ""));

            // 出声模式
            var noiseVisible = connected && features.Visible(Feature.Noise);
            NoiseTitle.Visibility = noiseVisible && !_collapsed ? Visibility.Visible : Visibility.Collapsed;
            NoiseSection.Visibility = noiseVisible && !_collapsed ? Visibility.Visible : Visibility.Collapsed;
            CompactNoiseSeg.Visibility = noiseVisible && _collapsed ? Visibility.Visible : Visibility.Collapsed;
            SetNoiseChecks(state?.NoiseMode ?? -1);

            // 空间音频 / 低延迟（折叠态隐藏）
            var spatialVisible = connected && features.Visible(Feature.Spatial) && !_collapsed;
            SpatialRow.Visibility = spatialVisible ? Visibility.Visible : Visibility.Collapsed;
            SpatialSwitch.IsChecked = state?.Spatial == 1;
            SpatialBadge.Visibility = features.IsExperimental(Feature.Spatial)
                ? Visibility.Visible : Visibility.Collapsed;

            var gameVisible = connected && features.Visible(Feature.Game) && !_collapsed;
            GameRow.Visibility = gameVisible ? Visibility.Visible : Visibility.Collapsed;
            GameSwitch.IsChecked = state?.Game == 1;
            GameBadge.Visibility = features.IsExperimental(Feature.Game)
                ? Visibility.Visible : Visibility.Collapsed;

            // 多设备
            var multi = state?.MultiPoint ?? new List<MultiPointRow>();
            var multiVisible = connected && features.Visible(Feature.Dual) && multi.Count > 0 && !_collapsed;
            MultiSection.Visibility = multiVisible ? Visibility.Visible : Visibility.Collapsed;
            if (multiVisible) MultiList.ItemsSource = multi.ToList();

            // 音效
            var eqOptions = state?.EqOptions ?? (IReadOnlyList<EqOption>)Array.Empty<EqOption>();
            var eqVisible = connected && features.Has(Feature.Eq) && eqOptions.Count > 0 && !_collapsed;
            EqTitle.Visibility = eqVisible ? Visibility.Visible : Visibility.Collapsed;
            EqPanel.Visibility = eqVisible ? Visibility.Visible : Visibility.Collapsed;
            if (eqVisible) RebuildEq(eqOptions, state?.EqMode ?? -1);

            if (!connected && _findRinging)
            {
                try { _hub.Find(false); } catch { /* 忽略 */ }
                ResetFindUi();
            }

            FindButton.Visibility = connected && features.Visible(Feature.Find) && !_collapsed
                ? Visibility.Visible : Visibility.Collapsed;

            // 固件
            var firmware = state?.Firmware ?? "";
            FirmwareText.Visibility = connected && firmware.Length > 0 && !_collapsed
                ? Visibility.Visible : Visibility.Collapsed;
            FirmwareText.Text = firmware.Length > 0 ? $"固件 {firmware}" : "";
        }
        finally
        {
            _suppress = false;
            ScheduleAdaptiveHeight();
        }
    }

    /// <summary>
    /// 按当前实际可见内容计算窗口高度：功能少时收紧，功能多时变高；
    /// 超过屏幕可用区域后交给 ContentScroll 滚动。
    /// </summary>
    private double MeasureWindowHeight(bool collapsed)
    {
        try
        {
            var contentWidth = Math.Max(100, Width - 16 - 24 - 4);
            TitleBar.Measure(new Size(contentWidth, double.PositiveInfinity));

            double body;
            if (ContentScroll.Visibility == Visibility.Visible)
            {
                BodyStack.Measure(new Size(contentWidth, double.PositiveInfinity));
                body = BodyStack.DesiredSize.Height;
                // 只有内容真的超出视口、出现滚动时，ExtentHeight 才是完整内容高度；
                // 内容不足一屏时它会等于视口高度，直接取会把窗口撑出大块空白。
                if (ContentScroll.ScrollableHeight > 0)
                    body = Math.Max(body, ContentScroll.ExtentHeight);
            }
            else if (EmptySection.Visibility == Visibility.Visible)
            {
                EmptySection.Measure(new Size(contentWidth, double.PositiveInfinity));
                body = EmptySection.DesiredSize.Height;
            }
            else
            {
                body = 0;
            }

            var firmware = 0.0;
            if (FirmwareText.Visibility == Visibility.Visible)
            {
                FirmwareText.Measure(new Size(contentWidth, double.PositiveInfinity));
                firmware = 6 + FirmwareText.DesiredSize.Height;
            }

            var title = TitleBar.DesiredSize.Height;
            // root margin 16 + border 2 + padding 22 + 标题栏底边距 8 + 固件上边距 6
            var height = 16 + 2 + 10 + title + 8 + body + firmware + 12;
            var min = collapsed ? CollapsedMinHeight : ExpandedMinHeight;
            var max = Math.Max(min, WorkArea().Height - 24);
            return Math.Clamp(height, min, max);
        }
        catch
        {
            return collapsed ? CollapsedMinHeight : ExpandedMinHeight;
        }
    }

    private void ScheduleAdaptiveHeight()
    {
        if (_adaptivePending) return;
        _adaptivePending = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _adaptivePending = false;
            if (_animating) return;
            ResizeToContent(animate: false);
        }), DispatcherPriority.Background);
    }

    private void ResizeToContent(bool animate)
    {
        var target = MeasureWindowHeight(_collapsed);
        if (Math.Abs(Height - target) < 0.5) return;

        var work = WorkArea();
        if (animate && IsVisible)
        {
            _animating = true;
            var topTarget = Math.Max(work.Top, work.Bottom - target - BottomGap);
            var heightAnim = new DoubleAnimation(Height, target, TimeSpan.FromMilliseconds(CollapseMs))
            {
                EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
            };
            heightAnim.Completed += (_, _) =>
            {
                Height = target;
                Top = topTarget;
                _animating = false;
                ScheduleAdaptiveHeight();
            };
            BeginAnimation(HeightProperty, heightAnim);
            AnimateTop(Top, topTarget, CollapseMs);
            return;
        }

        // 保持底边贴边：高度变化时只改 Top，不改变窗口的下沿位置。
        // 以当前下沿为基准向上计算，并限制在屏幕工作区内，避免底部内容被顶出屏幕。
        var bottom = Math.Min(work.Bottom - BottomGap,
            Math.Max(work.Top + target, Top + Height));
        Height = target;
        if (IsVisible && !double.IsNaN(bottom))
        {
            Top = Math.Max(work.Top, bottom - target);
        }
    }

    private static string PercentText(int percent) => percent >= 0 ? $"{percent}%" : "—";

    /// <summary>标题放不下品牌前缀时优先剥掉（对应 Python ElideLabel.short_prefixes）。</summary>
    private static string ShortDeviceName(string name)
    {
        foreach (var prefix in new[] { "HUAWEI ", "OPPO ", "vivo ", "iQOO ", "HONOR ", "OnePlus ", "realme " })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return name[prefix.Length..];
        }
        return name;
    }

    private static string WearDescription(DeviceState state)
    {
        if (state.WearLeft == "worn" && state.WearRight == "worn") return "双耳佩戴中";
        if (state.WearLeft == "worn") return "仅左耳佩戴";
        if (state.WearRight == "worn") return "仅右耳佩戴";
        if (state.WearLeft == "in_case" && state.WearRight == "in_case") return "耳机在充电盒中";
        return "未佩戴";
    }

    private static string FormatCaseCaption(string stamp)
    {
        if (string.IsNullOrWhiteSpace(stamp)) return "";
        try
        {
            var time = DateTime.Parse(stamp);
            return time.Date == DateTime.Today
                ? $"上次更新 {time:HH:mm}"
                : $"上次更新 {time:MM-dd HH:mm}";
        }
        catch
        {
            return "";
        }
    }

    private void SetNoiseChecks(int mode)
    {
        NoiseAncButton.IsChecked = mode == 0;
        NoiseOffButton.IsChecked = mode == 1;
        NoiseTransparencyButton.IsChecked = mode == 2;

        CompactAncButton.IsChecked = mode == 0;
        CompactOffButton.IsChecked = mode == 1;
        CompactTransparencyButton.IsChecked = mode == 2;
    }

    private void RebuildEq(IReadOnlyList<EqOption> options, int current)
    {
        var changed = options.Count != _renderedEq.Count ||
                      options.Where((t, i) => t.Code != _renderedEq[i].Code || t.Name != _renderedEq[i].Name).Any();
        if (changed)
        {
            EqPanel.Children.Clear();
            foreach (var option in options)
            {
                var button = new ToggleButton
                {
                    Content = option.Name,
                    Tag = option.Code,
                    Style = (Style)FindResource("ChipStyle")
                };
                button.Click += (sender, _) =>
                {
                    if (_suppress) return;
                    if (sender is ToggleButton { Tag: int code }) _hub.SetEq(code);
                };
                EqPanel.Children.Add(button);
            }
            _renderedEq = options.ToList();
        }

        foreach (var element in EqPanel.Children)
        {
            if (element is ToggleButton toggle && toggle.Tag is int code)
            {
                toggle.IsChecked = code == current;
            }
        }
    }

    // ------------------------------------------------------------------ //
    // 事件处理
    // ------------------------------------------------------------------ //
    private void NoiseAnc_Click(object sender, RoutedEventArgs e) => ApplyNoise(0);
    private void NoiseOff_Click(object sender, RoutedEventArgs e) => ApplyNoise(1);
    private void NoiseTransparency_Click(object sender, RoutedEventArgs e) => ApplyNoise(2);

    private void ApplyNoise(int mode)
    {
        if (_suppress) return;
        _hub.SetNoise(mode);
        SetNoiseChecks(mode);
    }

    private void SpatialSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        _hub.SetSpatial(SpatialSwitch.IsChecked == true);
    }

    private void GameSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        _hub.SetGame(GameSwitch.IsChecked == true);
    }

    private async void Find_Click(object sender, RoutedEventArgs e)
    {
        if (_findBusy) return;

        var start = FindButton.IsChecked == true;
        _findRinging = start;
        FindText.Text = start ? "取消" : "查找耳机";
        try
        {
            _hub.Find(start);
        }
        catch (Exception ex)
        {
            AppLog.Runtime($"查找耳机失败：{ex.Message}");
            ResetFindUi();
            return;
        }

        // 开始 / 取消后短暂锁定，避免连点把设备来回切。
        _findBusy = true;
        FindButton.IsEnabled = false;
        try
        {
            await Task.Delay(800);
        }
        finally
        {
            _findBusy = false;
            FindButton.IsEnabled = true;
        }
    }

    private void ResetFindUi()
    {
        _findRinging = false;
        FindButton.IsChecked = false;
        FindText.Text = "查找耳机";
    }

    private void DemoVivo_Click(object sender, RoutedEventArgs e) => DemoRequested?.Invoke(Vendor.Vivo);
    private void DemoHuawei_Click(object sender, RoutedEventArgs e) => DemoRequested?.Invoke(Vendor.Huawei);
    private void DemoOppo_Click(object sender, RoutedEventArgs e) => DemoRequested?.Invoke(Vendor.Oppo);

    private void AboutBack_Click(object sender, RoutedEventArgs e) => AboutPanel.Visibility = Visibility.Collapsed;

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        FillDeviceMenu(menu.Items);
        menu.PlacementTarget = ConnectButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ------------------------------------------------------------------ //
    // 设置菜单（齿轮按钮）—— 顺序 / 文案对齐 Python _build_menu()
    // ------------------------------------------------------------------ //
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsMenu is null) return;
        _settingsMenu.PlacementTarget = SettingsButton;
        _settingsMenu.Placement = PlacementMode.Bottom;
        _settingsMenu.IsOpen = true;
    }

    private void BuildSettingsMenu()
    {
        var menu = new ContextMenu();

        var reconnect = new MenuItem { Header = "重新连接当前设备" };
        reconnect.Click += (_, _) => ReconnectRequested?.Invoke();

        _miDevices = new MenuItem { Header = "选择其他设备…" };

        var linkInfo = new MenuItem { Header = "音频链路信息…" };
        linkInfo.Click += (_, _) => ShowLinkInfo();

        var demo = new MenuItem { Header = "演示模式" };
        AddMenuItem(demo, "vivo TWS（演示）", () => DemoRequested?.Invoke(Vendor.Vivo));
        AddMenuItem(demo, "华为 FreeBuds Pro 5（演示）", () => DemoRequested?.Invoke(Vendor.Huawei));
        AddMenuItem(demo, "OPPO Enco X4（演示）", () => DemoRequested?.Invoke(Vendor.Oppo));
        AddMenuItem(demo, "Redmi Buds 8 Pro（演示）", () => DemoRequested?.Invoke(Vendor.Redmi));

        _miAutoDetect = new MenuItem
        {
            Header = "自动识别并连接耳机",
            IsCheckable = true,
            IsChecked = _settings.GetBool("auto_detect", true),
            StaysOpenOnClick = true
        };
        _miAutoDetect.Click += (_, _) =>
        {
            _settings.Set("auto_detect", _miAutoDetect.IsChecked);
            AutoDetectToggled?.Invoke(_miAutoDetect.IsChecked);
        };

        _miAutoShow = new MenuItem
        {
            Header = "识别到耳机后自动弹出面板",
            IsCheckable = true,
            IsChecked = _settings.GetBool("auto_show", false),
            StaysOpenOnClick = true
        };
        _miAutoShow.Click += (_, _) =>
        {
            _settings.Set("auto_show", _miAutoShow.IsChecked);
            AutoShowToggled?.Invoke(_miAutoShow.IsChecked);
        };

        _miDark = new MenuItem
        {
            Header = "深色模式",
            IsCheckable = true,
            IsChecked = _settings.GetBool("dark", true),
            StaysOpenOnClick = true
        };
        _miDark.Click += (_, _) => DarkThemeToggled?.Invoke(_miDark.IsChecked);

        _miFollowCC = new MenuItem
        {
            Header = "跟随系统控制中心（Win+A）",
            IsCheckable = true,
            IsChecked = _settings.GetBool("follow_control_center", false),
            StaysOpenOnClick = true
        };
        _miFollowCC.Click += (_, _) => FollowControlCenterToggled?.Invoke(_miFollowCC.IsChecked);

        _miCloseTray = new MenuItem
        {
            Header = "最小化到托盘（后台常驻）",
            IsCheckable = true,
            IsChecked = CloseToTray(),
            StaysOpenOnClick = true
        };
        _miCloseTray.Click += (_, _) =>
        {
            _settings.Set("close_action", "tray");
            SyncCloseActionState();
        };

        _miCloseExit = new MenuItem
        {
            Header = "直接退出程序",
            IsCheckable = true,
            IsChecked = !CloseToTray(),
            StaysOpenOnClick = true
        };
        _miCloseExit.Click += (_, _) =>
        {
            _settings.Set("close_action", "exit");
            SyncCloseActionState();
        };

        var closeMenu = new MenuItem { Header = "关闭按钮行为" };
        closeMenu.Items.Add(_miCloseTray);
        closeMenu.Items.Add(_miCloseExit);

        _miAutoHide = new MenuItem
        {
            Header = "自动弹出 5 秒后自动收回",
            IsCheckable = true,
            IsChecked = _settings.GetBool("auto_hide", true),
            StaysOpenOnClick = true
        };
        _miAutoHide.Click += (_, _) => _settings.Set("auto_hide", _miAutoHide.IsChecked);

        var quit = new MenuItem { Header = "退出" };
        quit.Click += (_, _) => QuitRequested?.Invoke();

        var about = new MenuItem { Header = "关于" };
        about.Click += (_, _) => AboutPanel.Visibility = Visibility.Visible;

        menu.Items.Add(reconnect);
        menu.Items.Add(_miDevices);
        menu.Items.Add(linkInfo);
        menu.Items.Add(demo);
        menu.Items.Add(new Separator());
        menu.Items.Add(_miAutoDetect);
        menu.Items.Add(_miAutoShow);
        menu.Items.Add(new Separator());
        menu.Items.Add(_miDark);
        menu.Items.Add(_miFollowCC);
        menu.Items.Add(closeMenu);
        menu.Items.Add(_miAutoHide);
        menu.Items.Add(new Separator());
        menu.Items.Add(quit);
        menu.Items.Add(new Separator());
        menu.Items.Add(about);

        menu.Opened += (_, _) =>
        {
            _menuOpen = true;
            SyncMenuChecks();
            if (_miDevices is not null) FillDeviceMenu(_miDevices.Items);
        };
        menu.Closed += (_, _) => _menuOpen = false;

        _settingsMenu = menu;
        SettingsButton.ContextMenu = menu;
    }

    private static void AddMenuItem(MenuItem parent, string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        parent.Items.Add(item);
    }

    private void SyncMenuChecks()
    {
        if (_miAutoDetect is not null) _miAutoDetect.IsChecked = _settings.GetBool("auto_detect", true);
        if (_miAutoShow is not null) _miAutoShow.IsChecked = _settings.GetBool("auto_show", false);
        if (_miAutoHide is not null) _miAutoHide.IsChecked = _settings.GetBool("auto_hide", true);
        if (_miDark is not null) _miDark.IsChecked = _settings.GetBool("dark", true);
        if (_miFollowCC is not null) _miFollowCC.IsChecked = _settings.GetBool("follow_control_center", false);
        SyncCloseActionState();
    }

    private void FillDeviceMenu(ItemCollection parent)
    {
        parent.Clear();
        var devices = DeviceProvider?.Invoke() ?? new List<DeviceInfo>();
        if (devices.Count == 0)
        {
            parent.Add(new MenuItem { Header = "（没有找到已配对的支持型号）", IsEnabled = false });
            return;
        }

        foreach (var device in devices)
        {
            var item = new MenuItem { Header = $"{device.Name}  {device.PrettyMac}" };
            item.Click += (_, _) => DeviceSelected?.Invoke(device);
            parent.Add(item);
        }
    }

    private void ShowLinkInfo()
    {
        try
        {
            var info = _hub.Info;
            var state = _hub.State;
            var text = info is null
                ? "当前没有连接设备。"
                : $"设备：{info.Name}\nMAC：{info.PrettyMac}\n品牌：{VendorNames.Label(info.Vendor)}\n" +
                  $"链路：蓝牙 RFCOMM（本机）\n状态：{state?.Status ?? "未知"}\n" +
                  $"固件：{(string.IsNullOrEmpty(state?.Firmware) ? "未知" : state!.Firmware)}";
            MessageBox.Show(this, text, "音频链路信息", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLog.Runtime($"音频链路信息失败：{ex.Message}");
        }
    }
}
