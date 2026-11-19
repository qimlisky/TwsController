using System.Collections.Concurrent;

using PodsController.Core;
using PodsController.Interop;

namespace PodsController.Devices.Oppo;

/// <summary>
/// OPPO / 一加 / realme 耳机会话（对应 oppo/controller.py）。
///
/// 连接通道按「15 → 1 → 其余」逐个尝试：Enco X4 实测走 15，
/// 部分旧型号 / realme 走 1，其余型号用扫描兜底。
/// 能力不按型号猜，收到对应回包才补出控件。
/// </summary>
public sealed class OppoSession : EarbudSession
{
    private static readonly int[] ChannelAttempts = BuildChannels();

    private readonly BlockingCollection<byte[]> _queue = new();
    private RfcommSocket? _socket;
    private Thread? _rxThread;
    private Thread? _txThread;
    private volatile bool _stop;

    private volatile bool _spatialSeen;
    private volatile bool _spatialV2 = true;
    private volatile bool _wantSpatial;

    public OppoSession(DeviceInfo device) : base(device) { }

    protected override IEnumerable<EqOption> OptionsForDevice() => OppoCatalog.EqPresets;

    private static int[] BuildChannels()
    {
        var list = new List<int> { 15, 1 };
        for (var i = 2; i <= 30; i++) if (i != 15) list.Add(i);
        return list.ToArray();
    }

