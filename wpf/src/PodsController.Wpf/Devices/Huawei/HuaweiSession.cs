using System.Collections.Concurrent;

using PodsController.Core;
using PodsController.Interop;

namespace PodsController.Devices.Huawei;

/// <summary>
/// 华为 / HONOR 耳机会话（对应 huawei/controller.py + huawei/bridge.py 的职责）。
///
/// 与 Python 版的差异：上游 OpenFreebuds 用 asyncio + handler 属性仓库，
/// C# 版把用到的几个 handler（battery / anc / tws_in_ear / low_latency /
/// config_eq / dual_connect / device_info）直接落在收包分发里，
/// 命令号、参数编号、语义映射与上游保持一致。
/// </summary>
public sealed class HuaweiSession : EarbudSession
{
    private const int SppChannel = 1;

    private readonly string? _model;
    private readonly BlockingCollection<HuaweiPackage> _queue = new();
    private readonly List<(int Index, MultiPointRow Row)> _dualPending = new();
    private int _dualCount = int.MaxValue;

    private RfcommSocket? _socket;
    private Thread? _rxThread;
    private Thread? _txThread;
    private volatile bool _stop;

    public HuaweiSession(DeviceInfo device) : base(device)
    {
        _model = HuaweiCatalog.FindModel(device.Name);
    }

    protected override IEnumerable<EqOption> OptionsForDevice() =>
        HuaweiCatalog.EqOptions(_model);

    // ------------------------------------------------------------------ //
    // 连接
    // ------------------------------------------------------------------ //
    public override void Connect()
    {
        if (_model is null)
        {
            NotifyError($"暂不支持该型号：{Device.Name}");
            SetStatus("连接失败");
            SetConnected(false);
            return;
        }

        ResetReadings();
        SetConnected(false);
        SetStatus("连接中…");

        _stop = false;
        _rxThread = new Thread(Run, 64 * 1024)
        {
            IsBackground = true,
            Name = $"huawei-{Device.PlainMac}"
        };
        _rxThread.Start();
    }

