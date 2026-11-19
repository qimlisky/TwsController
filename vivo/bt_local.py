"""本机蓝牙设备连接 / 断开（Windows 标准接口，纯 ctypes，零第三方依赖）。

⚠️ 重要说明（请务必阅读）
----------------------------------------------------------------------
本模块走的是 **Windows 官方蓝牙 API**（bthprops.cpl / bluetoothapis），
通过「启用 / 禁用某台已配对设备的蓝牙服务(如 A2DP/HFP/AVRCP)」来实现
**本机（这台电脑）** 与该设备的连接 / 断开：

    BluetoothFindFirstRadio / BluetoothFindNextRadio / BluetoothGetRadioInfo
        → 取得本机蓝牙 radio 句柄
    BluetoothSetServiceState(hRadio, &deviceInfo, &guidService, flags)
        → flags = BLUETOOTH_SERVICE_DISABLE(0x00) 断开 / ENABLE(0x01) 连接

它**不依赖耳机的任何私有协议**，可靠性远高于私有命令；但：

- 这是「本机侧」操作：只影响这台电脑自己的连接，不会改变耳机与其它设备的连接；
- **作者无法在开发环境（无蓝牙的 Linux/CI）里验证真机效果**，
  请在目标 Windows 机器上实测一次确认行为符合预期；
- 需要该设备此前已与本机配对（记住过），且函数在少数系统上可能返回
  ERROR_ACCESS_DENIED —— 此时会如实抛出并提示。

本模块只处理「本机」这一行；其它已连接设备的切换见 controller 的
set_multi_connect_status（实验性私有命令 0x014A）。
"""

from __future__ import annotations

import ctypes
import ctypes.wintypes as wintypes
import sys
from dataclasses import dataclass
from typing import List, Optional

IS_WINDOWS = sys.platform == "win32"

# BluetoothSetServiceState 的 flags
BLUETOOTH_SERVICE_DISABLE = 0x00
BLUETOOTH_SERVICE_ENABLE = 0x01

# ERROR_SUCCESS / ERROR_ACCESS_DENIED（用于把返回值翻成可读文本）
_ERROR_TEXT = {
    0: "ERROR_SUCCESS",
    5: "ERROR_ACCESS_DENIED",
    87: "ERROR_INVALID_PARAMETER",
    1168: "ERROR_NOT_FOUND (设备未配对/未记住)",
}

# 需要一起启用/禁用的经典音频服务 GUID（十六进制字符串，无花括号）
SERVICE_GUIDS = {
    "A2DP": "0000110B-0000-1000-8000-00805F9B34FB",   # 音频接收
    "HFP": "0000111E-0000-1000-8000-00805F9B34FB",    # 免提
    "AVRCP": "0000110E-0000-1000-8000-00805F9B34FB",  # 远程控制
}


class BluetoothError(RuntimeError):
    """本机蓝牙操作失败（附 Windows 错误码）。"""

    def __init__(self, message: str, code: Optional[int] = None) -> None:
        self.code = code
        if code is not None:
            message = f"{message}（WinError {code}: " \
                      f"{_ERROR_TEXT.get(code, 'UNKNOWN')}）"
        super().__init__(message)


@dataclass
class LocalActionResult:
    ok: bool
    detail: str
    code: Optional[int] = None


if IS_WINDOWS:
    _bth = ctypes.WinDLL("bthprops.cpl")
    _kernel32 = ctypes.windll.kernel32

    class _BLUETOOTH_ADDRESS(ctypes.Structure):
        _fields_ = [("ullLong", ctypes.c_uint64)]

    class _SYSTEMTIME(ctypes.Structure):
        _fields_ = [
            ("wYear", ctypes.c_uint16), ("wMonth", ctypes.c_uint16),
            ("wDayOfWeek", ctypes.c_uint16), ("wDay", ctypes.c_uint16),
            ("wHour", ctypes.c_uint16), ("wMinute", ctypes.c_uint16),
            ("wSecond", ctypes.c_uint16), ("wMilliseconds", ctypes.c_uint16),
        ]

    class BLUETOOTH_DEVICE_INFO(ctypes.Structure):
        _fields_ = [
            ("dwSize", ctypes.c_uint32),
            ("address", _BLUETOOTH_ADDRESS),
            ("ulClassofDevice", ctypes.c_uint32),
            ("fConnected", ctypes.c_int),
            ("fRemembered", ctypes.c_int),
            ("fAuthenticated", ctypes.c_int),
            ("stLastSeen", _SYSTEMTIME),
            ("stLastUsed", _SYSTEMTIME),
            ("szName", ctypes.c_wchar * 248),
        ]

    class BLUETOOTH_RADIO_INFO(ctypes.Structure):
        _fields_ = [
            ("dwSize", ctypes.c_uint32),
            ("address", _BLUETOOTH_ADDRESS),
            ("szName", ctypes.c_wchar * 248),
            ("ulClassofDevice", ctypes.c_uint32),
            ("lmpSubversion", ctypes.c_uint16),
            ("manufacturer", ctypes.c_uint16),
        ]

    # ---- 函数签名（避免 64 位下指针被截断）----
    _bth.BluetoothFindFirstRadio.argtypes = [
        ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)]
    _bth.BluetoothFindFirstRadio.restype = ctypes.c_void_p
    _bth.BluetoothFindNextRadio.argtypes = [
        ctypes.c_void_p, ctypes.POINTER(ctypes.c_void_p)]
    _bth.BluetoothFindNextRadio.restype = ctypes.c_int
    _bth.BluetoothFindRadioClose.argtypes = [ctypes.c_void_p]
    _bth.BluetoothFindRadioClose.restype = ctypes.c_int
    _bth.BluetoothGetRadioInfo.argtypes = [
        ctypes.c_void_p, ctypes.POINTER(BLUETOOTH_RADIO_INFO)]
    _bth.BluetoothGetRadioInfo.restype = ctypes.c_uint32
    _bth.BluetoothSetServiceState.argtypes = [
        ctypes.c_void_p, ctypes.POINTER(BLUETOOTH_DEVICE_INFO),
        ctypes.c_void_p, ctypes.c_uint32]
    _bth.BluetoothSetServiceState.restype = ctypes.c_uint32
