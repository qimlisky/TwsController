using System.Buffers.Binary;
using System.Text;

namespace PodsController.Devices.Oppo;

/// <summary>
/// OPPO / 一加 / realme 耳机控制协议（OPO v1，经典蓝牙 RFCOMM）。
/// 逐字移植 oppo/oppo_protocol.py。
///
/// 链路层：AA | LEN(变长 base-128) | CTRL | 00 | [FSN] | 应用层数据
/// 应用层：CMD(u16 LE) | SEQ(u8) | PLEN(u16 LE) | payload
/// 无校验、无加密；响应 CMD = 请求 CMD | 0x8000，并回显请求的 SEQ。
/// </summary>
public static class OppoProtocol
{
    public const byte FrameStart = 0xAA;

    // ---- 查询组 0x01xx ----
    public const int IdHandshake = 0x0100;
    public const int IdProductId = 0x0103;
    public const int IdFirmware = 0x0105;
    public const int IdBattery = 0x0106;
    public const int IdWear = 0x0109;
    public const int IdNoiseGet = 0x010C;
    public const int IdSwitchGet = 0x010D;
    public const int IdEqGet = 0x010F;
    public const int IdSpatialGet = 0x012A;

    // ---- 设置组 0x04xx ----
    public const int IdFind = 0x0400;
    public const int IdSwitchSet = 0x0403;
    public const int IdNoiseSet = 0x0404;
    public const int IdEqSet = 0x0406;
    public const int IdSpatialSet = 0x0422;

    // ---- 事件订阅 ----
    public const int IdEventsGet = 0x0200;
    public const int IdEventsSub = 0x0205;
    public const int IdHostTime = 0x8500;

    public static readonly byte[] SubscribeEvents = { 0x01, 0x02, 0x03 };

    // 降噪 protocolIndex
    public const int NoiseOff = 0;
    public const int NoiseAnc = 1;
    public const int NoiseTransparency = 2;

    // 功能开关 ID
    public const byte SwitchWearDetect = 4;
    public const byte SwitchGame = 6;
    public const byte SwitchDual = 17;
    public const byte SwitchSpatialV1 = 27;
    public const byte SwitchHeadTrack = 59;

    /// <summary>OPPO protocolIndex → UI 三档（0 降噪 / 1 关闭 / 2 通透）。</summary>
    private static readonly Dictionary<int, int> IndexToUi = new()
    {
        [0] = 1, [3] = 1,
        [1] = 0, [4] = 0, [5] = 0, [6] = 0, [7] = 0, [11] = 0, [12] = 0, [13] = 0, [14] = 0,
        [2] = 2, [8] = 2,
    };

    /// <summary>UI 三档 → 要下发的 protocolIndex（用顶层节点，让耳机自己挑档位）。</summary>
    private static readonly Dictionary<int, int> UiToIndex = new()
    {
        [0] = NoiseAnc,
        [1] = NoiseOff,
        [2] = NoiseTransparency,
    };

    public static string StatusText(int code) => code switch
    {
        0x00 => "成功",
        0x09 => "耳机忙碌或被其它设备占用",
        0x0E => "需要先戴上耳机",
        0x0F => "耳机忙碌",
        0x10 => "耳机电量过低",
        _ => $"耳机返回状态 0x{code:02X}"
    };

    // ------------------------------------------------------------------ //
    // 编解码
    // ------------------------------------------------------------------ //
    public static byte[] EncodeVarint(int value)
    {
        var output = new List<byte>();
        while (true)
        {
            var b = value & 0x7F;
            value >>= 7;
            if (value != 0)
            {
                output.Add((byte)(b | 0x80));
            }
            else
            {
                output.Add((byte)b);
                return output.ToArray();
            }
        }
    }

