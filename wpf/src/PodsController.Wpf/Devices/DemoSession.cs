using PodsController.Core;
using PodsController.Devices.Huawei;
using PodsController.Devices.Oppo;

namespace PodsController.Devices;

/// <summary>
/// 演示会话（对应 Python 版 huawei/demo_transport.py + oppo/demo_transport.py
/// 的「无真机也能跑完整流程」能力，三个品牌各一套模拟状态）。
///
/// 演示也遵循同一套状态机 / 能力矩阵，因此 UI 拿到的分区与真机一致，
/// 不会出现「演示里能看到、真机上却消失」的假象。
/// </summary>
public sealed class DemoSession : EarbudSession
{
    private Thread? _thread;
    private volatile bool _stop;

    public DemoSession(DeviceInfo device) : base(device) { }

    public static string DefaultModel(Vendor vendor) => vendor switch
    {
        Vendor.Vivo => "TWS 5",
        Vendor.Huawei => "FreeBuds Pro 5",
        Vendor.Oppo => "Enco X4",
        _ => "演示耳机"
    };

    protected override IEnumerable<EqOption> OptionsForDevice() => Vendor switch
    {
        Vendor.Vivo => DeviceCatalog.VivoEqOptions,
        Vendor.Huawei => HuaweiCatalog.EqOptions(Device.Name),
        Vendor.Oppo => OppoCatalog.EqPresets,
        _ => Array.Empty<EqOption>()
    };

    // ------------------------------------------------------------------ //
    public override void Connect()
    {
        ResetReadings();
        SetConnected(false);
        SetStatus("连接中…");
        NotifyLog($"演示模式：{Device.Name}（{Device.Mac}）");

        _stop = false;
        _thread = new Thread(Run) { IsBackground = true, Name = "demo-session" };
        _thread.Start();
    }

    private void Run()
    {
        Thread.Sleep(350);
        if (_stop) return;

        State.LeftPercent = 78;
        State.RightPercent = 76;
        State.CasePercent = 64;
        State.LeftCharging = false;
        State.RightCharging = false;
        State.CaseCharging = false;
        State.NoiseMode = 0;
        State.EqMode = 0;
        State.Firmware = Vendor == Vendor.Oppo ? "116 / 116 / 100" : "1.0.0.168（演示）";

        switch (Vendor)
        {
            case Vendor.Vivo:
                State.Spatial = 0;
                State.Game = 0;
                State.WearLeft = "unknown";
                State.WearRight = "unknown";
                State.MultiPoint.Clear();
                State.MultiPoint.Add(new MultiPointRow { Name = "这台电脑", Mac = "00:00:00:00:00:00", Status = 2 });
                State.MultiPoint.Add(new MultiPointRow { Name = "Xiaomi 15 Pro", Mac = "AA:BB:CC:DD:EE:01", Status = 1 });
                break;

            case Vendor.Huawei:
                State.Spatial = -1; // 华为侧没有空间音频
                State.Game = 0;
                State.WearLeft = "worn";
                State.WearRight = "worn";
                State.MultiPoint.Clear();
                State.MultiPoint.Add(new MultiPointRow { Name = "这台电脑", Mac = "00:00:00:00:00:00", Status = 2 });
                State.MultiPoint.Add(new MultiPointRow { Name = "Mate 60 Pro", Mac = "AA:BB:CC:DD:EE:02", Status = 1 });
                break;

            case Vendor.Oppo:
                State.Spatial = 0;
                State.Game = 0;
                State.WearLeft = "worn";
                State.WearRight = "worn";
                break;
        }

        State.Features = DeviceCatalog.FeaturesFor(Device.Name, Vendor);
        NotifyState();

        SetConnected(true);
        SetStatus("已连接");
    }

    public override void Disconnect()
    {
        _stop = true;
        try { _thread?.Join(1000); } catch { /* 忽略 */ }
        _thread = null;

        SetConnected(false);
        SetStatus("已断开");
        State.LeftPercent = -1; State.RightPercent = -1; State.CasePercent = -1;
        State.Spatial = -1; State.Game = -1;
        State.WearLeft = "unknown"; State.WearRight = "unknown";
        State.MultiPoint.Clear();
        State.Firmware = "";
        NotifyState();
    }

    // ------------------------------------------------------------------ //
    public override void SetNoise(int mode)
    {
        State.NoiseMode = mode;
        NotifyState();
    }

    public override void SetSpatial(bool on)
    {
        State.Spatial = on ? 1 : 0;
        NotifyState();
    }

    public override void SetGame(bool on)
    {
        State.Game = on ? 1 : 0;
        NotifyState();
    }

    public override void SetEq(int code)
    {
        State.EqMode = code;
        NotifyState();
    }

    public override void Find(bool start) => NotifyLog($"查找耳机：{(start ? "开始响铃" : "停止响铃")}（演示）");

    public override void SetMultiConnectStatus(string mac, bool connect)
    {
        var row = State.MultiPoint.FirstOrDefault(x => string.Equals(x.Mac, mac, StringComparison.OrdinalIgnoreCase));
        if (row is null) return;

        State.MultiPoint[State.MultiPoint.IndexOf(row)] = new MultiPointRow
        {
            Name = row.Name,
            Mac = row.Mac,
            Status = connect ? 1 : 0
        };
        NotifyState();
    }
}
