using System.Security.Cryptography;

namespace PodsController.Devices.Redmi;

/// <summary>
/// 小米 / Redmi 耳机 RCSP 协议常量。
///
/// 移植自 FallenLeeee / MiBudsController（MIT License）：
/// https://github.com/FallenLeeee/MiBudsController
/// 仅保留协议层，WinUI 部分未使用。
/// </summary>
internal static class RedmiProtocol
{
    public static readonly Guid ControlServiceUuid = new("0000fd2d-0000-1000-8000-00805f9b34fb");
}

/// <summary>RCSP 消息方向。</summary>
internal enum RcspMessageType : byte
{
    PhoneRequest = 0xC4,
    Response = 0x04,
    EarbudsRequest = 0xC0,
    EarbudsNotify = 0xC7,
}

internal static class RcspMessageTypeExtensions
{
    public static bool IsRequest(this RcspMessageType type) => ((byte)type & 0x40) != 0;
}

internal static class RcspOpcodes
{
    public const byte GetDeviceInfo = 0x02;
    public const byte SetAnc = 0x08;
    public const byte GetRunInfo = 0x09;
    public const byte ReportStatus = 0x0E;
    public const byte AuthChallenge = 0x50;
    public const byte AuthConfirm = 0x51;
    public const byte SetConfig = 0xF2;
    public const byte GetConfig = 0xF3;
    public const byte NotifyConfig = 0xF4;
}

internal static class RcspConfigIds
{
    public const byte AudioMode = 0x01;
    public const byte EffectStrength = 0x0B;
    public const byte SpatialAudioSwitch = 0x1D;
    public const byte SpatialAudioBitmap = 0x1E;
    public const byte LowLatency = 0x2F;
    public const byte SceneRendering = 0x36;
    public const byte SmartNoise = 0x66;
    public const byte DolbyAudioSpatialMode = 0x68;
}

internal static class RedmiAncModes
{
    public const byte Off = 0x00;
    public const byte NoiseCancelling = 0x01;
    public const byte Transparency = 0x02;
}

/// <summary>RCSP 帧编解码。</summary>
internal sealed class RcspMessage
{
    private const byte Header0 = 0xFE;
    private const byte Header1 = 0xDC;
    private const byte Header2 = 0xBA;
    private const byte Trailer = 0xEF;

    public RcspMessage(RcspMessageType type, byte opcode, byte sequence, byte[] payload)
    {
        Type = type;
        Opcode = opcode;
        Sequence = sequence;
        Payload = payload;
    }

    public RcspMessageType Type { get; }
    public byte Opcode { get; }
    public byte Sequence { get; }
    public byte[] Payload { get; }

    public byte[] Encode()
    {
        bool isRequest = Type.IsRequest();
        int payloadLength = Payload.Length + (isRequest ? 1 : 2);
        var frame = new byte[8 + payloadLength];
        frame[0] = Header0;
        frame[1] = Header1;
        frame[2] = Header2;
        frame[3] = (byte)Type;
        frame[4] = Opcode;
        frame[5] = (byte)(payloadLength >> 8);
        frame[6] = (byte)payloadLength;

        int offset = 7;
        if (!isRequest)
        {
            frame[offset++] = 0x00;
        }

        frame[offset++] = Sequence;
        Payload.CopyTo(frame, offset);
        frame[^1] = Trailer;
        return frame;
    }

    public static RcspMessage Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 9)
        {
            throw new ArgumentException("Frame is too short.", nameof(frame));
        }

        if (frame[0] != Header0 || frame[1] != Header1 || frame[2] != Header2)
        {
            throw new ArgumentException("Frame header is invalid.", nameof(frame));
        }

        if (frame[^1] != Trailer)
        {
            throw new ArgumentException("Frame trailer is invalid.", nameof(frame));
        }

        var type = (RcspMessageType)frame[3];
        bool isRequest = type.IsRequest();
        int payloadLength = (frame[5] << 8) | frame[6];
        if (frame.Length != 8 + payloadLength)
        {
            throw new ArgumentException("Frame length does not match declared payload length.", nameof(frame));
        }

        int payloadOffset = 7 + (isRequest ? 1 : 2);
        byte sequence = frame[payloadOffset - 1];
        byte[] payload = frame[payloadOffset..^1].ToArray();
        return new RcspMessage(type, frame[4], sequence, payload);
    }
}

