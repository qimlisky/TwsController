using System.IO;

using System.Runtime.InteropServices;

namespace PodsController.Interop;

/// <summary>
/// 经典蓝牙 RFCOMM 客户端（对应 Python 版 WindowsRfcommTransport 的 ws2_32 实现）。
///
/// 华为固定走通道 1；OPPO 按 15 → 1 → 其余通道顺序逐个试，
/// 不同型号的 SDP 记录不一样，写死一条会让另一批型号连不上。
/// </summary>
public sealed class RfcommSocket : IDisposable
{
    private static readonly object WsaGate = new();
    private static bool _wsaStarted;

    private IntPtr _socket = IntPtr.Zero;

    public string Mac { get; }
    public int Channel { get; }
    public bool Connected { get; private set; }

    private RfcommSocket(string mac, int channel)
    {
        Mac = mac;
        Channel = channel;
    }

    /// <summary>按通道列表依次尝试，第一条成功即返回；全部失败抛出最后一次错误。</summary>
    public static RfcommSocket Connect(string mac, params int[] channels)
    {
        Exception? lastError = null;
        foreach (var channel in channels)
        {
            var socket = new RfcommSocket(mac, channel);
            try
            {
                socket.Open();
                return socket;
            }
            catch (Exception ex)
            {
                lastError = ex;
                socket.Dispose();
            }
        }
        throw new InvalidOperationException(
            lastError?.Message ?? "RFCOMM 连接失败：没有可用通道");
    }

    private void Open()
    {
        EnsureWsa();
        var handle = Native.socket(Native.AF_BTH, Native.SOCK_STREAM, Native.BTHPROTO_RFCOMM);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            throw new InvalidOperationException("无法创建蓝牙 socket（ws2_32.dll）");
        }

        var plain = new string(Mac.Where(char.IsLetterOrDigit).ToArray());
        if (plain.Length != 12 || !ulong.TryParse(plain, System.Globalization.NumberStyles.HexNumber, null, out var address))
        {
            Native.closesocket(handle);
            throw new ArgumentException($"无效的蓝牙地址：{Mac}");
        }

        var sockAddr = new Native.SockAddrBth
        {
            AddressFamily = Native.AF_BTH,
            BtAddr = address,
            ServiceClassId = Guid.Empty,
            Port = (uint)Channel
        };

        var size = Marshal.SizeOf<Native.SockAddrBth>();
        if (Native.connect(handle, ref sockAddr, size) != 0)
        {
            var err = Native.WSAGetLastError();
            Native.closesocket(handle);
            throw new InvalidOperationException(
                $"RFCOMM 连接失败（通道 {Channel}，WSA 错误 {err}）。请确认耳机已与 Windows 配对、" +
                "已开机且在范围内，且没有被其它设备独占控制通道。");
        }

        SetTimeout(handle, Native.SO_RCVTIMEO, 1000);
        SetTimeout(handle, Native.SO_SNDTIMEO, 1000);

        _socket = handle;
        Connected = true;
    }

    private static void SetTimeout(IntPtr handle, int option, uint milliseconds)
    {
        Native.setsockopt(handle, Native.SOL_SOCKET, option, ref milliseconds, sizeof(uint));
    }

    public void Send(byte[] data)
    {
        if (!Connected) throw new InvalidOperationException("通道未连接");
        var sentTotal = 0;
        while (sentTotal < data.Length)
        {
            var chunk = sentTotal == 0 ? data : data[sentTotal..];
            var sent = Native.send(_socket, chunk, chunk.Length, 0);
            if (sent <= 0)
            {
                var err = Native.WSAGetLastError();
                Close();
                throw new IOException($"发送失败（WSA 错误 {err}）");
            }
            sentTotal += sent;
        }
    }

    /// <summary>读一次（带 1s 超时）；超时返回空数组，连接被对端关闭或出错时主动断开。</summary>
    public byte[] Receive(int bufferSize = 2048)
    {
        if (!Connected) return Array.Empty<byte>();
        var buffer = new byte[bufferSize];
        var read = Native.recv(_socket, buffer, buffer.Length, 0);
        if (read > 0) return buffer[..read];

        if (read == 0)
        {
            // 对端正常关闭
            Close();
            return Array.Empty<byte>();
        }

        var err = Native.WSAGetLastError();
        if (err == Native.WSAETIMEDOUT) return Array.Empty<byte>(); // 1s 超时，继续轮询
        Close();
        return Array.Empty<byte>();
    }

    public void Close()
    {
        if (_socket != IntPtr.Zero)
        {
            try { Native.closesocket(_socket); } catch { /* 忽略 */ }
            _socket = IntPtr.Zero;
        }
        Connected = false;
    }

    public void Dispose() => Close();

    private static void EnsureWsa()
    {
        lock (WsaGate)
        {
            if (_wsaStarted) return;
            var data = Marshal.AllocHGlobal(400);
            try
            {
                Native.WSAStartup(0x0202, data);
                _wsaStarted = true;
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }
    }
}
