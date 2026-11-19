using System.Collections.Concurrent;

using PodsController.Core;
using PodsController.Interop;

namespace PodsController.Devices.Redmi;

/// <summary>
/// Redmi / 小米耳机会话（Redmi Buds 8 Pro 已验证）。
///
/// 协议与鉴权移植自 FallenLeeee / MiBudsController（MIT License）：
/// https://github.com/FallenLeeee/MiBudsController
/// 原始实现使用 Windows WinRT StreamSocket；这里改为本项目的原生 RFCOMM，
/// 并映射到统一的 DeviceState / EarbudSession。
/// </summary>
public sealed class RedmiSession : EarbudSession
{
    private readonly BlockingCollection<(byte Opcode, byte[] Payload)> _queue = new();

    private RfcommSocket? _socket;
    private Thread? _rxThread;
    private Thread? _txThread;
    private volatile bool _stop;

    private readonly object _sendGate = new();
    private byte _sequence;

    private TaskCompletionSource<RcspMessage>? _authResponse;
    private TaskCompletionSource<RcspMessage>? _authConfirm;

    public RedmiSession(DeviceInfo device) : base(device) { }

    // ------------------------------------------------------------------ //
    public override void Connect()
    {
        ResetReadings();
        SetConnected(false);
        SetStatus("连接中…");
        NotifyLog($"准备连接 {RedmiCatalog.DisplayName(Device.Name)}（{Device.Mac}），服务 UUID fd2d");

        _stop = false;
        _sequence = 0;
        _authResponse = null;
        _authConfirm = null;

        _rxThread = new Thread(Run)
        {
            IsBackground = true,
            Name = $"redmi-{Device.PlainMac}"
        };
        _rxThread.Start();
    }

    private void Run()
    {
        try
        {
            _socket = RfcommSocket.Connect(Device.Mac, RedmiProtocol.ControlServiceUuid);
        }
        catch (Exception ex)
        {
            NotifyError($"连接失败：{ex.Message}");
            SetStatus("连接失败");
            SetConnected(false);
            return;
        }

        var readThread = new Thread(ReadLoop) { IsBackground = true, Name = "redmi-rx" };
        readThread.Start();

        try
        {
            Authenticate();
        }
        catch (Exception ex)
        {
            // 参考项目同样允许部分型号跳过 SAFER+，不把鉴权失败直接当成连接失败。
            NotifyLog($"SAFER+ 鉴权未完成，继续尝试：{ex.Message}");
        }

        if (_stop)
        {
            return;
        }

        SetConnected(true);
        SetStatus($"已连接 · {RedmiCatalog.DisplayName(Device.Name)}");
        NotifyLog($"已连接 {Device.Name}（{Device.Mac}），协议=RCSP / SAFER+");

        _txThread = new Thread(TxLoop) { IsBackground = true, Name = "redmi-tx" };
        _txThread.Start();

        SyncState();

        var lastPoll = DateTime.UtcNow;
        while (!_stop)
        {
            Thread.Sleep(500);
            if ((DateTime.UtcNow - lastPoll).TotalSeconds >= 30)
            {
                lastPoll = DateTime.UtcNow;
                Enqueue(RcspOpcodes.GetRunInfo, PayloadBuilder.GetRunInfo());
                Enqueue(RcspOpcodes.GetDeviceInfo, PayloadBuilder.GetDeviceInfo());
                Enqueue(RcspOpcodes.GetConfig, PayloadBuilder.GetConfig(RcspConfigIds.SpatialAudioSwitch));
                Enqueue(RcspOpcodes.GetConfig, PayloadBuilder.GetConfig(RcspConfigIds.LowLatency));
            }
        }
    }

