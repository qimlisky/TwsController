using System.Runtime.InteropServices;
using Microsoft.Win32;

using PodsController.Interop;

namespace PodsController.Core;

public sealed record PairedBluetoothDevice(string Name, string Mac, bool Connected);

/// <summary>
/// 已配对 / 已连接蓝牙设备枚举（对应 Python 版 pods/btdevices.py +
/// huawei/transport.py list_paired_devices）。
///
/// 优先走 bthprops.cpl 的 BluetoothFindFirstDevice；失败（权限 / 驱动异常）时
/// 退回注册表 BTHPORT\Parameters\Devices 读取（与 Python 版一致）。
/// </summary>
public static class BluetoothEnumerator
{
    private const string BthKey = @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices";

    public static List<PairedBluetoothDevice> ListPaired()
    {
        var devices = EnumerateBthApi();
        if (devices.Count == 0)
        {
            devices = EnumerateRegistry();
        }
        return devices;
    }

    /// <summary>当前处于「已连接」状态的受支持耳机（自动识别用的输入）。</summary>
    public static List<DeviceInfo> ListConnectedEarbuds()
    {
        var output = new List<DeviceInfo>();
        foreach (var device in ListPaired())
        {
            if (!device.Connected) continue;
            if (!DeviceCatalog.TryDetect(device.Name, out var vendor, out var canonical)) continue;
            output.Add(new DeviceInfo(canonical, device.Mac, vendor));
        }
        return output;
    }

    /// <summary>
    /// 已配对且受支持的耳机（托盘「连接设备」子菜单用，Python 版三个品牌一起列）。
    /// 不要求当前已连接，连不上的也能从列表里主动发起。
    /// </summary>
    public static List<DeviceInfo> ListSupportedPaired()
    {
        var output = new List<DeviceInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in ListPaired())
        {
            if (!DeviceCatalog.TryDetect(device.Name, out var vendor, out var canonical)) continue;
            if (!seen.Add(device.Mac)) continue;
            output.Add(new DeviceInfo(canonical, device.Mac, vendor));
        }
        return output;
    }

    // ------------------------------------------------------------------ //
    private static List<PairedBluetoothDevice> EnumerateBthApi()
    {
        var output = new List<PairedBluetoothDevice>();
        IntPtr find = IntPtr.Zero;
        try
        {
            var search = new Native.BluetoothDeviceSearchParams
            {
                Size = (uint)Marshal.SizeOf<Native.BluetoothDeviceSearchParams>(),
                ReturnAuthenticated = 1,
                ReturnRemembered = 1,
                ReturnConnected = 1,
                ReturnUnknown = 0,
                IssueInquiry = 0,
                Radio = IntPtr.Zero
            };

            var info = new Native.BluetoothDeviceInfo
            {
                Size = (uint)Marshal.SizeOf<Native.BluetoothDeviceInfo>(),
                Name = ""
            };

            find = Native.BluetoothFindFirstDevice(ref search, ref info);
            if (find == IntPtr.Zero) return output;

            while (true)
            {
                var mac = Native.BluetoothAddressToMac(info.Address.Long);
                var name = string.IsNullOrWhiteSpace(info.Name) ? mac : info.Name.Trim();
                output.Add(new PairedBluetoothDevice(name, mac, info.Connected != 0));

                info = new Native.BluetoothDeviceInfo
                {
                    Size = (uint)Marshal.SizeOf<Native.BluetoothDeviceInfo>(),
                    Name = ""
                };
                if (!Native.BluetoothFindNextDevice(find, ref info)) break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Runtime($"蓝牙枚举（bthprops）失败：{ex.Message}");
        }
        finally
        {
            if (find != IntPtr.Zero)
            {
                try { Native.BluetoothFindDeviceClose(find); } catch { /* 忽略 */ }
            }
        }
        return output;
    }

    private static List<PairedBluetoothDevice> EnumerateRegistry()
    {
        var output = new List<PairedBluetoothDevice>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(BthKey);
            if (root is null) return output;

            foreach (var subName in root.GetSubKeyNames())
            {
                var plain = new string(subName.Where(char.IsLetterOrDigit).ToArray());
                if (plain.Length != 12) continue;

                var mac = string.Join(":", Enumerable.Range(0, 6)
                    .Select(i => plain.Substring(i * 2, 2).ToUpperInvariant()));

                var name = "";
                try
                {
                    using var sub = root.OpenSubKey(subName);
                    name = DecodeName(sub?.GetValue("Name"));
                }
                catch
                {
                    // 读名字需要管理员；失败就用 MAC 当名字
                }

                output.Add(new PairedBluetoothDevice(string.IsNullOrWhiteSpace(name) ? mac : name, mac, false));
            }
        }
        catch (Exception ex)
        {
            AppLog.Runtime($"蓝牙枚举（注册表）失败：{ex.Message}");
        }
        return output;
    }

    private static string DecodeName(object? raw)
    {
        switch (raw)
        {
            case byte[] bytes:
                return System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\0').Trim();
            case string text:
                try
                {
                    return System.Text.Encoding.UTF8.GetString(Convert.FromHexString(text)).TrimEnd('\0').Trim();
                }
                catch
                {
                    return text.Trim();
                }
            default:
                return "";
        }
    }
}
