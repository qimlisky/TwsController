"""
vivo / iQOO TWS 私有 GAIA 协议编解码。

协议层为纯 Python、无任何第三方依赖、无 UI 依赖，可在任意平台跑单元测试。

帧格式（Bluetooth Classic RFCOMM / SPP 通道，官方 Android App 同款）：

    FF [ver] [flags] [len] [vendor_hi] [vendor_lo] [cmd_hi] [cmd_lo] [payload...]

BLE GATT 通道（官方 iOS App 同款）是「裸 content」：vendor(2) cmd(2) payload，
无 FF 帧头，见 encode_gatt_frame()。

致谢：命令号、wire profile、payload 布局移植自 Zhaoyi-ya/TWS-Pods-PC（GPL-3.0-only）
与其上游 HyperEars 的逆向成果；空间音频 0x0139 的开关语义参考 FLuoXue/VivoPodsManager。
本文件随项目以 GPL-3.0-only 发布。
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from enum import IntEnum
from typing import List, Optional

# ----------------------------------------------------------------------------
# 帧常量
# ----------------------------------------------------------------------------

PREAMBLE = 0xFF
FLAG_CHECKSUM = 0x01
FLAG_LENGTH_EXTENSION = 0x02
COMMAND_BYTES = 4
MAX_FRAME_BYTES = 65_544

# vendor：GAIA 通用握手用 0x000A；vivo 私有命令用 0x001B
VIVO_VENDOR = 0x001B
GAIA_VENDOR = 0x000A

# 版本分层：握手 / 注册通知链 / 电量恒用 v4；其余控制帧按 profile（多为 v3）
HANDSHAKE_VERSION = 4
CONTROL_VERSION = 3


class NoiseMode(IntEnum):
    """出声模式（全型号统一）：0=降噪 / 1=关闭 / 2=通透。"""

    ANC = 0
    OFF = 1
    TRANSPARENCY = 2


# ----------------------------------------------------------------------------
# 命令号
# ----------------------------------------------------------------------------

# 握手 / 注册 / 电量
HANDSHAKE = 0x0300
HANDSHAKE_RESPONSE = 0x8300
REGISTER_CHAIN = (0x0202, 0x0203, 0x0204, 0x0205, 0x0206)
QUERY_BATTERY = 0x0207
REPORT_BATTERY = 0x8207

# 当前降噪模式（权威命令；0x010C 只改配置不出声，0x0131 是长按手势配置）
SET_NOISE_MODE = 0x0130
QUERY_NOISE_MODE = 0x0230
ACK_NOISE_MODE = 0x8130
REPORT_NOISE_MODE = 0x8230

# 空间音频开关（反编译 + 社区实现，未在全型号真机验证，标记为实验功能）
SET_SPATIAL_AUDIO = 0x0139
QUERY_SPATIAL_AUDIO = 0x0239
ACK_SPATIAL_AUDIO = 0x8139
REPORT_SPATIAL_AUDIO = 0x8239

# 空间音效模式（剧场/音乐厅/录音棚/演唱会，命令语义待真机确认，预留）
SET_SPATIAL_PRESET = 0x013E
QUERY_SPATIAL_PRESET = 0x023E

# 低延迟游戏模式（实验）
SET_LOW_LATENCY = 0x0151
QUERY_LOW_LATENCY = 0x0251
ACK_LOW_LATENCY = 0x8151
REPORT_LOW_LATENCY = 0x8251

# EQ 音效预设
SET_AUDIO_EFFECT = 0x0118
QUERY_AUDIO_EFFECT = 0x0218
ACK_AUDIO_EFFECT = 0x8118
REPORT_AUDIO_EFFECT = 0x8218
AUDIO_EFFECT_CODES = {
    0x00: "标准",
    0x01: "清澈人声",
    0x02: "超重低音",
    0x03: "清亮高音",
    0x05: "悠扬听书",
}

# 查找耳机（响铃）
SET_FIND_DEVICE = 0x0120
ACK_FIND_DEVICE = 0x8120

# 佩戴检测开关 / 实时佩戴状态
SET_WEAR_DETECTION = 0x0103
REPORT_WEAR_DETECTION = 0x8203
QUERY_WEAR_STATE = 0x020D
REPORT_WEAR_STATE = 0x820D
# 以官方 App APK 反编译（AbstractC7500c）+ 真机验证、C# 实现交叉确认的位段：
# bit0/1 为在盒段（左/右），bit2/3 为佩戴段（左/右）
WEAR_LEFT_IN_CASE = 0x01
WEAR_RIGHT_IN_CASE = 0x02
WEAR_LEFT_WORN = 0x04
WEAR_RIGHT_WORN = 0x08

# 双击 / 长按手势
SET_DOUBLE_TAP = 0x0102
REPORT_DOUBLE_TAP = 0x8202
SET_LONG_PRESS = 0x0131
QUERY_LONG_PRESS = 0x0231
ACK_LONG_PRESS = 0x8131
REPORT_LONG_PRESS = 0x8231
LONG_PRESS_SUBCMD = 0x05
NOISE_CYCLE_CODES = {
    0x0B: "关闭 + 通透 + 降噪",
    0x0A: "通透 + 降噪",
    0x08: "关闭 + 降噪",
    0x09: "关闭 + 通透",
    0xFF: "无",
}
TAP_LEFT_CODES = {
    0x00: "语音助手", 0x01: "播放/暂停", 0x02: "上一首",
    0x03: "下一首", 0x05: "翻译", 0x06: "无",
}
TAP_RIGHT_CODES = {
    0x10: "语音助手", 0x11: "播放/暂停", 0x12: "上一首",
    0x13: "下一首", 0x15: "翻译", 0x16: "无",
}

# 设备信息
QUERY_FIRMWARE = 0x021C
REPORT_FIRMWARE = 0x821C
QUERY_MODEL = 0x021B
REPORT_MODEL = 0x821B
QUERY_SERIAL = 0x0215
REPORT_SERIAL = 0x8215

# 多连接
QUERY_MULTI_CONNECT = 0x0249
REPORT_MULTI_CONNECT = 0x8249
SET_MULTI_CONNECT = 0x014A
ENABLE_MULTI_CONNECT = 0x014C
REMOVE_MULTI_CONNECT = 0x014D
MC_DISCONNECTED = 0x00
MC_CONNECTED_INACTIVE = 0x01
MC_CONNECTED_ACTIVE = 0x02

# 时间同步（耳机主动索要，主机应答）
PEER_TIME_REQUEST = 0x8509
HOST_TIME_RESPONSE = 0x0509

# 埋点上报（吸收忽略）
TELEMETRY_REPORT = 0x8224

# RFCOMM 私有服务入口
VIVO_GAIA_UUID = "00000837-d102-11e1-9b23-00025b00a5a5"
# BLE GATT 通道
GATT_SERVICE_UUID = "00001100-d102-11e1-9b23-00025b00a5a5"
GATT_WRITE_UUID = "00001101-d102-11e1-9b23-00025b00a5a5"
GATT_NOTIFY_UUID = "00001102-d102-11e1-9b23-00025b00a5a5"


# ----------------------------------------------------------------------------
# Profile（不同代际型号的帧层差异）
# ----------------------------------------------------------------------------

@dataclass
class Profile:
    label: str
    gaia_version: int
    noise_query_payload: bytes
    noise_set_suffix: bytes
    register_notifications: bool = True


AIR3_PRO_CAPTURED = Profile(
    label="vivo TWS Air3 Pro（实测 v3）",
    gaia_version=3,
    noise_query_payload=b"",
    noise_set_suffix=bytes([4, 0]),
)
TWS_3E_V3 = Profile(
    label="vivo TWS 3e（实测 v3）",
    gaia_version=3,
    noise_query_payload=b"",
    noise_set_suffix=bytes([3]),
)
FAMILY_DEFAULT_V4 = Profile(
    label="vivo 家族默认 v4（TWS 5 等）",
    gaia_version=4,
    noise_query_payload=bytes([0]),
    noise_set_suffix=bytes([3, 1]),
    register_notifications=False,
)


# ----------------------------------------------------------------------------
# 帧编解码
# ----------------------------------------------------------------------------

@dataclass
class Frame:
    version: int
    flags: int
    vendor: int
    command: int
    payload: bytes
    raw: bytes


def _u8(value: int) -> int:
    return value & 0xFF


def _xor(data: bytes, count: int) -> int:
    value = 0
    for i in range(count):
        value ^= data[i] & 0xFF
    return value & 0xFF


def frame(version: int, vendor: int, command: int, payload: bytes = b"", flags: int = 0) -> bytes:
    """构造完整 GAIA 帧（RFCOMM 通道）。"""
    if len(payload) > 254:
        raise ValueError("Compact GAIA payload is limited to 254 bytes")
    extended = bool(flags & FLAG_LENGTH_EXTENSION)
    header_size = 5 if extended else 4
    out = bytearray(header_size + COMMAND_BYTES + len(payload))
    out[0] = PREAMBLE
    out[1] = _u8(version)
    out[2] = _u8(flags)
    if extended:
        out[3] = _u8((len(payload) >> 8) & 0xFF)
        out[4] = _u8(len(payload) & 0xFF)
    else:
        out[3] = _u8(len(payload))
    off = header_size
    out[off] = _u8((vendor >> 8) & 0xFF)
    out[off + 1] = _u8(vendor & 0xFF)
    out[off + 2] = _u8((command >> 8) & 0xFF)
    out[off + 3] = _u8(command & 0xFF)
    out[off + COMMAND_BYTES:] = payload
    if flags & FLAG_CHECKSUM:
        out.append(_u8(_xor(bytes(out), len(out))))
    return bytes(out)


def encode_gatt_frame(vendor: int, command: int, payload: bytes = b"") -> bytes:
    """构造 GATT 裸帧：vendor(2) cmd(2) payload（无帧头）。"""
    return bytes([
        (vendor >> 8) & 0xFF, vendor & 0xFF,
        (command >> 8) & 0xFF, command & 0xFF,
    ]) + bytes(payload)


def parse_gatt_frame(data: bytes) -> Frame:
    if len(data) < 4:
        raise ValueError("GATT frame too short")
    return Frame(
        version=0, flags=0,
        vendor=(data[0] << 8) | data[1],
        command=(data[2] << 8) | data[3],
        payload=bytes(data[4:]), raw=bytes(data),
    )


class Decoder:
    """流式解码器：去前导噪声、拆包、粘包、可选 XOR 校验。"""

    def __init__(self, initial_capacity: int = 256) -> None:
        self._buf = bytearray(max(16, initial_capacity))
        self._size = 0

    def offer(self, chunk: bytes) -> List[Frame]:
        self._append(chunk)
        frames: List[Frame] = []
        while True:
            self._discard_noise()
            if self._size < 4:
                return frames
            flags = self._peek(2)
            extended = bool(flags & FLAG_LENGTH_EXTENSION)
            header_size = 5 if extended else 4
            if self._size < header_size:
                return frames
            payload_len = (
                ((self._peek(3) << 8) | self._peek(4)) if extended else self._peek(3)
            )
            checksum_bytes = 1 if (flags & FLAG_CHECKSUM) else 0
            total = header_size + COMMAND_BYTES + payload_len + checksum_bytes
            if total > MAX_FRAME_BYTES:
                self._discard(1)
                continue
            if self._size < total:
                return frames
            raw = self._take(total)
            if checksum_bytes == 1 and _xor(raw, len(raw) - 1) != raw[-1]:
                continue
            off = header_size
            frames.append(Frame(
                version=raw[1],
                flags=raw[2],
                vendor=(raw[off] << 8) | raw[off + 1],
                command=(raw[off + 2] << 8) | raw[off + 3],
                payload=bytes(raw[off + COMMAND_BYTES: off + COMMAND_BYTES + payload_len]),
                raw=bytes(raw),
            ))

    def reset(self) -> None:
        self._size = 0

    def _append(self, chunk: bytes) -> None:
        self._ensure(self._size + len(chunk))
        self._buf[self._size:self._size + len(chunk)] = chunk
        self._size += len(chunk)

    def _discard_noise(self) -> None:
        n = 0
        while n < self._size and self._buf[n] != PREAMBLE:
            n += 1
        if n:
            self._discard(n)

    def _peek(self, i: int) -> int:
        return self._buf[i] & 0xFF

    def _take(self, n: int) -> bytearray:
        chunk = self._buf[:n]
        self._discard(n)
        return chunk

    def _discard(self, n: int) -> None:
        if n >= self._size:
            self._size = 0
            return
        self._buf[: self._size - n] = self._buf[n: self._size]
        self._size -= n

    def _ensure(self, need: int) -> None:
        if need <= len(self._buf):
            return
        cap = len(self._buf)
        while cap < need:
            cap *= 2
        self._buf = bytearray(self._buf) + bytearray(cap - len(self._buf))


# ----------------------------------------------------------------------------
# 状态模型
# ----------------------------------------------------------------------------

@dataclass
class BatteryState:
    left: Optional[int]
    right: Optional[int]
    case: Optional[int]
    left_charging: bool
    right_charging: bool
    case_charging: bool


@dataclass
class BatteryReading:
    level: Optional[int] = None
    charging: bool = False
    is_stale: bool = True

    @property
    def valid(self) -> bool:
        return self.level is not None and not self.is_stale


class BatteryCache:
    """0xFF 无效值保留旧读数并置灰；断连整体置灰；换设备清空。"""

    def __init__(self) -> None:
        self.left = BatteryReading()
        self.right = BatteryReading()
        self.case = BatteryReading()
        self._items = (self.left, self.right, self.case)

    def update(self, s: BatteryState) -> None:
        for comp, pct, chg in (
            (self.left, s.left, s.left_charging),
            (self.right, s.right, s.right_charging),
            (self.case, s.case, s.case_charging),
        ):
            if pct is None:
                comp.is_stale = True
            else:
                comp.level, comp.charging, comp.is_stale = pct, chg, False

    def mark_all_stale(self) -> None:
        for c in self._items:
            c.is_stale = True

    def reset(self) -> None:
        for c in self._items:
            c.level, c.charging, c.is_stale = None, False, True


@dataclass
class WearSnapshot:
    left: str   # worn / in_case / removed
    right: str
    flags: int


@dataclass
class MultiConnectDevice:
    mac: bytes
    raw3: bytes
    status: int
    name: str

    @property
    def active(self) -> bool:
        return self.status == MC_CONNECTED_ACTIVE


@dataclass
class MultiConnectState:
    status: int
    devices: List[MultiConnectDevice]


# ----------------------------------------------------------------------------
# 指令编码
# ----------------------------------------------------------------------------

def _cmd(version: int, command: int, payload: bytes = b"") -> bytes:
    return frame(version, VIVO_VENDOR, command, payload)


def encode_handshake() -> bytes:
    return frame(HANDSHAKE_VERSION, GAIA_VENDOR, HANDSHAKE)


def encode_register_notifications() -> List[bytes]:
    return [frame(HANDSHAKE_VERSION, VIVO_VENDOR, c) for c in REGISTER_CHAIN]


def encode_query_battery() -> bytes:
    return frame(HANDSHAKE_VERSION, VIVO_VENDOR, QUERY_BATTERY)


def encode_query_noise(profile: Profile) -> bytes:
    return frame(profile.gaia_version, VIVO_VENDOR, QUERY_NOISE_MODE,
                 profile.noise_query_payload)


def encode_set_noise(mode: NoiseMode, profile: Profile) -> bytes:
    return frame(profile.gaia_version, VIVO_VENDOR, SET_NOISE_MODE,
                 bytes([int(mode)]) + profile.noise_set_suffix)


def encode_query_spatial(profile: Profile) -> bytes:
    return _cmd(profile.gaia_version, QUERY_SPATIAL_AUDIO)


def encode_set_spatial(enabled: bool, profile: Profile) -> bytes:
    return _cmd(profile.gaia_version, SET_SPATIAL_AUDIO, bytes([1 if enabled else 0]))


def encode_query_low_latency(profile: Profile) -> bytes:
    return _cmd(profile.gaia_version, QUERY_LOW_LATENCY)


def encode_set_low_latency(enabled: bool, profile: Profile) -> bytes:
    return _cmd(profile.gaia_version, SET_LOW_LATENCY, bytes([1 if enabled else 0]))


def encode_query_eq(profile: Profile) -> bytes:
    return _cmd(profile.gaia_version, QUERY_AUDIO_EFFECT)


def encode_set_eq(eq_code: int, profile: Profile) -> bytes:
    return _cmd(profile.gaia_version, SET_AUDIO_EFFECT, bytes([eq_code & 0xFF]))


def encode_find_device(start: bool, profile: Profile) -> bytes:
    return _cmd(profile.gaia_version, SET_FIND_DEVICE, bytes([1 if start else 0]))


def encode_query_wear(profile: Profile) -> bytes:
    return _cmd(profile.gaia_version, QUERY_WEAR_STATE)


def encode_set_wear_detection(enabled: bool, profile: Profile) -> bytes:
    return _cmd(profile.gaia_version, SET_WEAR_DETECTION, bytes([1 if enabled else 0]))


def encode_query_firmware(profile: Profile) -> bytes:
    return _cmd(profile.gaia_version, QUERY_FIRMWARE)


def encode_query_model(profile: Profile) -> bytes:
    return _cmd(profile.gaia_version, QUERY_MODEL)


def encode_query_multi_connect(profile: Profile) -> bytes:
    return _cmd(profile.gaia_version, QUERY_MULTI_CONNECT)


def encode_set_multi_connect(devices: List[MultiConnectDevice], profile: Profile) -> bytes:
    payload = bytearray()
    for d in devices:
        payload += bytes(d.mac[:6]) + bytes([d.status & 0xFF])
    return _cmd(profile.gaia_version, SET_MULTI_CONNECT, bytes(payload))


def encode_host_time(dt: Optional[datetime] = None, version: int = CONTROL_VERSION) -> bytes:
    """主机应答耳机 0x8509 时间请求，7 字节纯二进制（非 BCD）。"""
    dt = dt or datetime.now()
    y = dt.year
    payload = bytes([(y // 100) & 0xFF, y % 100, dt.month, dt.day,
                     dt.hour, dt.minute, dt.second])
    return _cmd(version, HOST_TIME_RESPONSE, payload)


def initial_commands(profile: Profile, spatial: bool = False, game: bool = False) -> List[bytes]:
    """连接初始化序列（按 profile 决定是否注册通知链）。"""
    cmds: List[bytes] = [encode_handshake()]
    if profile.register_notifications:
        cmds += encode_register_notifications()
    cmds += [
        encode_query_battery(),
        encode_query_noise(profile),
        encode_query_firmware(profile),
        encode_query_wear(profile),
        encode_query_eq(profile),
    ]
    # 实验功能按 UI 开关查询
    if spatial:
        cmds.append(encode_query_spatial(profile))
    if game:
        cmds.append(encode_query_low_latency(profile))
    return cmds


# ----------------------------------------------------------------------------
# 回包解析
# ----------------------------------------------------------------------------

def _pct(b: int) -> Optional[int]:
    v = b & 0xFF
    return v if 0 <= v <= 100 else None


def parse_battery(fr: Frame) -> Optional[BatteryState]:
    if fr.vendor != VIVO_VENDOR or fr.command != REPORT_BATTERY:
        return None
    p = fr.payload
    if len(p) < 5 or p[0] != 0:
        return None
    return BatteryState(
        left=_pct(p[1]), right=_pct(p[2]), case=_pct(p[3]),
        left_charging=bool(p[4] & 0x01),
        right_charging=bool(p[4] & 0x02),
        case_charging=bool(p[4] & 0x04),
    )


def parse_noise(fr: Frame) -> Optional[NoiseMode]:
    """0x8130 / 0x8230 → [00][mode][reduceModel...]。"""
    if fr.vendor != VIVO_VENDOR or fr.command not in (ACK_NOISE_MODE, REPORT_NOISE_MODE):
        return None
    p = fr.payload
    if len(p) < 2 or p[0] != 0:
        return None
    try:
        return NoiseMode(p[1])
    except ValueError:
        return None


def _parse_bool_switch(fr: Frame, ack: int, report: int) -> Optional[bool]:
    """通用单字节开关回包：[00][0/1]。"""
    if fr.vendor != VIVO_VENDOR or fr.command not in (ack, report):
        return None
    p = fr.payload
    if len(p) >= 2 and p[0] == 0 and p[1] in (0, 1):
        return bool(p[1])
    return None


def parse_spatial(fr: Frame) -> Optional[bool]:
    return _parse_bool_switch(fr, ACK_SPATIAL_AUDIO, REPORT_SPATIAL_AUDIO)


def parse_low_latency(fr: Frame) -> Optional[bool]:
    return _parse_bool_switch(fr, ACK_LOW_LATENCY, REPORT_LOW_LATENCY)


def parse_eq(fr: Frame) -> Optional[int]:
    if fr.vendor != VIVO_VENDOR or fr.command not in (ACK_AUDIO_EFFECT, REPORT_AUDIO_EFFECT):
        return None
    p = fr.payload
    if len(p) >= 2 and p[0] == 0:
        return p[1]
    return None


def parse_find(fr: Frame) -> Optional[bool]:
    if fr.vendor != VIVO_VENDOR or fr.command != ACK_FIND_DEVICE:
        return None
    p = fr.payload
    if len(p) >= 2 and p[0] == 0 and p[1] in (0, 1):
        return bool(p[1])
    return None


def parse_wear(fr: Frame) -> Optional[WearSnapshot]:
    """0x820D → [status][flags]。

    bit0=左耳在盒 / bit1=右耳在盒 / bit2=左耳佩戴 / bit3=右耳佩戴
    （APK AbstractC7500c 实锤：m36699c=(i&4)==4 左佩戴、m36702f=(i&8)==8 右佩戴；
    0x0C=双耳同时佩戴，0x03=双耳在盒）。
    """
    if fr.vendor != VIVO_VENDOR or fr.command != REPORT_WEAR_STATE:
        return None
    p = fr.payload
    if not p:
        return None
    flags = p[1] if len(p) >= 2 else p[0]

    def ear(in_case_bit: int, worn_bit: int) -> str:
        if flags & worn_bit:
            return "worn"
        if flags & in_case_bit:
            return "in_case"
        return "removed"

    return WearSnapshot(
        left=ear(WEAR_LEFT_IN_CASE, WEAR_LEFT_WORN),
        right=ear(WEAR_RIGHT_IN_CASE, WEAR_RIGHT_WORN),
        flags=flags,
    )


def parse_firmware(fr: Frame) -> Optional[str]:
    """0x821C 紧凑二进制：payload[2]=主、[5]=次、[6]=修订。"""
    if fr.vendor != VIVO_VENDOR or fr.command != REPORT_FIRMWARE:
        return None
    p = fr.payload
    if not p:
        return None
    if p[0] == 0 and len(p) >= 7 and p[2] <= 99 and p[5] <= 99 and p[6] <= 99:
        return f"{p[2]}.{p[5]}.{p[6]}"
    return None


def parse_multi_connect(fr: Frame) -> Optional[MultiConnectState]:
    if fr.vendor != VIVO_VENDOR or fr.command != REPORT_MULTI_CONNECT:
        return None
    p = fr.payload
    if len(p) < 2 or p[0] != 0:
        return None
    count = p[1]
    off = 2
    devices: List[MultiConnectDevice] = []
    for _ in range(count):
        if off + 11 > len(p):
            return None
        mac = bytes(p[off:off + 6])
        raw3 = bytes(p[off + 6:off + 9])
        status = p[off + 9]
        name_len = p[off + 10]
        if off + 11 + name_len > len(p):
            return None
        name = p[off + 11:off + 11 + name_len].decode("utf-8", errors="replace")
        devices.append(MultiConnectDevice(mac=mac, raw3=raw3, status=status, name=name))
        off += 11 + name_len
    return MultiConnectState(status=p[0], devices=devices)


def is_peer_time_request(fr: Frame) -> bool:
    return fr.vendor == VIVO_VENDOR and fr.command == PEER_TIME_REQUEST


def hexlify(data: bytes) -> str:
    return " ".join(f"{b:02X}" for b in data)