else:  # 非 Windows：占位结构，保证可导入
    BLUETOOTH_DEVICE_INFO = None  # type: ignore
    BLUETOOTH_RADIO_INFO = None   # type: ignore


def _mac_to_ull(mac: str) -> int:
    """把 'AA:BB:CC:DD:EE:FF' 转成 BluetoothFind/Set 用的 ullLong。"""
    h = mac.replace("-", ":").replace(":", "").strip()
    if len(h) != 12:
        raise BluetoothError(f"非法 MAC：{mac!r}")
    return int(h, 16)


def _guid_from_str(s: str):
    """把 GUID 字符串转成 BLUETOOTH_GUID（16 字节）ctypes 缓冲。"""
    import uuid as _uuid
    u = _uuid.UUID(s)
    return (ctypes.c_ubyte * 16).from_buffer_copy(u.bytes_le)


def _find_first_radio() -> Optional[int]:
    """返回本机第一个蓝牙 radio 句柄（int）；没有蓝牙则 None。"""
    h = ctypes.c_void_p()
    if not _bth.BluetoothFindFirstRadio(None, ctypes.byref(h)):
        return None
    return int(h.value) if h.value else None


def _radio_info(h_radio: int) -> Optional[dict]:
    info = BLUETOOTH_RADIO_INFO()
    info.dwSize = ctypes.sizeof(BLUETOOTH_RADIO_INFO)
    rc = _bth.BluetoothGetRadioInfo(ctypes.c_void_p(h_radio), ctypes.byref(info))
    if rc != 0:
        return None
    addr = info.address.ullLong
    mac = ":".join(f"{(addr >> (8 * i)) & 0xFF:02X}" for i in range(5, -1, -1))
    return {"name": info.szName, "mac": mac}


def list_radios() -> List[dict]:
    """枚举本机所有蓝牙 radio。非 Windows 或失败返回空列表。"""
    if not IS_WINDOWS:
        return []
    out: List[dict] = []
    h = ctypes.c_void_p()
    if not _bth.BluetoothFindFirstRadio(None, ctypes.byref(h)):
        return out
    try:
        cur = h
        while cur.value:
            info = _radio_info(int(cur.value))
            if info:
                out.append(info)
            nxt = ctypes.c_void_p()
            if not _bth.BluetoothFindNextRadio(cur, ctypes.byref(nxt)):
                break
            cur = nxt
    finally:
        try:
            _bth.BluetoothFindRadioClose(h)
        except Exception:
            pass
    return out


def _device_info_for(mac: str) -> "BLUETOOTH_DEVICE_INFO":
    info = BLUETOOTH_DEVICE_INFO()
    info.dwSize = ctypes.sizeof(BLUETOOTH_DEVICE_INFO)
    info.address.ullLong = _mac_to_ull(mac)
    return info


def set_service_state(mac: str, enable: bool,
                      services: Optional[List[str]] = None) -> LocalActionResult:
    """对**本机**与 mac 之间的经典音频服务做 启用 / 禁用。

    enable=True  → BLUETOOTH_SERVICE_ENABLE（连接）
    enable=False → BLUETOOTH_SERVICE_DISABLE（断开）

    返回 LocalActionResult（含是否全部成功、可读详情）。
    ⚠️ 需在真机确认行为；见模块顶部说明。
    """
    if not IS_WINDOWS:
        return LocalActionResult(False, "本机蓝牙操作仅支持 Windows。")
    h_radio = _find_first_radio()
    if h_radio is None:
        return LocalActionResult(False, "未找到本机蓝牙 radio（蓝牙关闭或无适配器）。")
    try:
        info = _device_info_for(mac)
        flags = BLUETOOTH_SERVICE_ENABLE if enable else BLUETOOTH_SERVICE_DISABLE
        names = services or list(SERVICE_GUIDS.keys())
        results = []
        all_ok = True
        for name in names:
            guid_s = SERVICE_GUIDS.get(name)
            if not guid_s:
                continue
            gbuf = _guid_from_str(guid_s)
            rc = int(_bth.BluetoothSetServiceState(
                ctypes.c_void_p(h_radio), ctypes.byref(info),
                ctypes.cast(gbuf, ctypes.c_void_p), flags))
            ok = (rc == 0)
            all_ok = all_ok and ok
            results.append(f"{name}={'OK' if ok else rc}")
        act = "连接" if enable else "断开"
        detail = f"本机 {act} {mac}：{', '.join(results)}"
        return LocalActionResult(all_ok, detail,
                                 None if all_ok else -1)
    except BluetoothError as exc:
        return LocalActionResult(False, str(exc), exc.code)
    except Exception as exc:
        return LocalActionResult(False, f"本机蓝牙操作异常：{exc}")
    finally:
        try:
            _bth.BluetoothFindRadioClose(ctypes.c_void_p(h_radio))
        except Exception:
            pass


def disconnect_local(mac: str) -> LocalActionResult:
    """断开**本机**与该设备的连接（禁用 A2DP/HFP/AVRCP 服务）。"""
    return set_service_state(mac, enable=False)


def connect_local(mac: str) -> LocalActionResult:
    """重新连接**本机**与该设备（启用 A2DP/HFP/AVRCP 服务）。"""
    return set_service_state(mac, enable=True)