    private void Run()
    {
        try
        {
            _socket = RfcommSocket.Connect(Device.Mac, SppChannel);
        }
        catch (Exception ex)
        {
            NotifyError($"连接失败：{ex.Message}");
            SetStatus("连接失败");
            SetConnected(false);
            return;
        }

        SetConnected(true);
        SetStatus($"已连接 · {HuaweiCatalog.DisplayName(Device.Name)}");
        NotifyLog($"已连接 {Device.Name}（{Device.Mac}），协议=华为 SPP/RFCOMM 通道 {SppChannel}");

        _txThread = new Thread(TxLoop) { IsBackground = true, Name = "huawei-tx" };
        _txThread.Start();

        var splitter = new HuaweiFrameSplitter();
        try
        {
            while (!_stop)
            {
                var chunk = _socket.Receive();
                if (chunk.Length == 0)
                {
                    if (_socket.Connected) continue;
                    break;
                }

                foreach (var package in splitter.Offer(chunk))
                {
                    try
                    {
                        Handle(package);
                    }
                    catch (Exception ex)
                    {
                        NotifyLog($"包处理异常：{ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (!_stop) NotifyError($"蓝牙读取异常：{ex.Message}");
        }
        finally
        {
            _stop = true;
            try { _socket?.Dispose(); } catch { /* 忽略 */ }
            _socket = null;
            SetConnected(false);
            SetStatus("已断开");
        }
    }

    private void TxLoop()
    {
        // 初始化序列：与上游 handler on_init 依次发出的命令一致
        Enqueue(HuaweiPackage.ReadRequest(HuaweiCommands.AncRead, 1, 2));
        Enqueue(HuaweiPackage.ReadRequest(HuaweiCommands.BatteryRead, 1, 2, 3));
        Enqueue(HuaweiPackage.ReadRequest(HuaweiCommands.LowLatency, 2));
        Enqueue(HuaweiPackage.ReadRequest(HuaweiCommands.Info, Enumerable.Range(0, 32).ToArray()));
        Enqueue(HuaweiPackage.ReadRequest(HuaweiCommands.EqRead, 1, 2, 3, 4, 5, 6, 7, 8));
        Enqueue(HuaweiPackage.ReadRequest(HuaweiCommands.DualEnabledRead, 1));
        Enqueue(HuaweiPackage.ChangeRequest(HuaweiCommands.DualEnumerate, (1, Array.Empty<byte>())));

        var lastPoll = DateTime.UtcNow;
        while (!_stop)
        {
            HuaweiPackage? package;
            try
            {
                package = _queue.TryTake(out var item, 100) ? item : null;
            }
            catch (InvalidOperationException)
            {
                break;
            }

            if (package is not null)
            {
                try
                {
                    _socket?.Send(package.ToBytes());
                }
                catch (Exception ex)
                {
                    if (!_stop) NotifyError($"发送失败：{ex.Message}");
                }
                Thread.Sleep(150);
                continue;
            }

            // 30s 轮询一次电量 / 降噪（对应 Python 版的周期查询）
            if ((DateTime.UtcNow - lastPoll).TotalSeconds >= 30)
            {
                lastPoll = DateTime.UtcNow;
                Enqueue(HuaweiPackage.ReadRequest(HuaweiCommands.BatteryRead, 1, 2, 3));
                Enqueue(HuaweiPackage.ReadRequest(HuaweiCommands.AncRead, 1, 2));
            }
        }
    }

    private void Enqueue(HuaweiPackage package)
    {
        try
        {
            if (!_stop) _queue.Add(package);
        }
        catch
        {
            // 队列已关闭
        }
    }

    public override void Disconnect()
    {
        _stop = true;
        try { _socket?.Dispose(); } catch { /* 忽略 */ }
        _socket = null;

        try { _rxThread?.Join(2000); } catch { /* 忽略 */ }
        try { _txThread?.Join(1000); } catch { /* 忽略 */ }
        _rxThread = null;
        _txThread = null;

        SetConnected(false);
        SetStatus("已断开");
        State.LeftPercent = -1; State.RightPercent = -1; State.CasePercent = -1;
        State.Spatial = -1; State.Game = -1;
        State.MultiPoint.Clear();
        State.WearLeft = "unknown"; State.WearRight = "unknown";
        State.Firmware = "";
        NotifyState();
    }

    // ------------------------------------------------------------------ //
    // 收包分发
    // ------------------------------------------------------------------ //
    private void Handle(HuaweiPackage package)
    {
        switch (package.CommandId)
        {
            case 0x2B2A: // ANC 读回（上游 OfbHuaweiAncHandler）
                OnAnc(package);
                break;

            case 0x0108: // CMD_BATTERY_READ
            case 0x0127: // CMD_BATTERY_NOTIFY
                OnBattery(package);
                break;

            case 0x2B03: // 佩戴状态（+ 0x03）
                OnInEar(package);
                break;

            case 0x2B6C: // 低延迟
                OnLowLatency(package);
                break;

            case 0x2B4A: // EQ 读回
                OnEqualizer(package);
                break;

            case 0x0107: // 设备信息（固件版本）
                OnInfo(package);
                break;

            case 0x2B2F: // 双连开关
                break;

            case 0x2B31: // 双连枚举
            case 0x2B36: // 双连变化事件
                OnDualConnect(package);
                break;
        }
    }

    private void OnAnc(HuaweiPackage package)
    {
        var data = package.FindParam(1);
        if (data.Length != 2) return;

        var ui = data[1] switch
        {
            1 => 0, // cancellation
            0 => 1, // normal
            2 => 2, // awareness
            _ => -1
        };
        if (ui < 0) return;
        State.NoiseMode = ui;
        NotifyState();
    }

    private void OnBattery(HuaweiPackage package)
    {
        var levels = package.FindParam(2);
        if (levels.Length == 3)
        {
            State.LeftPercent = levels[0];
            State.RightPercent = levels[1];
            State.CasePercent = levels[2];
        }
        else
        {
            var global = package.FindParam(1);
            if (global.Length == 1)
            {
                State.LeftPercent = global[0];
                State.RightPercent = global[0];
            }
        }

        var flags = package.FindParam(3);
        var charging = flags.Contains((byte)0x01);
        State.LeftCharging = charging;
        State.RightCharging = charging;
        State.CaseCharging = charging;
        NotifyState();
    }

    private void OnInEar(HuaweiPackage package)
    {
        // 上游 handler 取 param 8 / 9 的单字节：1 = 在耳
        var value = package.FindParam(8, 9);
        if (value.Length != 1) return;
        var worn = value[0] == 1;
        State.WearLeft = worn ? "worn" : "in_case";
        State.WearRight = worn ? "worn" : "in_case";
        NotifyState();
    }

    private void OnLowLatency(HuaweiPackage package)
    {
        var value = package.FindParam(2);
        if (value.Length == 0) return;
        State.Game = value[0] == 1 ? 1 : 0;
        NotifyState();
    }

    private void OnEqualizer(HuaweiPackage package)
    {
        var current = package.FindParam(2);
        if (current.Length != 1) return;

        var presetId = current[0];
        var uiCode = HuaweiCatalog.UiCodeFromPresetId(_model, presetId);
        if (uiCode is null) return;

        State.EqMode = uiCode.Value;
        NotifyState();
    }

    private void OnInfo(HuaweiPackage package)
    {
        var software = package.FindParam(7);
        if (software.Length == 0) return;
        State.Firmware = System.Text.Encoding.UTF8.GetString(software).Trim('\0', ' ');
        NotifyState();
    }

    private void OnDualConnect(HuaweiPackage package)
    {
        var macBytes = package.FindParam(4);
        if (macBytes.Length < 6) return;

        var countBytes = package.FindParam(2);
        var indexBytes = package.FindParam(3);
        if (countBytes.Length == 0 || indexBytes.Length == 0) return;

        _dualCount = countBytes[0];
        var index = indexBytes[0];

        var mac = string.Join(":", Enumerable.Range(0, 6)
            .Select(i => macBytes[i].ToString("X2")));

        var stateBytes = package.FindParam(5);
        var connState = stateBytes.Length > 0 ? stateBytes[0] : 0;
        var status = connState == 9 ? 2 : connState > 0 ? 1 : 0;

        var nameBytes = package.FindParam(9);
        var name = nameBytes.Length > 0
            ? System.Text.Encoding.UTF8.GetString(nameBytes).Trim('\0', ' ')
            : mac;

        _dualPending.Add((index, new MultiPointRow { Name = name, Mac = mac, Status = status }));
        if (_dualPending.Count < _dualCount && _dualPending.Count < 8) return;

        State.MultiPoint.Clear();
        foreach (var (i, row) in _dualPending.OrderBy(x => x.Index)) State.MultiPoint.Add(row);
        _dualPending.Clear();
        _dualCount = int.MaxValue;
        NotifyState();
    }

    // ------------------------------------------------------------------ //
    // 控制指令
    // ------------------------------------------------------------------ //
    public override void SetNoise(int mode)
    {
        var vendor = mode switch
        {
            0 => 1, // cancellation
            1 => 0, // normal
            2 => 2, // awareness
            _ => -1
        };
        if (vendor < 0)
        {
            NotifyLog($"未知降噪档位：{mode}");
            return;
        }

        var data = new[] { (byte)vendor, vendor == 0 ? (byte)0x00 : (byte)0xFF };
        Enqueue(HuaweiPackage.ChangeRequest(HuaweiCommands.AncWrite, (1, data)));
        Enqueue(HuaweiPackage.ReadRequest(HuaweiCommands.AncRead, 1, 2));
        NotifyLog($"[降噪] 下发 {mode} → 华为 mode={vendor}");
    }

    public override void SetSpatial(bool on)
    {
        // 上游没有空间音频 handler，能力矩阵里也不会给出这一位
        NotifyLog("该型号不支持空间音频（华为侧未接入），指令已忽略");
    }

    public override void SetGame(bool on)
    {
        Enqueue(HuaweiPackage.ChangeRequest(HuaweiCommands.LowLatency, (1, new[] { on ? (byte)1 : (byte)0 })));
        Enqueue(HuaweiPackage.ReadRequest(HuaweiCommands.LowLatency, 2));
    }

    public override void SetEq(int code)
    {
        var presetId = HuaweiCatalog.PresetIdFromUiCode(_model, code);
        if (presetId is null)
        {
            NotifyLog($"该型号不支持 EQ 预设 {code}");
            return;
        }
        Enqueue(HuaweiPackage.ChangeRequest(HuaweiCommands.EqWrite, (1, new[] { (byte)presetId.Value })));
        Enqueue(HuaweiPackage.ReadRequest(HuaweiCommands.EqRead, 1, 2, 3, 4, 5, 6, 7, 8));
        NotifyLog($"[EQ] 下发 UI {code} → preset id {presetId}");
    }

    public override void Find(bool start)
    {
        NotifyLog($"查找耳机：该型号暂不支持（华为侧未接入），start={start}");
    }

    public override void SetMultiConnectStatus(string mac, bool connect)
    {
        var plain = MacPlain(mac);
        if (plain.Length != 12)
        {
            NotifyLog("缺少设备 MAC，已忽略双连操作");
            return;
        }

        var command = (byte)(connect ? HuaweiDualCommand.Connect : HuaweiDualCommand.Disconnect);
        Enqueue(HuaweiPackage.ChangeRequest(HuaweiCommands.DualExecute,
            (command, Convert.FromHexString(plain))));
        NotifyLog($"[双连] 已下发 {(connect ? "连接" : "断开")} {mac}");
    }

    public override void SetPreferredDevice(string mac)
    {
        var plain = MacPlain(mac);
        if (plain.Length != 12) return;
        Enqueue(HuaweiPackage.ChangeRequest(HuaweiCommands.DualPreferredWrite,
            (1, Convert.FromHexString(plain))));
    }
}
