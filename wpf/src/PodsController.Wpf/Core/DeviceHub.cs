using PodsController.Devices;
using PodsController.Devices.Huawei;
using PodsController.Devices.Oppo;
using PodsController.Devices.Redmi;

namespace PodsController.Core;

/// <summary>
/// 多品牌统一控制器（对应 Python 版 pods/hub.py）。
///
/// 按型号路由到 HuaweiSession / OppoSession / DemoSession，对外只暴露
/// 一个状态快照和少量事件；UI 不需要为品牌写分支。
/// </summary>
public sealed class DeviceHub
{
    private readonly Action<Action> _post;
    private EarbudSession? _session;

    public DeviceHub(Action<Action>? post = null)
    {
        // 会话在自己的后台线程上跑，事件必须回到 UI 线程再刷新界面
        _post = post ?? (action => action());
    }

    public EarbudSession? Session => _session;

    public DeviceInfo? Info => _session?.Device;

    public DeviceState? State => _session?.State;

    public Vendor Vendor => _session?.Vendor ?? Vendor.Unknown;

    public FeatureSet Features => _session?.State.Features ?? FeatureSet.Empty;

    public bool IsDemo => _session?.Device.IsDemo ?? false;

    public event Action? StateChanged;
    public event Action<bool>? ConnectedChanged;
    public event Action? SessionChanged;
    public event Action<string>? ErrorOccurred;
    public event Action<string>? LogEmitted;

    // ------------------------------------------------------------------ //
    // 连接管理
    // ------------------------------------------------------------------ //
    public void ConnectDemo(Vendor vendor)
    {
        var model = DemoSession.DefaultModel(vendor);
        var device = new DeviceInfo(model, "DEMO:00:00", vendor, IsDemo: true);
        var session = new DemoSession(device);
        Attach(session);
        session.Connect();
    }

    public void ConnectDevice(DeviceInfo info)
    {
        // 启动回退与自动接管可能在同一时刻各发一次；同一设备正在连接时不要重复建 RFCOMM 通道。
        if (_session is not null
            && !_session.State.Connected
            && _session.State.Status.StartsWith("连接中", StringComparison.Ordinal)
            && string.Equals(_session.Device.PlainMac, info.PlainMac, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (info.IsDemo)
        {
            var demo = new DemoSession(info);
            Attach(demo);
            demo.Connect();
            return;
        }

        switch (info.Vendor)
        {
            case Vendor.Huawei:
            {
                var session = new HuaweiSession(info);
                Attach(session);
                session.Connect();
                break;
            }
            case Vendor.Oppo:
            {
                var session = new OppoSession(info);
                Attach(session);
                session.Connect();
                break;
            }
            case Vendor.Redmi:
            {
                var session = new RedmiSession(info);
                Attach(session);
                session.Connect();
                break;
            }
            case Vendor.Vivo:
                // vivo 的 Python 协议源码在仓库里，但 C# 侧还没有对应 Session。
                RaiseError("vivo / iQOO 的真实控制链路尚未移植到 C#，当前请先用演示模式。");
                break;
            default:
                RaiseError($"未知品牌：{info.Name}");
                break;
        }
    }

    public void Disconnect()
    {
        try { _session?.Disconnect(); } catch { /* 忽略 */ }
    }

    private void Attach(EarbudSession session)
    {
        var previous = _session;
        if (previous is not null)
        {
            previous.ConnectedChanged -= OnConnectedChanged;
            previous.StateChanged -= OnStateChanged;
            previous.ErrorOccurred -= RaiseError;
            previous.LogEmitted -= OnLog;
            try { previous.Disconnect(); } catch { /* 忽略 */ }
        }

        session.AttachDispatcher(_post);
        session.ConnectedChanged += OnConnectedChanged;
        session.StateChanged += OnStateChanged;
        session.ErrorOccurred += RaiseError;
        session.LogEmitted += OnLog;

        _session = session;
        _post(() => SessionChanged?.Invoke());
    }

    // ------------------------------------------------------------------ //
    // 控制转发
    // ------------------------------------------------------------------ //
    public void SetNoise(int mode) => _session?.SetNoise(mode);
    public void SetSpatial(bool on) => _session?.SetSpatial(on);
    public void SetGame(bool on) => _session?.SetGame(on);
    public void SetEq(int code) => _session?.SetEq(code);
    public void Find(bool start) => _session?.Find(start);
    public void SetMultiConnectStatus(string mac, bool connect) => _session?.SetMultiConnectStatus(mac, connect);
    public void SetPreferredDevice(string mac) => _session?.SetPreferredDevice(mac);

    // ------------------------------------------------------------------ //
    private void OnConnectedChanged() => _post(() => ConnectedChanged?.Invoke(_session?.State.Connected ?? false));

    private void OnStateChanged() => _post(() => StateChanged?.Invoke());

    private void OnLog(string message) => _post(() => LogEmitted?.Invoke(message));

    private void RaiseError(string message) => _post(() => ErrorOccurred?.Invoke(message));
}