    private void Authenticate()
    {
        Thread.Sleep(500);

        byte[] random = SaferPlus.GenerateChallenge();
        var response = new TaskCompletionSource<RcspMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _authResponse = response;

        NotifyLog("[鉴权] 发送 SAFER+ 挑战 0x50");
        SendRequestNow(RcspOpcodes.AuthChallenge, PayloadBuilder.AuthChallenge(random));

        if (!response.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("等待 0x50 鉴权响应超时");
        }

        RcspMessage answer = response.Task.Result;
        if (answer.Payload.Length < 16)
        {
            throw new InvalidOperationException("鉴权响应长度不足 16 字节");
        }

        byte[] deviceAnswer = answer.Payload.Length >= 17 ? answer.Payload[^16..] : answer.Payload;
        byte[] expected = new SaferPlus().ComputeChallengeResponse(random);
        if (!deviceAnswer.AsSpan().SequenceEqual(expected))
        {
            NotifyLog("[鉴权] 设备返回自有挑战，回以 SAFER+ 响应");
            byte[] reply = new SaferPlus().ComputeChallengeResponse(deviceAnswer);
            SendResponseNow(RcspOpcodes.AuthChallenge, answer.Sequence,
                PayloadBuilder.AuthChallengeResponse(reply));
        }
        else
        {
            NotifyLog("[鉴权] 挑战校验通过");
        }

        var confirm = new TaskCompletionSource<RcspMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _authConfirm = confirm;

        NotifyLog("[鉴权] 发送确认 0x51");
        SendRequestNow(RcspOpcodes.AuthConfirm, PayloadBuilder.AuthConfirmRequest());

        if (!confirm.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("等待耳机发起的 0x51 确认超时");
        }

        NotifyLog("[鉴权] 收到耳机 0x51，握手完成");
    }

    private void SyncState()
    {
        Enqueue(RcspOpcodes.GetDeviceInfo, PayloadBuilder.GetDeviceInfo());
        Enqueue(RcspOpcodes.GetRunInfo, PayloadBuilder.GetRunInfo());

        byte[] configIds =
        {
            RcspConfigIds.AudioMode,
            RcspConfigIds.EffectStrength,
            RcspConfigIds.SpatialAudioSwitch,
            RcspConfigIds.SpatialAudioBitmap,
            RcspConfigIds.LowLatency,
            RcspConfigIds.SceneRendering,
            RcspConfigIds.SmartNoise,
            RcspConfigIds.DolbyAudioSpatialMode,
        };

        foreach (byte id in configIds)
        {
            Enqueue(RcspOpcodes.GetConfig, PayloadBuilder.GetConfig(id));
        }
    }

