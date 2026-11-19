namespace PodsController.Devices.Huawei;

/// <summary>
/// 华为 SPP 命令号（对应 upstream openfreebuds 的 constants/spp_commands.py）。
/// </summary>
public static class HuaweiCommands
{
    public static readonly byte[] BatteryRead = { 0x01, 0x08 };
    public static readonly byte[] BatteryNotify = { 0x01, 0x27 };
    public static readonly byte[] Info = { 0x01, 0x07 };
    public static readonly byte[] InEar = { 0x2B, 0x03 };
    public static readonly byte[] AncRead = { 0x2B, 0x2A };
    public static readonly byte[] AncWrite = { 0x2B, 0x04 };
    public static readonly byte[] LowLatency = { 0x2B, 0x6C };
    public static readonly byte[] EqRead = { 0x2B, 0x4A };
    public static readonly byte[] EqWrite = { 0x2B, 0x49 };
    public static readonly byte[] DualEnabledRead = { 0x2B, 0x2F };
    public static readonly byte[] DualEnabledWrite = { 0x2B, 0x2E };
    public static readonly byte[] DualEnumerate = { 0x2B, 0x31 };
    public static readonly byte[] DualPreferredWrite = { 0x2B, 0x32 };
    public static readonly byte[] DualExecute = { 0x2B, 0x33 };
    public static readonly byte[] DualChangeEvent = { 0x2B, 0x36 };
}

/// <summary>多设备（双连）操作码（upstream OfbHuaweiDualConnCommand）。</summary>
public enum HuaweiDualCommand
{
    Connect = 1,
    Disconnect = 2,
    Unpair = 3,
    EnableAuto = 4,
    DisableAuto = 5
}

/// <summary>
/// 华为 SPP 数据包（对应 openfreebuds/driver/huawei/package.py）。
/// 帧格式：'Z' | 长度(2, BE) | 0x00 | 命令(2) | TLV… | CRC16(2, BE)
/// </summary>
public sealed class HuaweiPackage
{
    public const byte FrameStart = 0x5A;

    public byte CommandHigh { get; }
    public byte CommandLow { get; }
    public ushort CommandId => (ushort)((CommandHigh << 8) | CommandLow);

    public Dictionary<byte, byte[]> Parameters { get; } = new();

    public HuaweiPackage(byte[] command, IEnumerable<KeyValuePair<byte, byte[]>>? parameters = null)
    {
        if (command.Length != 2) throw new ArgumentException("命令号必须是 2 字节", nameof(command));
        CommandHigh = command[0];
        CommandLow = command[1];
        if (parameters is not null)
        {
            foreach (var (key, value) in parameters) Parameters[key] = value;
        }
    }

    /// <summary>读取请求：参数长度都为 0。</summary>
    public static HuaweiPackage ReadRequest(byte[] command, params int[] parameterIds)
    {
        var package = new HuaweiPackage(command);
        foreach (var id in parameterIds) package.Parameters[(byte)id] = Array.Empty<byte>();
        return package;
    }

    /// <summary>写入请求（等回包）。</summary>
    public static HuaweiPackage ChangeRequest(byte[] command, params (int Id, byte[] Value)[] parameters)
    {
        var package = new HuaweiPackage(command);
        foreach (var (id, value) in parameters) package.Parameters[(byte)id] = value;
        return package;
    }

    public byte[] FindParam(params int[] ids)
    {
        foreach (var id in ids)
        {
            if (Parameters.TryGetValue((byte)id, out var value)) return value;
        }
        return Array.Empty<byte>();
    }

    public byte[] ToBytes()
    {
        var body = new List<byte> { CommandHigh, CommandLow };
        foreach (var (id, value) in Parameters)
        {
            body.Add(id);
            body.Add((byte)value.Length);
            body.AddRange(value);
        }

        var frame = new List<byte>
        {
            FrameStart,
            (byte)((body.Count + 1) >> 8),
            (byte)((body.Count + 1) & 0xFF),
            0x00
        };
        frame.AddRange(body);

        var crc = Crc16Xmodem(frame.ToArray());
        frame.Add((byte)(crc >> 8));
        frame.Add((byte)(crc & 0xFF));
        return frame.ToArray();
    }

    public static HuaweiPackage? FromBytes(byte[] data)
    {
        if (data.Length < 8 || data[0] != FrameStart || data[3] != 0x00) return null;

        var length = (data[1] << 8) | data[2];
        if (data.Length < length + 5) return null;

        var package = new HuaweiPackage(new[] { data[4], data[5] });
        var position = 6;
        while (position + 1 < length + 3 && position + 1 < data.Length)
        {
            var type = data[position];
            var size = data[position + 1];
            position += 2;
            if (position + size > data.Length) break;
            package.Parameters[type] = data.Skip(position).Take(size).ToArray();
            position += size;
        }
        return package;
    }

    /// <summary>CRC-16/XMODEM（多项式 0x1021，与上游 crc16_xmodem 同值）。</summary>
    public static ushort Crc16Xmodem(byte[] data)
    {
        ushort crc = 0;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            }
        }
        return crc;
    }
}

/// <summary>把字节流切成完整的数据包（半包 / 粘包都要处理）。</summary>
public sealed class HuaweiFrameSplitter
{
    private readonly List<byte> _buffer = new();

    public void Reset() => _buffer.Clear();

    public List<HuaweiPackage> Offer(byte[] chunk)
    {
        var output = new List<HuaweiPackage>();
        _buffer.AddRange(chunk);

        while (true)
        {
            var start = _buffer.IndexOf(HuaweiPackage.FrameStart);
            if (start < 0)
            {
                _buffer.Clear();
                break;
            }
            if (start > 0) _buffer.RemoveRange(0, start);
            if (_buffer.Count < 4) break;

            var length = (_buffer[1] << 8) | _buffer[2];
            if (length <= 0 || length > 4096 || _buffer[3] != 0x00)
            {
                _buffer.RemoveAt(0);
                continue;
            }

            var total = length + 5;
            if (_buffer.Count < total) break;

            var frame = _buffer.GetRange(0, total).ToArray();
            _buffer.RemoveRange(0, total);

            var package = HuaweiPackage.FromBytes(frame);
            if (package is not null) output.Add(package);
        }

        return output;
    }
}