    private static int? DecodeVarint(byte[] buffer, int position, out int next)
    {
        var value = 0;
        var shift = 0;
        next = position;
        while (position < buffer.Length)
        {
            var b = buffer[position];
            position++;
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                next = position;
                return value;
            }
            shift += 7;
            if (shift > 21)
            {
                next = position;
                return null;
            }
        }
        next = position;
        return null;
    }

    /// <summary>组一条完整帧（单帧，CTRL=0，无校验）。</summary>
    public static byte[] Frame(int cmd, byte seq = 0, byte[]? payload = null)
    {
        payload ??= Array.Empty<byte>();
        var app = new byte[5 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(app.AsSpan(0, 2), (ushort)(cmd & 0xFFFF));
        app[2] = seq;
        BinaryPrimitives.WriteUInt16LittleEndian(app.AsSpan(3, 2), (ushort)payload.Length);
        payload.CopyTo(app, 5);

        var body = new byte[2 + app.Length];
        body[0] = 0x00;
        body[1] = 0x00;
        app.CopyTo(body, 2);

        var header = EncodeVarint(body.Length);
        var frame = new byte[1 + header.Length + body.Length];
        frame[0] = FrameStart;
        header.CopyTo(frame, 1);
        body.CopyTo(frame, 1 + header.Length);
        return frame;
    }

    // ------------------------------------------------------------------ //
    // 命令构造
    // ------------------------------------------------------------------ //
    public static byte[] CmdHandshake() => Frame(IdHandshake, 0x00);
    public static byte[] CmdProductId() => Frame(IdProductId, 0x01);
    public static byte[] CmdQueryEvents() => Frame(IdEventsGet, 0x00);
    public static byte[] CmdSubscribe() => Frame(IdEventsSub, 0x00, new byte[] { (byte)SubscribeEvents.Length }.Concat(SubscribeEvents).ToArray());
    public static byte[] CmdFirmware() => Frame(IdFirmware, 0x02);
    public static byte[] CmdBattery() => Frame(IdBattery, 0xF0);
    public static byte[] CmdWear() => Frame(IdWear, 0x03);
    public static byte[] CmdNoiseGet() => Frame(IdNoiseGet, 0x04, new byte[] { 0x01, 0x01 });

    public static byte[] CmdNoiseSet(int uiMode)
    {
        var index = UiToIndex.TryGetValue(uiMode, out var value) ? value : NoiseAnc;
        var maskLength = index / 8 + 1;
        var mask = new byte[maskLength];
        for (var i = 0; i < 32; i++)
        {
            if (((index >> i) & 1) != 0)
            {
                mask[i / 8] |= (byte)(1 << (i % 8));
            }
        }
        return Frame(IdNoiseSet, 0x05, new byte[] { 0x01, 0x01 }.Concat(mask).ToArray());
    }

    public static byte[] CmdSpatialGet() => Frame(IdSpatialGet, 0x06);
    public static byte[] CmdSpatialSet(int mode) => Frame(IdSpatialSet, 0x07, new[] { (byte)(mode & 0x03) });
    public static byte[] CmdEqGet() => Frame(IdEqGet, 0x08);
    public static byte[] CmdEqSet(int code) => Frame(IdEqSet, 0x09, new[] { (byte)(code & 0xFF) });

    public static byte[] CmdSwitchGet(byte[]? ids = null)
    {
        ids ??= new[]
        {
            SwitchWearDetect, SwitchGame, SwitchDual, SwitchSpatialV1, SwitchHeadTrack
        };
        return Frame(IdSwitchGet, 0x0A, new[] { (byte)ids.Length }.Concat(ids).ToArray());
    }

    public static byte[] CmdSwitchSet(byte switchId, bool on) =>
        Frame(IdSwitchSet, 0x0B, new[] { (byte)(switchId & 0xFF), on ? (byte)1 : (byte)0 });

    public static byte[] CmdFind(bool start) => Frame(IdFind, 0x0C, new[] { start ? (byte)1 : (byte)0 });

    public static byte[] CmdHostTime()
    {
        var timestamp = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = new byte[5];
        payload[0] = 0x00;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1, 4), timestamp);
        return Frame(IdHostTime, 0x00, payload);
    }

    /// <summary>连上后的初始化序列（顺序与 EncoDeck 在 X4 上验证的一致）。</summary>
    public static List<byte[]> InitialCommands() => new()
    {
        CmdHandshake(),
        CmdProductId(),
        CmdQueryEvents(),
        CmdSubscribe(),
        CmdFirmware(),
        CmdBattery(),
        CmdWear(),
        CmdNoiseGet(),
        CmdEqGet(),
        CmdSpatialGet(),
        CmdSwitchGet()
    };

    // ------------------------------------------------------------------ //
    // 解析
    // ------------------------------------------------------------------ //
    /// <summary>在 payload 里找 `[n]{a,b}×n` 结构，容忍前面 1~3 字节的状态 / 子类型。</summary>
    public static List<(int A, int B)> ScanPairs(byte[] payload)
    {
        var result = new List<(int, int)>();
        for (var offset = 0; offset < Math.Min(4, payload.Length); offset++)
        {
            var n = payload[offset];
            if (n is <= 0 or > 8) continue;
            if (offset + 1 + 2 * n > payload.Length) continue;

            var pairs = new List<(int, int)>();
            var ok = true;
            for (var k = 0; k < n; k++)
            {
                var a = payload[offset + 1 + 2 * k];
                var b = payload[offset + 2 + 2 * k];
                if (a is not (1 or 2 or 3))
                {
                    ok = false;
                    break;
                }
                pairs.Add((a, b));
            }
            if (ok && pairs.Count > 0) return pairs;
        }
        return result;
    }

    public sealed class Battery
    {
        public int? Left { get; set; }
        public bool LeftCharging { get; set; }
        public int? Right { get; set; }
        public bool RightCharging { get; set; }
        public int? Case { get; set; }
        public bool CaseCharging { get; set; }
    }

    public static Battery? ParseBattery(byte[] payload)
    {
        var pairs = ScanPairs(payload);
        if (pairs.Count == 0) return null;

        var battery = new Battery();
        foreach (var (component, raw) in pairs)
        {
            var percent = Math.Min(raw & 0x7F, 100);
            var charging = (raw & 0x80) != 0;
            switch (component)
            {
                case 1: battery.Left = percent; battery.LeftCharging = charging; break;
                case 2: battery.Right = percent; battery.RightCharging = charging; break;
                case 3: battery.Case = percent; battery.CaseCharging = charging; break;
            }
        }
        return battery;
    }

    /// <summary>返回 (left, right) 文本：worn / in_case / removed / unknown。</summary>
    public static (string Left, string Right)? ParseWear(byte[] payload)
    {
        if (payload.Length == 0) return null;

        var body = payload[0] == 0 ? payload[1..] : payload;
        if (body.Length == 0) return null;

        var pairs = ScanPairs(payload);
        var states = new Dictionary<int, string>();
        if (pairs.Count > 0)
        {
            foreach (var (component, flags) in pairs)
            {
                states[component] = FlagsToState(flags);
            }
        }
        else if (body.Length >= 2)
        {
            var state = FlagsToState(body[^1]);
            states[1] = state;
            states[2] = state;
        }
        else
        {
            var state = FlagsToState(body[0]);
            states[1] = state;
            states[2] = state;
        }

        if (states.Count == 0) return null;
        return (states.GetValueOrDefault(1, "unknown"), states.GetValueOrDefault(2, "unknown"));
    }

    private static string FlagsToState(int flags) =>
        (flags & 0x02) != 0 ? "worn" : (flags & 0x01) == 0 ? "in_case" : "removed";

    public static int? ParseNoise(byte[] payload)
    {
        if (payload.Length < 3 || payload[0] != 0) return null;
        var mask = payload[3..];
        if (mask.Length == 0) return null;

        var value = 0;
        for (var i = 0; i < mask.Length; i++) value |= mask[i] << (8 * i);
        if (value == 0) return null;

        if ((value & (1 << NoiseAnc)) != 0) return 0;
        if ((value & (1 << NoiseTransparency)) != 0) return 2;

        var index = LowestBitIndex(value);
        return IndexToUi.TryGetValue(index, out var ui) ? ui : 1;
    }

    /// <summary>耳机上报的原始 protocolIndex（用于判断有无 ANC）。</summary>
    public static int? ParseNoiseIndex(byte[] payload)
    {
        if (payload.Length < 4 || payload[0] != 0) return null;
        var value = 0;
        for (var i = 3; i < payload.Length; i++) value |= payload[i] << (8 * (i - 3));
        if (value == 0) return null;
        return LowestBitIndex(value);
    }

    public static int? ParseSpatial(byte[] payload)
    {
        if (payload.Length < 2 || payload[0] != 0) return null;
        return payload[1];
    }

    public static int? ParseEq(byte[] payload)
    {
        if (payload.Length < 2 || payload[0] != 0) return null;
        return payload[1];
    }

    public static Dictionary<byte, byte>? ParseSwitchValues(byte[] payload)
    {
        if (payload.Length >= 2)
        {
            var n = payload[1];
            if (n > 0 && n <= 16 && 2 + 2 * n <= payload.Length)
            {
                var map = new Dictionary<byte, byte>();
                for (var i = 0; i < n; i++)
                {
                    map[payload[2 + 2 * i]] = payload[3 + 2 * i];
                }
                return map;
            }
        }

        var pairs = ScanPairs(payload);
        if (pairs.Count > 0)
        {
            var map = new Dictionary<byte, byte>();
            foreach (var (a, b) in pairs) map[(byte)a] = (byte)b;
            return map;
        }
        return null;
    }

    public static string? ParseFirmware(byte[] payload)
    {
        if (payload.Length < 3 || payload[0] != 0) return null;

        var n = payload[1];
        var raw = n > 0 && n <= payload.Length - 2 ? payload[2..(2 + n)] : payload[2..];
        var text = Encoding.UTF8.GetString(raw).Trim('\0', ' ');
        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToArray();
        if (parts.Length == 0) return null;

        var versions = new List<string>();
        for (var i = 0; i + 2 < parts.Length; i += 3) versions.Add(parts[i + 2]);
        if (versions.Count == 0) return text;
        return versions.Distinct().Count() == 1 ? versions[0] : string.Join(" / ", versions);
    }

    private static int LowestBitIndex(int value)
    {
        var index = 0;
        while ((value & 1) == 0 && index < 31)
        {
            value >>= 1;
            index++;
        }
        return index;
    }
}

