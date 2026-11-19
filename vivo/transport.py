"""
蓝牙传输层。

- Windows：纯 ctypes 调 ws2_32.dll 建立 Bluetooth Classic RFCOMM（SPP）连接，
  服务 UUID 为 vivo 私有 00000837-d102-11e1-9b23-00025b00a5a5（隐式 SDP 查通道），
  零第三方依赖；已配对设备枚举直接读注册表 BTHPORT\\Parameters\\Devices。
- 其他平台：本模块可正常导入（仅在真正连接时才报平台错误），方便开发/跑协议测试。

参考并移植自 Zhaoyi-ya/TWS-Pods-PC（GPL-3.0-only），接收超时改用 SO_RCVTIMEO。
"""

from __future__ import annotations

import sys
import threading
import time
import uuid as _uuid
from abc import ABC, abstractmethod
from typing import List, Optional, Tuple

from .vivo_protocol import VIVO_GAIA_UUID, Profile

IS_WINDOWS = sys.platform == "win32"


class Transport(ABC):
    @abstractmethod
    def connect(self) -> None: ...

    @abstractmethod
    def send(self, data: bytes) -> None: ...

    @abstractmethod
    def recv(self, timeout: float = 1.0) -> bytes: ...

    @abstractmethod
    def close(self) -> None: ...

    @property
    def connected(self) -> bool:
        return False


# --------------------------------------------------------------------------- #
# Windows 实现
# --------------------------------------------------------------------------- #