/// <summary>RCSP 流分帧：处理粘包、半包和长度异常。</summary>
internal sealed class FrameParser
{
    private const int MaxBufferSize = 4096;
    private readonly List<byte> _buffer = new();

    public void Append(ReadOnlySpan<byte> data)
    {
        _buffer.AddRange(data);
        if (_buffer.Count > MaxBufferSize)
        {
            _buffer.RemoveRange(0, _buffer.Count - MaxBufferSize);
        }
    }

    public IReadOnlyList<RcspMessage> Drain()
    {
        var messages = new List<RcspMessage>();
        while (true)
        {
            int start = FindHeader();
            if (start < 0)
            {
                _buffer.Clear();
                break;
            }

            if (start > 0)
            {
                _buffer.RemoveRange(0, start);
            }

            if (_buffer.Count < 7)
            {
                break;
            }

            int payloadLength = (_buffer[5] << 8) | _buffer[6];
            int totalLength = 8 + payloadLength;
            if (totalLength > MaxBufferSize)
            {
                _buffer.RemoveAt(0);
                continue;
            }

            if (_buffer.Count < totalLength)
            {
                break;
            }

            if (_buffer[totalLength - 1] != 0xEF)
            {
                _buffer.RemoveAt(0);
                continue;
            }

            var frame = _buffer.GetRange(0, totalLength).ToArray();
            _buffer.RemoveRange(0, totalLength);
            messages.Add(RcspMessage.Decode(frame));
        }

        return messages;
    }

    private int FindHeader()
    {
        for (int i = 0; i < _buffer.Count - 2; i++)
        {
            if (_buffer[i] == 0xFE && _buffer[i + 1] == 0xDC && _buffer[i + 2] == 0xBA)
            {
                return i;
            }
        }

        return -1;
    }
}

internal static class PayloadBuilder
{
    public static byte[] GetDeviceInfo() => new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
    public static byte[] GetRunInfo() => new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
    public static byte[] SetAncMode(byte mode) => new byte[] { 0x02, 0x04, mode };
    public static byte[] GetConfig(byte configId) => new byte[] { 0x00, configId };

    public static byte[] SetConfig(byte configId, params byte[] values)
    {
        var payload = new byte[values.Length + 3];
        payload[0] = (byte)(values.Length + 2);
        payload[1] = 0x00;
        payload[2] = configId;
        values.CopyTo(payload, 3);
        return payload;
    }

    public static byte[] AuthChallenge(byte[] random)
    {
        if (random.Length != 16) throw new ArgumentException("Challenge must be 16 bytes.", nameof(random));
        var payload = new byte[17];
        payload[0] = 0x01;
        random.CopyTo(payload, 1);
        return payload;
    }

    public static byte[] AuthChallengeResponse(byte[] response)
    {
        if (response.Length != 16) throw new ArgumentException("Response must be 16 bytes.", nameof(response));
        var payload = new byte[17];
        payload[0] = 0x01;
        response.CopyTo(payload, 1);
        return payload;
    }

    public static byte[] AuthConfirmRequest() => new byte[] { 0x01, 0x00 };
    public static byte[] AuthConfirmResponse() => new byte[] { 0x01 };
}

internal readonly record struct TlvEntry(byte Index, byte[] Data);

internal static class TlvParser
{
    public static IReadOnlyList<TlvEntry> Walk(ReadOnlySpan<byte> payload, int indexOffset)
    {
        var entries = new List<TlvEntry>();
        int i = 0;
        while (i + 1 < payload.Length)
        {
            int length = payload[i];
            if (length <= indexOffset) break;

            int indexPosition = i + indexOffset;
            if (indexPosition >= payload.Length) break;

            int dataStart = indexPosition + 1;
            int dataLength = length - indexOffset;
            if (dataStart + dataLength > payload.Length) break;

            entries.Add(new TlvEntry(payload[indexPosition], payload.Slice(dataStart, dataLength).ToArray()));
            i += length + 1;
        }

        return entries;
    }
}

