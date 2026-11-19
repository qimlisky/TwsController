using PodsController.Core;

namespace PodsController.Devices;

/// <summary>
/// 耳机会话基类：对外只有「一个状态快照 + 少量事件」，
/// 与 Python 版三个控制器的信号面等价（connected / status / battery /
/// noise_mode / spatial / game_mode / eq_mode / wear / firmware /
/// multipoint / error / log）。
///
/// 会话跑在自己的后台线程上，所有事件统一经由 <see cref="Post"/> 投到
/// UI 线程（AppHost 注入 DispatcherQueue），UI 拿到的永远是完整快照。
/// </summary>
public abstract class EarbudSession
{
    protected EarbudSession(DeviceInfo device)
    {
        Device = device;
        State = new DeviceState();
    }

    public DeviceInfo Device { get; }

    public Vendor Vendor => Device.Vendor;

    public DeviceState State { get; }

    /// <summary>UI 线程投递器（默认同步执行，测试 / 演示时可直接用）。</summary>
    protected Action<Action> Post { get; set; } = action => action();

    public event Action? StateChanged;
    public event Action? ConnectedChanged;
    public event Action<string>? ErrorOccurred;
    public event Action<string>? LogEmitted;

    public void AttachDispatcher(Action<Action> post) => Post = post;

    // ------------------------------------------------------------------ //
    // 子类要实现的公开面
    // ------------------------------------------------------------------ //
    public abstract void Connect();

    public abstract void Disconnect();

    public abstract void SetNoise(int mode);

    public abstract void SetSpatial(bool on);

    public abstract void SetGame(bool on);

    public abstract void SetEq(int code);

    public abstract void Find(bool start);

    public virtual void SetMultiConnectStatus(string mac, bool connect) { }

    public virtual void SetPreferredDevice(string mac) { }

    // ------------------------------------------------------------------ //
    // 子类用的通知工具
    // ------------------------------------------------------------------ //
    protected void SetStatus(string text)
    {
        State.Status = text;
        Post(() => StateChanged?.Invoke());
    }

    protected void SetConnected(bool connected)
    {
        if (State.Connected == connected)
        {
            State.Connected = connected;
            return;
        }
        State.Connected = connected;
        Post(() =>
        {
            ConnectedChanged?.Invoke();
            StateChanged?.Invoke();
        });
    }

    protected void NotifyState() => Post(() => StateChanged?.Invoke());

    protected void NotifyError(string message)
    {
        AppLog.Runtime($"[{VendorNames.ToKey(Vendor)}] {message}");
        Post(() => ErrorOccurred?.Invoke(message));
    }

    protected void NotifyLog(string message)
    {
        AppLog.Runtime($"[{VendorNames.ToKey(Vendor)}] {message}");
        Post(() => LogEmitted?.Invoke(message));
    }

    /// <summary>新会话开始：清空上一台设备的读数，避免初始化期间显示旧数据。</summary>
    protected void ResetReadings()
    {
        State.LeftPercent = -1; State.LeftCharging = false;
        State.RightPercent = -1; State.RightCharging = false;
        State.CasePercent = -1; State.CaseCharging = false;
        State.NoiseMode = -1;
        State.Spatial = -1;
        State.Game = -1;
        State.EqMode = -1;
        State.WearLeft = "unknown";
        State.WearRight = "unknown";
        State.Firmware = "";
        State.MultiPoint.Clear();
        State.Features = DeviceCatalog.FeaturesFor(Device.Name, Vendor);
        State.EqOptions.Clear();
        State.EqOptions.AddRange(OptionsForDevice());
        NotifyState();
    }

    protected virtual IEnumerable<EqOption> OptionsForDevice()
    {
        return Array.Empty<EqOption>();
    }

    protected static bool TryParseBool(string? value, bool fallback = false)
    {
        var text = (value ?? "").Trim().Trim('"').ToLowerInvariant();
        return text switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => fallback
        };
    }

    protected static string MacPlain(string? mac) =>
        new string((mac ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}