if IS_WINDOWS:
    import ctypes
    import winreg

    AF_BTH = 32
    SOCK_STREAM = 1
    BTHPROTO_RFCOMM = 3
    SOL_SOCKET = 0xFFFF
    SO_RCVTIMEO = 0x1006
    SO_SNDTIMEO = 0x1005

    class _GUID(ctypes.Structure):
        _fields_ = [
            ("Data1", ctypes.c_uint32),
            ("Data2", ctypes.c_uint16),
            ("Data3", ctypes.c_uint16),
            ("Data4", ctypes.c_uint8 * 8),
        ]

    class _SOCKADDR_BTH(ctypes.Structure):
        _pack_ = 1
        _fields_ = [
            ("addressFamily", ctypes.c_ushort),
            ("btAddr", ctypes.c_ulonglong),
            ("serviceClassId", _GUID),
            ("port", ctypes.c_uint32),
        ]

    _ws = ctypes.WinDLL("ws2_32.dll")
    _ws.WSAStartup.argtypes = [ctypes.c_uint16, ctypes.c_void_p]
    _ws.WSAStartup.restype = ctypes.c_int
    _ws.WSAGetLastError.restype = ctypes.c_int
    _ws.socket.restype = ctypes.c_void_p
    _ws.socket.argtypes = [ctypes.c_int, ctypes.c_int, ctypes.c_int]
    _ws.connect.restype = ctypes.c_int
    _ws.connect.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int]
    _ws.closesocket.restype = ctypes.c_int
    _ws.closesocket.argtypes = [ctypes.c_void_p]
    _ws.send.restype = ctypes.c_int
    _ws.send.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
    _ws.recv.restype = ctypes.c_int
    _ws.recv.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
    _ws.setsockopt.restype = ctypes.c_int
    _ws.setsockopt.argtypes = [
        ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_void_p, ctypes.c_int
    ]

    _wsa_started = False
    _wsa_lock = threading.Lock()

    def _ensure_wsa() -> None:
        global _wsa_started
        with _wsa_lock:
            if not _wsa_started:
                _ws.WSAStartup(0x0202, (ctypes.c_ubyte * 400)())
                _wsa_started = True

    def _uuid_to_guid(uuid_str: str) -> "_GUID":
        b = _uuid.UUID(uuid_str).bytes
        return _GUID(
            int.from_bytes(b[0:4], "big"),
            int.from_bytes(b[4:6], "big"),
            int.from_bytes(b[6:8], "big"),
            (ctypes.c_uint8 * 8)(*b[8:16]),
        )

    class WindowsRfcommTransport(Transport):
        """通过 vivo 私有 SPP UUID 连接耳机的 RFCOMM 客户端。"""

        def __init__(
            self,
            address: str,
            profile: Optional[Profile] = None,
            service_uuid: str = VIVO_GAIA_UUID,
            channel: Optional[int] = None,
        ) -> None:
            self.address = address
            self.profile = profile
            self.service_uuid = service_uuid
            self.channel = channel
            self._sock = None
            self._lock = threading.Lock()
            self._connected = False

        @property
        def connected(self) -> bool:
            return self._connected

        def connect(self) -> None:
            _ensure_wsa()
            s = _ws.socket(AF_BTH, SOCK_STREAM, BTHPROTO_RFCOMM)
            if not s:
                raise RuntimeError("无法创建蓝牙 socket（ws2_32.dll）")
            sa = _SOCKADDR_BTH()
            sa.addressFamily = AF_BTH
            sa.btAddr = int(self.address.replace(":", ""), 16)
            sa.serviceClassId = _uuid_to_guid(self.service_uuid)
            sa.port = self.channel or 0  # 0 + serviceClassId → 隐式 SDP 查找 RFCOMM 通道
            if _ws.connect(s, ctypes.byref(sa), ctypes.sizeof(sa)) != 0:
                err = _ws.WSAGetLastError()
                _ws.closesocket(s)
                raise RuntimeError(
                    f"RFCOMM 连接失败（WSA 错误 {err}）。请确认耳机已与 Windows 配对、"
                    "已开机且在范围内，且没有被其他设备独占控制通道。"
                )
            # 收发超时 1s（DWORD 毫秒），让读线程可周期性退出
            ms = ctypes.c_ulong(1000)
            _ws.setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, ctypes.byref(ms), ctypes.sizeof(ms))
            _ws.setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, ctypes.byref(ms), ctypes.sizeof(ms))
            self._sock = s
            self._connected = True

        def send(self, data: bytes) -> None:
            if self._sock is None:
                raise RuntimeError("通道未连接")
            with self._lock:
                _ws.send(self._sock, data, len(data), 0)

        def recv(self, timeout: float = 1.0) -> bytes:
            if self._sock is None:
                return b""
            end = time.time() + timeout
            chunks: List[bytes] = []
            while time.time() < end:
                buf = (ctypes.c_ubyte * 1024)()
                n = _ws.recv(self._sock, buf, 1024, 0)
                if n > 0:
                    chunks.append(bytes(buf[:n]))
                    break  # 一次返回，剩余交给 Decoder 缓存与下次读取
                if n == 0:
                    break
                err = _ws.WSAGetLastError()
                if err in (10060, 10035):  # 超时 /  WouldBlock
                    continue
                self._connected = False
                break
            return b"".join(chunks)

        def close(self) -> None:
            self._connected = False
            if self._sock is not None:
                try:
                    _ws.closesocket(self._sock)
                except OSError:
                    pass
            self._sock = None

    # ------------------------------------------------------------------ #
    # 已配对设备枚举
    #
    # 首选 bthprops.cpl 的 BluetoothFindFirstDevice/Next（普通用户可用，
    # 只读已配对/已记忆设备，不发起空中查询）；注册表路径作为兜底
    # （BTHPORT\Parameters\Devices 在部分系统上需要管理员）。
    # ------------------------------------------------------------------ #

    class _SYSTEMTIME(ctypes.Structure):
        _fields_ = [
            ("wYear", ctypes.c_uint16),
            ("wMonth", ctypes.c_uint16),
            ("wDayOfWeek", ctypes.c_uint16),
            ("wDay", ctypes.c_uint16),
            ("wHour", ctypes.c_uint16),
            ("wMinute", ctypes.c_uint16),
            ("wSecond", ctypes.c_uint16),
            ("wMilliseconds", ctypes.c_uint16),
        ]

    class _BLUETOOTH_DEVICE_INFO(ctypes.Structure):
        _fields_ = [
            ("dwSize", ctypes.c_uint32),
            ("address", ctypes.c_uint64),          # BLUETOOTH_ADDRESS.ullLong
            ("ulClassofDevice", ctypes.c_uint32),
            ("fConnected", ctypes.c_int),
            ("fRemembered", ctypes.c_int),
            ("fAuthenticated", ctypes.c_int),
            ("stLastSeen", _SYSTEMTIME),
            ("stLastUsed", _SYSTEMTIME),
            ("szName", ctypes.c_wchar * 248),
        ]

    class _BLUETOOTH_DEVICE_SEARCH_PARAMS(ctypes.Structure):
        _fields_ = [
            ("dwSize", ctypes.c_uint32),
            ("fReturnAuthenticated", ctypes.c_int),
            ("fReturnRemembered", ctypes.c_int),
            ("fReturnUnknown", ctypes.c_int),
            ("fReturnConnected", ctypes.c_int),
            ("fIssueInquiry", ctypes.c_int),       # False：只列已配对，不发起查询
            ("cTimeoutMultiplier", ctypes.c_ubyte),
            ("hRadio", ctypes.c_void_p),
        ]

    try:
        _bth = ctypes.WinDLL("bthprops.cpl")
        _bth.BluetoothFindFirstDevice.argtypes = [
            ctypes.POINTER(_BLUETOOTH_DEVICE_SEARCH_PARAMS),
            ctypes.POINTER(_BLUETOOTH_DEVICE_INFO),
        ]
        _bth.BluetoothFindFirstDevice.restype = ctypes.c_void_p
        _bth.BluetoothFindNextDevice.argtypes = [
            ctypes.c_void_p, ctypes.POINTER(_BLUETOOTH_DEVICE_INFO)
        ]
        _bth.BluetoothFindNextDevice.restype = ctypes.c_int
        _bth.BluetoothFindDeviceClose.argtypes = [ctypes.c_void_p]
        _bth.BluetoothFindDeviceClose.restype = ctypes.c_int
        _BTH_AVAILABLE = True
    except OSError:
        _BTH_AVAILABLE = False

    def _bth_addr_to_mac(addr: int) -> str:
        return ":".join(f"{(addr >> (8 * i)) & 0xFF:02X}" for i in range(5, -1, -1))

    def _enumerate_paired_bthapi() -> List[Tuple[str, str]]:
        """通过 BluetoothFind* API 枚举已配对设备（无需管理员）。"""
        out: List[Tuple[str, str]] = []
        if not _BTH_AVAILABLE:
            return out
        search = _BLUETOOTH_DEVICE_SEARCH_PARAMS()
        search.dwSize = ctypes.sizeof(_BLUETOOTH_DEVICE_SEARCH_PARAMS)
        search.fReturnAuthenticated = 1
        search.fReturnRemembered = 1
        search.fReturnConnected = 1
        search.fReturnUnknown = 0
        search.fIssueInquiry = 0
        search.hRadio = None
        info = _BLUETOOTH_DEVICE_INFO()
        info.dwSize = ctypes.sizeof(_BLUETOOTH_DEVICE_INFO)
        h = _bth.BluetoothFindFirstDevice(ctypes.byref(search), ctypes.byref(info))
        if not h:
            return out
        try:
            while True:
                mac = _bth_addr_to_mac(info.address)
                name = (info.szName or "").strip()
                out.append((name or mac, mac))
                info = _BLUETOOTH_DEVICE_INFO()
                info.dwSize = ctypes.sizeof(_BLUETOOTH_DEVICE_INFO)
                if not _bth.BluetoothFindNextDevice(h, ctypes.byref(info)):
                    break
        finally:
            _bth.BluetoothFindDeviceClose(h)
        return out

    # ------------------------------------------------------------------ #
    # 注册表枚举（兜底）
    # ------------------------------------------------------------------ #

    _BTH_KEY = r"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices"

    def _decode_reg_name(raw) -> str:
        if isinstance(raw, bytes):
            s = raw.decode("utf-8", "replace")
        elif isinstance(raw, str):
            try:
                s = bytes.fromhex(raw).decode("utf-8", "replace")
            except ValueError:
                s = raw
        else:
            return ""
        return s.rstrip("\x00").strip()

    def _enumerate_paired_registry() -> List[Tuple[str, str]]:
        """注册表兜底枚举（部分系统需要管理员）。失败返回空列表。"""
        out: List[Tuple[str, str]] = []
        try:
            root = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, _BTH_KEY)
        except OSError:
            return out
        try:
            i = 0
            while True:
                try:
                    sub = winreg.EnumKey(root, i)
                except OSError:
                    break
                i += 1
                if len(sub) != 12:
                    continue
                mac = ":".join(sub[j:j + 2] for j in range(0, 12, 2))
                name = ""
                try:
                    dk = winreg.OpenKey(root, sub)
                    try:
                        for value_name in ("Name", "LEName"):
                            try:
                                v, _ = winreg.QueryValueEx(dk, value_name)
                            except FileNotFoundError:
                                continue
                            name = _decode_reg_name(v)
                            if name:
                                break
                    finally:
                        winreg.CloseKey(dk)
                except OSError:
                    pass
                out.append((name or mac, mac))
        finally:
            winreg.CloseKey(root)
        return out

    def list_paired_devices(
        keywords: Optional[Tuple[str, ...]] = None
    ) -> List[Tuple[str, str]]:
        """返回 [(设备名, MAC), ...]。先 BluetoothFind* API，再注册表兜底。"""
        devices = _enumerate_paired_bthapi()
        if not devices:
            devices = _enumerate_paired_registry()
        if keywords:
            devices = [
                (name, mac) for name, mac in devices
                if any(k.lower() in name.lower() for k in keywords)
            ]
        # 去重（同一 MAC 两条通道都可能出现）
        seen, uniq = set(), []
        for name, mac in devices:
            if mac not in seen:
                seen.add(mac)
                uniq.append((name, mac))
        return uniq

else:
    def list_paired_devices(keywords: Optional[Tuple[str, ...]] = None) -> List[Tuple[str, str]]:
        return []

    class WindowsRfcommTransport(Transport):  # type: ignore
        def __init__(self, *args, **kwargs) -> None:
            raise RuntimeError("RFCOMM 传输仅支持 Windows；当前平台可使用演示模式。")
