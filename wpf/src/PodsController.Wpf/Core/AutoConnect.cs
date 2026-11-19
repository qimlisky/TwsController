namespace PodsController.Core;

/// <summary>
/// 自动识别 / 自动连接（对应 Python 版 pods/autoconnect.py）。
///
/// 后台每 3 秒读一次 Windows 蓝牙设备表，发现「受支持的耳机进入已连接状态」
/// 就上报一次；同一次连接期间只上报一次，断开后重新连上才会再报。
/// 只看结果、不弹面板 —— 弹不弹由 AppHost 按设置决定。
/// </summary>
public sealed class AutoConnectManager
{
    private readonly DeviceHub _hub;
    private readonly SettingsStore _settings;

    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _enabled = true;
    private bool _autoShow = true;

    private readonly HashSet<string> _takenOver = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _missingPolls = new(StringComparer.OrdinalIgnoreCase);
    private string _lastSignature = "";

    public AutoConnectManager(DeviceHub hub, SettingsStore settings)
    {
        _hub = hub;
        _settings = settings;
        _autoShow = settings.GetBool("auto_show", false);
        _enabled = settings.GetBool("auto_detect", true);
    }

    public bool Enabled => _enabled;

    public bool AutoShow
    {
        get => _autoShow;
        set
        {
            _autoShow = value;
            _settings.Set("auto_show", value);
        }
    }

    public event Action<DeviceInfo>? AutoConnected;
    public event Action<string>? Hint;
    public event Action<List<DeviceInfo>>? SnapshotChanged;

    // ------------------------------------------------------------------ //
    public void Start()
    {
        if (_thread is not null) return;
        _stop = false;
        _thread = new Thread(Loop) { IsBackground = true, Name = "auto-connect" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        try { _thread?.Join(1000); } catch { /* 忽略 */ }
        _thread = null;
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        _settings.Set("auto_detect", enabled);
        if (!enabled) _takenOver.Clear();
        if (!enabled) _missingPolls.Clear();
        SnapshotChanged?.Invoke(new List<DeviceInfo>());
    }

    /// <summary>睡眠唤醒 / 解锁之后丢掉旧快照，按当前真实蓝牙状态重新判断。</summary>
    public void NotifyResume()
    {
        lock (_takenOver)
        {
            _takenOver.Clear();
            _missingPolls.Clear();
            _lastSignature = "";
        }
        SnapshotChanged?.Invoke(new List<DeviceInfo>());
    }

    // ------------------------------------------------------------------ //
    private void Loop()
    {
        // 启动先立刻 poll 一次：首屏就能知道耳机在不在
        Poll();
        while (!_stop)
        {
            if (Wait(3000)) return;
            Poll();
        }
    }

    private bool Wait(int milliseconds)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!_stop && DateTime.UtcNow < end)
        {
            Thread.Sleep(100);
        }
        return _stop;
    }

    private void Poll()
    {
        try
        {
            var devices = BluetoothEnumerator.ListConnectedEarbuds();
            var signature = string.Join("|", devices.Select(d => $"{d.Vendor}:{d.PlainMac}"));

            if (signature != _lastSignature)
            {
                _lastSignature = signature;
                if (devices.Count > 0)
                {
                    Hint?.Invoke("检测到已连接耳机：" +
                        string.Join("、", devices.Select(d => $"{d.Name}({d.Mac})")));
                }
                SnapshotChanged?.Invoke(devices);
            }

            lock (_takenOver)
            {
                // 断开过的设备要放回可接管集合；连续两次没看到才认为真的断开，
                // 避免 RFCOMM 控制通道建立时蓝牙枚举短暂掉线导致反复重连。
                var stillConnected = devices.Select(d => d.PlainMac).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var mac in _takenOver.ToList())
                {
                    if (stillConnected.Contains(mac))
                    {
                        _missingPolls.Remove(mac);
                        continue;
                    }

                    var missing = _missingPolls.TryGetValue(mac, out var count) ? count + 1 : 1;
                    _missingPolls[mac] = missing;
                    if (missing >= 2)
                    {
                        _takenOver.Remove(mac);
                        _missingPolls.Remove(mac);
                    }
                }

                if (!_enabled) return;

                foreach (var device in devices)
                {
                    if (_takenOver.Contains(device.PlainMac)) continue;
                    _takenOver.Add(device.PlainMac);
                    Hint?.Invoke($"自动接管：{device.Name}（{VendorNames.Label(device.Vendor)}）");
                    AutoConnected?.Invoke(device);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Runtime($"自动识别失败：{ex.Message}");
            Hint?.Invoke($"自动识别不可用：{ex.Message}");
        }
    }
}