    // ------------------------------------------------------------------ //
    public override void Connect()
    {
        ResetReadings();
        SetConnected(false);
        SetStatus("连接中…");

        _stop = false;
        _spatialSeen = false;
        _spatialV2 = true;
        _wantSpatial = false;

        _rxThread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"oppo-{Device.PlainMac}"
        };
        _rxThread.Start();
    }

    private void Run()
    {
        try
        {
            _socket = RfcommSocket.Connect(Device.Mac, ChannelAttempts);
        }
        catch (Exception ex)
        {
            NotifyError($"连接失败：{ex.Message}");
            SetStatus("连接失败");
            SetConnected(false);
            return;
        }

        SetConnected(true);
        SetStatus($"已连接 · {OppoCatalog.DisplayName(Device.Name)}");
        NotifyLog($"已连接 {Device.Name}（{Device.Mac}），协议=OPO v1 RFCOMM 通道 {_socket.Channel}");

        _txThread = new Thread(TxLoop) { IsBackground = true, Name = "oppo-tx" };
        _txThread.Start();

        var decoder = new OppoDecoder();
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

                foreach (var frame in decoder.Offer(chunk))
                {
                    try
                    {
                        Handle(frame);
                    }
                    catch (Exception ex)
                    {
                        NotifyLog($"帧处理异常：{ex.Message}");
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
        // 初始化序列：与 oppo/controller._sender_loop 一致
        foreach (var command in OppoProtocol.InitialCommands())
        {
            if (_stop) return;
            Send(command);
            Thread.Sleep(180);
        }

        Thread.Sleep(800);
        if (!_stop && !_spatialSeen)
        {
            _spatialV2 = false;
            Send(OppoProtocol.CmdSwitchGet(new[] { OppoProtocol.SwitchSpatialV1, OppoProtocol.SwitchHeadTrack }));
        }

        var lastPoll = DateTime.UtcNow;
        while (!_stop)
        {
            byte[]? pending = null;
            if (_queue.TryTake(out var item, 100)) pending = item;

            if (pending is not null)
            {
                Send(pending);
                continue;
            }

            if ((DateTime.UtcNow - lastPoll).TotalSeconds >= 30)
            {
                lastPoll = DateTime.UtcNow;
                Send(OppoProtocol.CmdBattery());
                Send(OppoProtocol.CmdNoiseGet());
                Send(OppoProtocol.CmdSpatialGet());
            }
        }
    }

    private void Send(byte[] data)
    {
        try
        {
            if (!_stop) _socket?.Send(data);
        }
        catch (Exception ex)
        {
            if (!_stop) NotifyError($"发送失败：{ex.Message}");
        }
    }

    private void Enqueue(byte[] data)
    {
        try
        {
            if (!_stop) _queue.Add(data);
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
        NotifyState();
    }

    // ------------------------------------------------------------------ //
    private void Handle(OppoFrame frame)
    {
        switch (frame.Cmd)
        {
            // 耳机主动索要主机时间：必须应答，否则部分型号不推状态
            case 0x0500:
                Enqueue(OppoProtocol.CmdHostTime());
                return;

            case 0x051C:
                NotifyLog("耳机请求主机能力（暂不应答）");
                return;

            case 0x8100:
                NotifyLog($"握手成功，能力位图={BitConverter.ToString(frame.Payload)}");
                return;

            case 0x8106:
            case 0x0201:
                OnBattery(frame.Payload, frame.Cmd == 0x0201);
                return;

            case 0x8109:
            case 0x0202:
                OnWear(frame.Payload, frame.Cmd == 0x0202);
                return;

            case 0x810C:
            case 0x0203:
                OnNoise(frame.Payload, frame.Cmd == 0x0203);
                return;

            case 0x812A:
                OnSpatial(frame.Payload);
                return;

            case 0x8422:
                // 空间音频 V2 写入结果：被拒则退回功能开关 27
                if (frame.Status == 0x00)
                {
                    _spatialSeen = true;
                    _spatialV2 = true;
                }
                else
                {
                    _spatialV2 = false;
                    NotifyLog($"0x0422 写入被拒（{OppoProtocol.StatusText(frame.Status)}），改用功能开关 27");
                    Enqueue(OppoProtocol.CmdSwitchSet(OppoProtocol.SwitchSpatialV1, _wantSpatial));
                }
                return;

            case 0x810D:
            case 0x0503:
                var body = frame.Cmd == 0x810D
                    ? frame.Payload
                    : (frame.Payload.Length > 0 && frame.Payload[0] == 0 ? frame.Payload[1..] : frame.Payload);
                OnSwitches(body);
                return;

            case 0x810F:
                OnEq(frame.Payload);
                return;

            case 0x0504:
                if (frame.Payload.Length > 0)
                {
                    State.EqMode = frame.Payload[0];
                    NotifyState();
                }
                return;

            case 0x8105:
                var firmware = OppoProtocol.ParseFirmware(frame.Payload);
                if (!string.IsNullOrEmpty(firmware))
                {
                    State.Firmware = firmware;
                    NotifyState();
                }
                return;
        }
    }

    private void OnBattery(byte[] payload, bool stripSubType)
    {
        var body = stripSubType && payload.Length > 0 ? payload[1..] : payload;
        var battery = OppoProtocol.ParseBattery(body);
        if (battery is null) return;

        State.LeftPercent = battery.Left ?? -1;
        State.LeftCharging = battery.LeftCharging;
        State.RightPercent = battery.Right ?? -1;
        State.RightCharging = battery.RightCharging;
        State.CasePercent = battery.Case ?? -1;
        State.CaseCharging = battery.CaseCharging;
        NotifyState();
    }

    private void OnWear(byte[] payload, bool stripSubType)
    {
        var body = stripSubType && payload.Length > 0 ? payload[1..] : payload;
        var wear = OppoProtocol.ParseWear(body);
        if (wear is null) return;

        State.WearLeft = wear.Value.Left;
        State.WearRight = wear.Value.Right;
        NotifyState();
    }

    private void OnNoise(byte[] payload, bool stripSubType)
    {
        var body = stripSubType && payload.Length > 0 ? payload[1..] : payload;

        var raw = OppoProtocol.ParseNoiseIndex(body);
        var mode = OppoProtocol.ParseNoise(body);

        // 只有真的出现降噪 / 通透档位，才算这只耳机有 ANC
        if (raw is not null && raw is not 0 and not 3)
        {
            Grant(Feature.Noise);
        }
        if (mode is not null)
        {
            State.NoiseMode = mode.Value;
            NotifyState();
        }
    }

    private void OnSpatial(byte[] payload)
    {
        _spatialSeen = true;
        _spatialV2 = true;
        var mode = OppoProtocol.ParseSpatial(payload);
        if (mode is null) return;

        Grant(Feature.Spatial);
        State.Spatial = mode.Value != 0 ? 1 : 0;
        NotifyState();
    }

    private void OnSwitches(byte[] payload)
    {
        var values = OppoProtocol.ParseSwitchValues(payload);
        if (values is null || values.Count == 0) return;

        if (values.TryGetValue(OppoProtocol.SwitchGame, out var game))
        {
            Grant(Feature.Game);
            State.Game = game != 0 ? 1 : 0;
        }
        if (values.TryGetValue(OppoProtocol.SwitchSpatialV1, out var spatial))
        {
            Grant(Feature.Spatial);
            if (!_spatialSeen) State.Spatial = spatial != 0 ? 1 : 0;
        }
        if (values.TryGetValue(OppoProtocol.SwitchHeadTrack, out var headTrack) && headTrack != 0)
        {
            // 有头部追踪就一定支持空间音频
            Grant(Feature.Spatial);
        }
        NotifyState();
    }

    private void OnEq(byte[] payload)
    {
        var preset = OppoProtocol.ParseEq(payload);
        if (preset is null) return;
        State.EqMode = preset.Value;
        NotifyState();
    }

    private void Grant(string capability)
    {
        if (State.Features.Has(capability)) return;
        State.Features = State.Features.With(capability);
        NotifyLog($"能力发现：+{capability} → {State.Features}");
    }

    // ------------------------------------------------------------------ //
    // 控制指令
    // ------------------------------------------------------------------ //
    public override void SetNoise(int mode)
    {
        Enqueue(OppoProtocol.CmdNoiseSet(mode));
        Enqueue(OppoProtocol.CmdNoiseGet());
    }

    public override void SetSpatial(bool on)
    {
        _wantSpatial = on;
        if (_spatialV2)
        {
            Enqueue(OppoProtocol.CmdSpatialSet(on ? 1 : 0));
            Enqueue(OppoProtocol.CmdSpatialGet());
        }
        else
        {
            Enqueue(OppoProtocol.CmdSwitchSet(OppoProtocol.SwitchSpatialV1, on));
            Enqueue(OppoProtocol.CmdSwitchGet(new[] { OppoProtocol.SwitchSpatialV1 }));
        }
    }

    public override void SetGame(bool on)
    {
        Enqueue(OppoProtocol.CmdSwitchSet(OppoProtocol.SwitchGame, on));
        Enqueue(OppoProtocol.CmdSwitchGet(new[] { OppoProtocol.SwitchGame }));
    }

    public override void SetEq(int code)
    {
        Enqueue(OppoProtocol.CmdEqSet(code));
        Enqueue(OppoProtocol.CmdEqGet());
    }

    public override void Find(bool start) => Enqueue(OppoProtocol.CmdFind(start));

    public override void SetMultiConnectStatus(string mac, bool connect)
    {
        NotifyLog("OPPO 暂不支持多设备列表的断开 / 连接操作");
    }
}