/// <summary>小米 SAFER+ 变体：128 位密钥、8 轮加密。</summary>
internal sealed class SaferPlus
{
    private const int Pattern = 0x9999;
    private const int BlockSize = 16;

    private static readonly byte[] Sequence =
    {
        0x11, 0x22, 0x33, 0x33, 0x22, 0x11, 0x11, 0x22,
        0x33, 0x33, 0x22, 0x11, 0x11, 0x22, 0x33, 0x33,
    };

    private static readonly int[][] Coefficients =
    {
        new[] { 2, 1, 1, 1, 4, 2, 1, 1, 2, 2, 4, 2, 4, 4, 16, 8 },
        new[] { 2, 1, 1, 1, 4, 2, 1, 1, 1, 1, 2, 1, 2, 2, 8, 4 },
        new[] { 1, 1, 4, 2, 2, 2, 4, 2, 16, 8, 4, 4, 2, 1, 1, 1 },
        new[] { 1, 1, 4, 2, 1, 1, 2, 1, 8, 4, 2, 2, 2, 1, 1, 1 },
        new[] { 16, 8, 2, 2, 4, 2, 4, 4, 1, 1, 4, 2, 1, 1, 2, 1 },
        new[] { 8, 4, 1, 1, 2, 1, 2, 2, 1, 1, 4, 2, 1, 1, 2, 1 },
        new[] { 2, 2, 4, 2, 4, 4, 16, 8, 2, 1, 1, 1, 4, 2, 1, 1 },
        new[] { 1, 1, 2, 1, 2, 2, 8, 4, 2, 1, 1, 1, 4, 2, 1, 1 },
        new[] { 4, 2, 4, 4, 16, 8, 2, 2, 1, 1, 2, 1, 1, 1, 4, 2 },
        new[] { 2, 1, 2, 2, 8, 4, 1, 1, 1, 1, 2, 1, 1, 1, 4, 2 },
        new[] { 4, 4, 16, 8, 1, 1, 2, 1, 4, 2, 1, 1, 4, 2, 2, 2 },
        new[] { 2, 2, 8, 4, 1, 1, 2, 1, 4, 2, 1, 1, 2, 1, 1, 1 },
        new[] { 1, 1, 2, 1, 1, 1, 4, 2, 4, 4, 16, 8, 2, 2, 4, 2 },
        new[] { 1, 1, 2, 1, 1, 1, 4, 2, 2, 2, 8, 4, 1, 1, 2, 1 },
        new[] { 4, 2, 1, 1, 2, 1, 1, 1, 4, 2, 2, 2, 16, 8, 4, 4 },
        new[] { 4, 2, 1, 1, 2, 1, 1, 1, 2, 1, 1, 1, 8, 4, 2, 2 },
    };

    private readonly byte[][] _biasMatrix = new byte[16][];
    private readonly byte[] _expTab = new byte[256];
    private readonly byte[] _logTab = new byte[256];

    public SaferPlus()
    {
        GenerateBiasMatrix();
        GenerateExpTab();
        GenerateLogTab();
    }

    public static byte[] GenerateChallenge()
    {
        var challenge = new byte[BlockSize];
        RandomNumberGenerator.Fill(challenge);
        return challenge;
    }

    public byte[] ComputeChallengeResponse(byte[] challenge)
    {
        byte[][] keys = KeySchedule((byte[])challenge.Clone());
        return Encrypt(Sequence, keys);
    }

    private static bool BitSet(int index) => ((1 << index) & Pattern) != 0;