/// <summary>OPPO 帧对象（对应 oppo_protocol.Frame）。</summary>
public sealed class OppoFrame
{
    public required int Cmd { get; init; }
    public required byte Seq { get; init; }
    public required byte[] Payload { get; init; }
    public int Status => Payload.Length > 0 ? Payload[0] : -1;

    public int? ResponseOf => (Cmd & 0x8000) != 0 ? Cmd & 0x7FFF : null;
}

/// <summary>半包 / 粘包 / 非法头重同步。</summary>
public sealed class OppoDecoder
{
    private readonly List<byte> _buffer = new();

    public void Reset() => _buffer.Clear();

    public List<OppoFrame> Offer(byte[] chunk)
    {
        var output = new List<OppoFrame>();
        _buffer.AddRange(chunk);

        while (true)
        {
            var start = _buffer.IndexOf(OppoProtocol.FrameStart);
            if (start < 0)
            {
                _buffer.Clear();
                break;
            }
            if (start > 0) _buffer.RemoveRange(0, start);
            if (_buffer.Count < 2) break;

            var length = DecodeLength(_buffer, out var headerSize);
            if (length is null || length <= 0 || length > 4096)
            {
                _buffer.RemoveAt(0);
                continue;
            }

            if (_buffer.Count < headerSize + length.Value) break;

            var body = _buffer.GetRange(headerSize, length.Value).ToArray();
            _buffer.RemoveRange(0, headerSize + length.Value);

            var frame = ParseBody(body);
            if (frame is not null) output.Add(frame);
        }

        return output;
    }

    /// <summary>解码从下标 1 开始的变长长度；headerSize 是「0xAA + 长度字节」的总字节数。</summary>
    private static int? DecodeLength(List<byte> buffer, out int headerSize)
    {
        var value = 0;
        var shift = 0;
        var position = 1;
        headerSize = 1;

        while (position < buffer.Count)
        {
            var b = buffer[position];
            position++;
            value |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                headerSize = position;
                return value;
            }
            shift += 7;
            if (shift > 21) break;
        }

        headerSize = position;
        return null;
    }

    private static OppoFrame? ParseBody(byte[] body)
    {
        if (body.Length < 2) return null;
        if ((body[0] & 0x03) != 0) return null; // 分片帧直接丢弃

        var app = body[2..];
        if (app.Length < 5) return null;

        var cmd = BinaryPrimitives.ReadUInt16LittleEndian(app.AsSpan(0, 2));
        var seq = app[2];
        var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(app.AsSpan(3, 2));
        var payload = app.Length >= 5 + payloadLength
            ? app[5..(5 + payloadLength)]
            : app[5..];

        return new OppoFrame { Cmd = cmd, Seq = seq, Payload = payload };
    }
}
