"""
演示传输：在没有真机（或非 Windows 开发环境）时完整模拟一只 vivo TWS 耳机，
让 UI 与会话状态机可以全流程跑通。

它是一个内存回环：send() 解析主机发来的 GAIA 帧，按耳机语义把响应帧放进接收队列，
recv() 再取出。所有响应均使用与真机一致的帧格式。
"""

from __future__ import annotations

import queue
import random
import time
from typing import Optional

from . import vivo_protocol as vp
from .transport import Transport


def _frame(v: int, vendor: int, command: int, payload: bytes = b"") -> bytes:
    return vp.frame(v, vendor, command, payload)


class DemoTransport(Transport):
    """模拟 vivo TWS 4（家族 v4 profile：设置后缀 [03,01]）。"""

    def __init__(self, name: str = "vivo TWS 4（演示）") -> None:
        self.device_name = name
        self._rx: "queue.Queue[bytes]" = queue.Queue()
        self._connected = False
        self._decoder = vp.Decoder()
        # 模拟状态
        self._noise = vp.NoiseMode.OFF
        self._spatial = False
        self._game = False
        self._eq = 0
        self._finding = False
        self._profile = vp.FAMILY_DEFAULT_V4
        self._mc_devices = [
            (b"\xAA\xBB\xCC\x11\x22\x33", "vivo X200 Pro",
             vp.MC_CONNECTED_INACTIVE),
            (b"\x11\x22\x33\x44\x55\x66", "这台电脑",
             vp.MC_CONNECTED_ACTIVE),
        ]

    @property
    def connected(self) -> bool:
        return self._connected

    def connect(self) -> None:
        self._connected = True
        # 连上后耳机主动索要一次时间（真机行为）
        self._rx.put(_frame(vp.CONTROL_VERSION, vp.VIVO_VENDOR, vp.PEER_TIME_REQUEST, b"\x00"))

    def close(self) -> None:
        self._connected = False

    def send(self, data: bytes) -> None:
        for fr in self._decoder.offer(data):
            self._handle(fr)

    def recv(self, timeout: float = 0.5) -> bytes:
        try:
            return self._rx.get(timeout=timeout)
        except queue.Empty:
            return b""

    # ------------------------------------------------------------------ #
    def _put(self, data: bytes, delay: float = 0.0) -> None:
        if delay:
            time.sleep(delay)
        self._rx.put(data)

    def _handle(self, fr: vp.Frame) -> None:
        v = self._profile.gaia_version
        # 握手
        if fr.vendor == vp.GAIA_VENDOR and fr.command == vp.HANDSHAKE:
            self._put(_frame(vp.HANDSHAKE_VERSION, vp.GAIA_VENDOR,
                             vp.HANDSHAKE_RESPONSE, b"\x00"))
            return
        # 注册通知链 ACK
        if fr.command in vp.REGISTER_CHAIN:
            self._put(_frame(vp.HANDSHAKE_VERSION, vp.VIVO_VENDOR,
                             fr.command | 0x8000, b"\x00"))
            return
        # 电量（带一点随机波动，模拟真实上报；左耳在盒时常为 0xFF）
        if fr.command == vp.QUERY_BATTERY:
            left = random.randint(60, 99)
            right = random.randint(60, 99)
            case = random.randint(40, 100)
            charging = random.choice([0, 1])  # 偶尔左耳充电
            self._put(_frame(vp.HANDSHAKE_VERSION, vp.VIVO_VENDOR,
                             vp.REPORT_BATTERY, bytes([0, left, right, case, charging])))
            return
        # 降噪
        if fr.command == vp.QUERY_NOISE_MODE:
            self._put(_frame(v, vp.VIVO_VENDOR, vp.REPORT_NOISE_MODE,
                             bytes([0, int(self._noise), 0x03, 0x01])))
            return
        if fr.command == vp.SET_NOISE_MODE and fr.payload:
            self._noise = vp.NoiseMode(fr.payload[0])
            body = bytes([0, int(self._noise), 0x03, 0x01])
            self._put(_frame(v, vp.VIVO_VENDOR, vp.ACK_NOISE_MODE, body))
            self._put(_frame(v, vp.VIVO_VENDOR, vp.REPORT_NOISE_MODE, body))
            return
        # 空间音频
        if fr.command == vp.QUERY_SPATIAL_AUDIO:
            self._put(_frame(v, vp.VIVO_VENDOR, vp.REPORT_SPATIAL_AUDIO,
                             bytes([0, int(self._spatial)])))
            return
        if fr.command == vp.SET_SPATIAL_AUDIO and fr.payload:
            self._spatial = bool(fr.payload[0])
            self._put(_frame(v, vp.VIVO_VENDOR, vp.ACK_SPATIAL_AUDIO,
                             bytes([0, int(self._spatial)])))
            return
        # 低延迟
        if fr.command == vp.QUERY_LOW_LATENCY:
            self._put(_frame(v, vp.VIVO_VENDOR, vp.REPORT_LOW_LATENCY,
                             bytes([0, int(self._game)])))
            return
        if fr.command == vp.SET_LOW_LATENCY and fr.payload:
            self._game = bool(fr.payload[0])
            self._put(_frame(v, vp.VIVO_VENDOR, vp.ACK_LOW_LATENCY,
                             bytes([0, int(self._game)])))
            return
        # EQ
        if fr.command == vp.QUERY_AUDIO_EFFECT:
            self._put(_frame(v, vp.VIVO_VENDOR, vp.REPORT_AUDIO_EFFECT,
                             bytes([0, self._eq])))
            return
        if fr.command == vp.SET_AUDIO_EFFECT and fr.payload:
            self._eq = fr.payload[0]
            self._put(_frame(v, vp.VIVO_VENDOR, vp.ACK_AUDIO_EFFECT,
                             bytes([0, self._eq])))
            return
        # 查找耳机
        if fr.command == vp.SET_FIND_DEVICE and fr.payload:
            self._finding = bool(fr.payload[0])
            self._put(_frame(v, vp.VIVO_VENDOR, vp.ACK_FIND_DEVICE,
                             bytes([0, int(self._finding)])))
            return
        # 佩戴状态：双耳佩戴（bit2 左佩戴 + bit3 右佩戴 = 0x0C）
        if fr.command == vp.QUERY_WEAR_STATE:
            self._put(_frame(v, vp.VIVO_VENDOR, vp.REPORT_WEAR_STATE,
                             bytes([0, vp.WEAR_LEFT_WORN | vp.WEAR_RIGHT_WORN])))
            return
        # 固件
        if fr.command == vp.QUERY_FIRMWARE:
            self._put(_frame(v, vp.VIVO_VENDOR, vp.REPORT_FIRMWARE,
                             bytes([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x09, 0x0C])))
            return
        # 时间应答：无需回应
        # 多连接（双连）：演示同时连着手机（正在使用）与这台电脑（已连接待机）
        if fr.command == vp.QUERY_MULTI_CONNECT:
            payload = self._mc_payload()
            self._put(_frame(v, vp.VIVO_VENDOR, vp.REPORT_MULTI_CONNECT,
                             bytes(payload)))
            return
        # 实验：全量列表下发 0x014A（演示里让它真实生效，方便验证回退流程）
        if fr.command == vp.SET_MULTI_CONNECT:
            self._apply_multi_connect(fr.payload)
            return

    # ---- 多连接（双连）演示辅助 ----
    def _mc_payload(self) -> bytearray:
        devices = self._mc_devices
        payload = bytearray([0, len(devices)])
        for mac, name, status in devices:
            nb = name.encode("utf-8")
            payload += mac + b"\x00\x00\x00" + bytes([status, len(nb)]) + nb
        return payload

    def _apply_multi_connect(self, payload: bytes) -> None:
        """解析 0x014A 全量列表（每台 MAC[6]+state[1]），更新演示状态。"""
        if len(payload) < 2:
            return
        count = payload[1]
        off = 2
        by_mac = {}
        for _ in range(count):
            if off + 7 > len(payload):
                break
            mac = bytes(payload[off:off + 6])
            status = payload[off + 6]
            by_mac[mac] = status
            off += 7
        self._mc_devices = [
            (mac, name, by_mac.get(mac, status))
            for mac, name, status in self._mc_devices
        ]