    private void GenerateBiasMatrix()
    {
        for (int i = 0; i < 16; i++)
        {
            var biasVec = new byte[16];
            for (int j = 0; j < 16; j++)
            {
                int exponent = 17 * (i + 2) + (j + 1);
                int inner = ModPow(45, exponent, 257);
                int value = ModPow(45, inner, 257);
                biasVec[j] = (byte)(value == 256 ? 0 : value);
            }

            _biasMatrix[i] = biasVec;
        }
    }

    private void GenerateExpTab()
    {
        for (int i = 0; i < 256; i++)
        {
            int value = ModPow(45, i, 257);
            _expTab[i] = (byte)(i == 128 ? 0 : value);
        }
    }

    private void GenerateLogTab()
    {
        for (int i = 0; i < 256; i++)
        {
            if (i == 0)
            {
                _logTab[i] = 128;
                continue;
            }

            int value = ModPow(45, i, 257);
            if (value != 256) _logTab[value] = (byte)i;
        }
    }

    private byte[][] KeySchedule(byte[] keyInit)
    {
        keyInit[15] ^= 6;

        var keys = new byte[17][];
        keys[0] = keyInit;

        var register = new byte[17];
        keyInit.CopyTo(register, 0);
        byte xor = 0;
        foreach (byte b in keyInit) xor ^= b;
        register[16] = xor;

        for (int keyIdx = 1; keyIdx < keys.Length; keyIdx++)
        {
            for (int i = 0; i < 17; i++) register[i] = RotateLeft5(register[i]);

            var keyI = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                keyI[i] = (byte)(register[(keyIdx + i) % 17] + _biasMatrix[keyIdx - 1][i]);
            }

            keys[keyIdx] = keyI;
        }

        return keys;
    }

    private static byte RotateLeft5(byte value)
    {
        int v = value & 0xFF;
        return (byte)(((v >> 5) | (v << 3)) & 0xFF);
    }

    private byte[] Encrypt(byte[] plaintext, byte[][] keys)
    {
        var ciphertext = (byte[])plaintext.Clone();
        for (int round = 0; round < 8; round++)
        {
            if (round == 2)
            {
                for (int i = 0; i < BlockSize; i++)
                {
                    ciphertext[i] = BitSet(i)
                        ? (byte)(ciphertext[i] ^ plaintext[i])
                        : (byte)(ciphertext[i] + plaintext[i]);
                }
            }

            for (int i = 0; i < BlockSize; i++)
            {
                ciphertext[i] = BitSet(i)
                    ? (byte)(ciphertext[i] ^ keys[round * 2][i])
                    : (byte)(ciphertext[i] + keys[round * 2][i]);
            }

            for (int i = 0; i < BlockSize; i++)
            {
                ciphertext[i] = BitSet(i)
                    ? _expTab[ciphertext[i] & 0xFF]
                    : _logTab[ciphertext[i] & 0xFF];
            }

            for (int i = 0; i < BlockSize; i++)
            {
                ciphertext[i] = BitSet(i)
                    ? (byte)(keys[round * 2 + 1][i] + ciphertext[i])
                    : (byte)(keys[round * 2 + 1][i] ^ ciphertext[i]);
            }

            var copy = (byte[])ciphertext.Clone();
            for (int i = 0; i < BlockSize; i++)
            {
                byte sum = 0;
                for (int j = 0; j < BlockSize; j++)
                {
                    sum = (byte)(sum + (byte)(Coefficients[i][j] * copy[j]));
                }

                ciphertext[i] = sum;
            }
        }

        for (int i = 0; i < BlockSize; i++)
        {
            ciphertext[i] = BitSet(i)
                ? (byte)(keys[16][i] ^ ciphertext[i])
                : (byte)(keys[16][i] + ciphertext[i]);
        }

        return ciphertext;
    }

    private static int ModPow(int value, int exponent, int modulus)
    {
        long result = 1;
        long baseValue = value % modulus;
        while (exponent > 0)
        {
            if ((exponent & 1) == 1) result = (result * baseValue) % modulus;
            baseValue = (baseValue * baseValue) % modulus;
            exponent >>= 1;
        }

        return (int)result;
    }
}