    // ------------------------------------------------------------------ //
    // 接收 / 发送
    // ------------------------------------------------------------------ //
    private void ReadLoop()
    {
        var parser = new FrameParser();
        try
        {
            while (!_stop)
            {
                var chunk = _socket?.Receive() ?? Array.Empty<byte>();
                if (chunk.Length == 0)
                {
                    if (_socket is { Connected: true }) continue;
                    break;
                }

                parser.Append(chunk);
                foreach (var message in parser.Drain())
                {
                    try
                    {
                        Handle(message);
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
            SetConnected(false);
            SetStatus("已断开");
        }
    }

    private void Handle(RcspMessage message)
    {
        switch (message.Opcode)
        {
            case RcspOpcodes.AuthChallenge when message.Type == RcspMessageType.Response:
                _authResponse?.TrySetResult(message);
                return;

            case RcspOpcodes.AuthChallenge:
                if (message.Payload.Length >= 16)
                {
                    byte[] challenge = message.Payload.Length >= 17 ? message.Payload[^16..] : message.Payload;
                    byte[] reply = new SaferPlus().ComputeChallengeResponse(challenge);
                    SendResponseNow(RcspOpcodes.AuthChallenge, message.Sequence,
                        PayloadBuilder.AuthChallengeResponse(reply));
                }

                return;

            case RcspOpcodes.AuthConfirm when message.Type != RcspMessageType.Response:
                SendResponseNow(RcspOpcodes.AuthConfirm, message.Sequence, PayloadBuilder.AuthConfirmResponse());
                _authConfirm?.TrySetResult(message);
                return;

            case RcspOpcodes.ReportStatus:
                ParseDeviceUpdate(message.Payload);
                SendResponseNow(RcspOpcodes.ReportStatus, message.Sequence, Array.Empty<byte>());
                return;

            case RcspOpcodes.NotifyConfig:
                ParseNotifyConfig(message.Payload);
                SendResponseNow(RcspOpcodes.NotifyConfig, message.Sequence, Array.Empty<byte>());
                return;

            case RcspOpcodes.GetDeviceInfo when message.Type == RcspMessageType.Response:
                ParseDeviceInfo(message.Payload);
                return;

            case RcspOpcodes.GetRunInfo when message.Type == RcspMessageType.Response:
                ParseRunInfo(message.Payload);
                return;

            case RcspOpcodes.GetConfig when message.Type == RcspMessageType.Response:
                ParseGetConfig(message.Payload);
                return;
        }
    }

    private void TxLoop()
    {
        try
        {
            while (!_stop)
            {
                (byte opcode, byte[] payload) item;
                try
                {
                    if (!_queue.TryTake(out item, 500)) continue;
                }
                catch (InvalidOperationException)
                {
                    return;
                }

                try
                {
                    SendRequestNow(item.opcode, item.payload);
                }
                catch (Exception ex)
                {
                    if (!_stop) NotifyError($"发送失败：{ex.Message}");
                }
            }
        }
        catch
        {
            // 退出
        }
    }

    private void Enqueue(byte opcode, byte[] payload)
    {
        if (_stop) return;
        try
        {
            _queue.Add((opcode, payload));
        }
        catch (InvalidOperationException)
        {
            // 队列已关闭
        }
    }

    private byte SendRequestNow(byte opcode, byte[] payload)
    {
        var socket = _socket;
        if (socket is null || !socket.Connected) throw new InvalidOperationException("通道未连接");

        lock (_sendGate)
        {
            byte sequence = _sequence++;
            var message = new RcspMessage(RcspMessageType.PhoneRequest, opcode, sequence, payload);
            socket.Send(message.Encode());
            return sequence;
        }
    }

    private void SendResponseNow(byte opcode, byte sequence, byte[] payload)
    {
        var socket = _socket;
        if (socket is null || !socket.Connected) return;

        try
        {
            lock (_sendGate)
            {
                socket.Send(new RcspMessage(RcspMessageType.Response, opcode, sequence, payload).Encode());
            }
        }
        catch (Exception ex)
        {
            if (!_stop) NotifyLog($"发送响应失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------------ //
    // 状态解析
    // ------------------------------------------------------------------ //
    private void ParseDeviceInfo(byte[] payload)
    {
        string? firmware1 = null;
        string? firmware2 = null;

        foreach (var entry in TlvParser.Walk(payload, 1))
        {
            switch (entry.Index)
            {
                case 0x01 when entry.Data.Length >= 4:
                    firmware1 = NibbleVersion(entry.Data[0], entry.Data[1]);
                    firmware2 = NibbleVersion(entry.Data[2], entry.Data[3]);
                    break;

                case 0x07 when entry.Data.Length >= 3:
                    State.LeftPercent = BatteryPercent(entry.Data[0]);
                    State.LeftCharging = BatteryCharging(entry.Data[0]);
                    State.RightPercent = BatteryPercent(entry.Data[1]);
                    State.RightCharging = BatteryCharging(entry.Data[1]);
                    State.CasePercent = BatteryPercent(entry.Data[2]);
                    State.CaseCharging = BatteryCharging(entry.Data[2]);
                    break;
            }
        }

        if (firmware1 is not null)
        {
            State.Firmware = firmware2 is not null && firmware2 != firmware1
                ? $"{firmware1} / {firmware2}"
                : firmware1;
        }

        NotifyState();
    }

    private void ParseRunInfo(byte[] payload)
    {
        foreach (var entry in TlvParser.Walk(payload, 1))
        {
            if (entry.Index == 0x09 && entry.Data.Length >= 1)
            {
                ApplyNoiseProtocol(entry.Data[0]);
            }
        }

        NotifyState();
    }

    private void ParseGetConfig(byte[] payload)
    {
        if (payload.Length < 3) return;

        byte configId = payload[2];
        byte[] values = payload[3..];
        ApplyConfig(configId, values);
    }

    private void ParseNotifyConfig(byte[] payload)
    {
        foreach (var entry in TlvParser.Walk(payload, 2))
        {
            if (entry.Index == RcspConfigIds.EffectStrength && entry.Data.Length >= 2)
            {
                ApplyNoiseProtocol(entry.Data[0]);
            }
            else
            {
                ApplyConfig(entry.Index, entry.Data);
            }
        }

        NotifyState();
    }

    private void ParseDeviceUpdate(byte[] payload)
    {
        foreach (var entry in TlvParser.Walk(payload, 1))
        {
            switch (entry.Index)
            {
                case 0x00 when entry.Data.Length >= 3:
                    State.LeftPercent = BatteryPercent(entry.Data[0]);
                    State.LeftCharging = BatteryCharging(entry.Data[0]);
                    State.RightPercent = BatteryPercent(entry.Data[1]);
                    State.RightCharging = BatteryCharging(entry.Data[1]);
                    State.CasePercent = BatteryPercent(entry.Data[2]);
                    State.CaseCharging = BatteryCharging(entry.Data[2]);
                    NotifyState();
                    break;

                case 0x04 when entry.Data.Length >= 1:
                    ApplyNoiseProtocol(entry.Data[0]);
                    NotifyState();
                    break;
            }
        }
    }

    private void ApplyConfig(byte configId, byte[] values)
    {
        if (values.Length < 1) return;

        switch (configId)
        {
            case RcspConfigIds.EffectStrength when values.Length >= 2:
                ApplyNoiseProtocol(values[0]);
                break;

            case RcspConfigIds.SpatialAudioSwitch:
            case RcspConfigIds.SpatialAudioBitmap:
                State.Spatial = (values[0] & 0x01) != 0 ? 1 : 0;
                break;

            case RcspConfigIds.LowLatency:
                State.Game = values[0] != 0 ? 1 : 0;
                break;
        }
    }

    private void ApplyNoiseProtocol(byte raw)
    {
        // 协议：0 关闭 / 1 降噪 / 2 通透；UI：0 降噪 / 1 关闭 / 2 通透。
        State.NoiseMode = raw switch
        {
            RedmiAncModes.NoiseCancelling => 0,
            RedmiAncModes.Off => 1,
            RedmiAncModes.Transparency => 2,
            _ => -1
        };
    }

    private static int BatteryPercent(byte value) => value == 0xFF ? -1 : value & 0x7F;

    private static bool BatteryCharging(byte value) => value != 0xFF && (value & 0x80) != 0;

    private static string NibbleVersion(byte high, byte low) =>
        $"{(high >> 4) & 0xF}.{high & 0xF}.{(low >> 4) & 0xF}.{low & 0xF}";

    // ------------------------------------------------------------------ //
    // 控制
    // ------------------------------------------------------------------ //
    public override void SetNoise(int mode)
    {
        byte protocol = mode switch
        {
            0 => RedmiAncModes.NoiseCancelling,
            1 => RedmiAncModes.Off,
            2 => RedmiAncModes.Transparency,
            _ => RedmiAncModes.Off
        };

        Enqueue(RcspOpcodes.SetAnc, PayloadBuilder.SetAncMode(protocol));
        Enqueue(RcspOpcodes.GetRunInfo, PayloadBuilder.GetRunInfo());
    }

    public override void SetSpatial(bool on)
    {
        // 官方 DeviceConfigSpatialAudio：bit0 开关 + bit1 默认低延迟偏好。
        byte bitmap = on ? (byte)0x03 : (byte)0x00;
        Enqueue(RcspOpcodes.SetConfig, PayloadBuilder.SetConfig(RcspConfigIds.SpatialAudioSwitch, bitmap));
        Enqueue(RcspOpcodes.SetConfig, PayloadBuilder.SetConfig(RcspConfigIds.SpatialAudioBitmap, bitmap));
        Enqueue(RcspOpcodes.GetConfig, PayloadBuilder.GetConfig(RcspConfigIds.SpatialAudioSwitch));
    }

    public override void SetGame(bool on)
    {
        Enqueue(RcspOpcodes.SetConfig,
            PayloadBuilder.SetConfig(RcspConfigIds.LowLatency, on ? (byte)1 : (byte)0));
        Enqueue(RcspOpcodes.GetConfig, PayloadBuilder.GetConfig(RcspConfigIds.LowLatency));
    }

    public override void SetEq(int code) => NotifyLog("Redmi 暂未接入 EQ 写入");

    public override void Find(bool start) => NotifyLog("Redmi 暂未接入查找耳机");

    public override void Disconnect()
    {
        _stop = true;
        try { _socket?.Dispose(); } catch { /* 忽略 */ }
        _socket = null;

        try { _queue.CompleteAdding(); } catch { /* 忽略 */ }
        try { _rxThread?.Join(2000); } catch { /* 忽略 */ }
        try { _txThread?.Join(1000); } catch { /* 忽略 */ }
        _rxThread = null;
        _txThread = null;

        SetConnected(false);
        SetStatus("已断开");
        State.LeftPercent = -1; State.RightPercent = -1; State.CasePercent = -1;
        State.Spatial = -1; State.Game = -1;
        NotifyState();
    }
}
