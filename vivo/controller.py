"""
耳机会话控制器：后台线程负责 RFCOMM 连接、初始化序列、持续收帧与主动请求应答，
通过 Qt 信号把状态变化投递到 UI 线程。UI 只调用这里的方法，不直接碰蓝牙。
"""

from __future__ import annotations

import threading
import time
from dataclasses import dataclass
from typing import Optional

from PySide6.QtCore import QObject, Signal

from . import vivo_protocol as vp
from . import bt_local
from .demo_transport import DemoTransport
from .transport import Transport, WindowsRfcommTransport, list_paired_devices
from .vivo_features import FeatureSet, resolve as resolve_features
from .vivo_models import display_name, select_profile

# 状态未知时用 -1 传递（Qt 信号不支持 Optional[int]）
UNKNOWN = -1


@dataclass
class DeviceInfo:
    name: str
    mac: str
    is_demo: bool = False


class EarbudController(QObject):
    connected = Signal(bool)
    status = Signal(str)                       # 连接状态文本（连接中/已连接/已断开）
    battery = Signal(int, bool, int, bool, int, bool)  # 左%,左充电,右%,右充电,盒%,盒充电（-1=未知）
    noise_mode = Signal(int)                  # 0 降噪 / 1 关闭 / 2 通透
    spatial = Signal(int)                     # -1 未知 / 0 / 1
    game_mode = Signal(int)
    eq_mode = Signal(int)
    wear = Signal(str, str)                   # left/right: worn / in_case / removed
    firmware = Signal(str)
    multipoint = Signal(list)                 # 多设备（双连）[{name,status,active}, ...]
    error = Signal(str)
    log = Signal(str)

    def __init__(self) -> None:
        super().__init__()
        self.transport: Optional[Transport] = None
        self.device: Optional[DeviceInfo] = None
        self.profile = vp.FAMILY_DEFAULT_V4
        self.features: FeatureSet = resolve_features(None)
        self._decoder = vp.Decoder()
        self._thread: Optional[threading.Thread] = None
        self._stop = threading.Event()
        self._send_lock = threading.Lock()
        self._is_connected = False
        # 多设备列表（供任务 3 的断开/连接按钮使用）
        self._mc_devices: list = []

    # ------------------------------------------------------------------ #
    # 设备发现
    # ------------------------------------------------------------------ #
    def discover(self) -> list[DeviceInfo]:
        """列出已配对的 vivo/iQOO 设备（Windows 注册表）；非 Windows 返回空。"""
        try:
            from .vivo_models import is_family_name
            paired = list_paired_devices(keywords=("vivo", "iqoo"))
            out = [DeviceInfo(n, m) for n, m in paired if is_family_name(n) or
                   "vivo" in n.lower() or "iqoo" in n.lower()]
            return out
        except PermissionError as exc:
            self.error.emit(str(exc))
            return []
        except Exception as exc:  # 非 Windows 等环境
            self.log.emit(f"设备枚举不可用：{exc}")
            return []

    # ------------------------------------------------------------------ #
    # 连接管理
    # ------------------------------------------------------------------ #
    def connect_demo(self) -> None:
        self._start(DemoTransport(), DeviceInfo("vivo TWS 4（演示）", "DEMO:00:00", is_demo=True))

    def connect_device(self, info: DeviceInfo) -> None:
        transport = WindowsRfcommTransport(info.mac, profile=select_profile(info.name))
        self._start(transport, info)

    def disconnect(self) -> None:
        self._stop.set()
        t = self.transport
        if t is not None:
            try:
                t.close()
            except Exception:
                pass
        self._set_connected(False)
        self.status.emit("已断开")
        self.battery.emit(UNKNOWN, False, UNKNOWN, False, UNKNOWN, False)
        self.spatial.emit(UNKNOWN)
        self.game_mode.emit(UNKNOWN)
        self.multipoint.emit([])

    def _start(self, transport: Transport, device: DeviceInfo) -> None:
        # 停掉旧会话
        if self._thread and self._thread.is_alive():
            self.disconnect()
            self._thread.join(timeout=2)
        self.transport = transport
        self.device = device
        self.profile = select_profile(device.name)
        self.features = resolve_features(device.name)
        self._decoder.reset()
        self._stop.clear()
        self.status.emit("连接中…")
        self._thread = threading.Thread(target=self._run, name="vivo-earbud", daemon=True)
        self._thread.start()

    def _run(self) -> None:
        assert self.transport is not None
        try:
            self.transport.connect()
        except Exception as exc:
            self.error.emit(f"连接失败：{exc}")
            self.status.emit("连接失败")
            # 强制 UI 回到未连接卡片（重连失败场景）
            self._is_connected = False
            self.connected.emit(False)
            return
        self._set_connected(True)
        self.status.emit(f"已连接 · {display_name(self.device.name if self.device else None)}")
        self.log.emit(f"已连接 {self.device.name}（{self.device.mac}），profile={self.profile.label}")

        # 初始化 + 周期查询放在独立线程，保证读循环不被阻塞，
        # 耳机连接后主动发的时间请求（0x8509）等能被及时应答
        sender = threading.Thread(target=self._sender_loop, name="vivo-sender", daemon=True)
        sender.start()

        # 读循环
        while not self._stop.is_set():
            try:
                chunk = self.transport.recv(0.6)
            except Exception as exc:
                self.error.emit(f"蓝牙读取异常：{exc}")
                break
            if not chunk:
                if not self.transport.connected:
                    break
                continue
            for fr in self._decoder.offer(chunk):
                self._on_frame(fr)
        self._set_connected(False)
        self.status.emit("已断开")

    def _sender_loop(self) -> None:
        # 初始化序列（握手、注册、查询电量/降噪/固件/佩戴/EQ/空间音频/游戏）
        want_spatial = self.features.has("spatial")
        want_game = self.features.has("game")
        for cmd in vp.initial_commands(self.profile, spatial=want_spatial, game=want_game):
            if self._stop.is_set():
                return
            self._send(cmd)
            time.sleep(0.18)
        # 多设备（双连）状态：连上后主动查一次
        self._send(vp.encode_query_multi_connect(self.profile))
        # 周期性刷新电量与多设备（耳机会主动推送大部分状态，这里兜底）
        while not self._stop.is_set():
            if self._stop.wait(30):
                return
            self._send(vp.encode_query_battery())
            self._send(vp.encode_query_multi_connect(self.profile))

    def _on_frame(self, fr: vp.Frame) -> None:
        try:
            if fr.vendor == vp.GAIA_VENDOR and fr.command == vp.HANDSHAKE_RESPONSE:
                self.log.emit("握手成功")
                return
            if vp.is_peer_time_request(fr):
                # 耳机索要时间，必须应答，否则部分型号后续不推状态
                self._send(vp.encode_host_time(version=self.profile.gaia_version))
                return
            b = vp.parse_battery(fr)
            if b is not None:
                self.battery.emit(
                    b.left if b.left is not None else UNKNOWN, b.left_charging,
                    b.right if b.right is not None else UNKNOWN, b.right_charging,
                    b.case if b.case is not None else UNKNOWN, b.case_charging,
                )
                return
            mode = vp.parse_noise(fr)
            if mode is not None:
                self.noise_mode.emit(int(mode))
                return
            sp = vp.parse_spatial(fr)
            if sp is not None:
                self.spatial.emit(int(sp))
                return
            gm = vp.parse_low_latency(fr)
            if gm is not None:
                self.game_mode.emit(int(gm))
                return
            eq = vp.parse_eq(fr)
            if eq is not None:
                self.eq_mode.emit(eq)
                return
            w = vp.parse_wear(fr)
            if w is not None:
                self.wear.emit(w.left, w.right)
                return
            fw = vp.parse_firmware(fr)
            if fw is not None:
                self.firmware.emit(fw)
                return
            mc = vp.parse_multi_connect(fr)
            if mc is not None:
                self._mc_devices = list(mc.devices)
                self.multipoint.emit([
                    {
                        "name": d.name or ":".join(f"{b:02X}" for b in d.mac),
                        "status": int(d.status),
                        "active": bool(d.active),
                        "mac": ":".join(f"{b:02X}" for b in d.mac),
                    }
                    for d in mc.devices
                ])
                return
        except Exception as exc:  # 单帧解析失败不应杀线程
            self.log.emit(f"帧处理异常：{exc}")

    # ------------------------------------------------------------------ #
    # 控制指令（均在 UI 线程被调用，transport.send 自身加锁）
    # ------------------------------------------------------------------ #
    def _send(self, data: bytes) -> None:
        if self.transport is None or not self._is_connected:
            self.log.emit("未连接，指令已忽略")
            return
        try:
            with self._send_lock:
                self.transport.send(data)
        except Exception as exc:
            self.error.emit(f"发送失败：{exc}")

    def set_noise(self, mode: int) -> None:
        self._send(vp.encode_set_noise(vp.NoiseMode(mode), self.profile))

    def set_spatial(self, on: bool) -> None:
        self._send(vp.encode_set_spatial(on, self.profile))

    def set_game(self, on: bool) -> None:
        self._send(vp.encode_set_low_latency(on, self.profile))

    def set_eq(self, code: int) -> None:
        self._send(vp.encode_set_eq(code, self.profile))

    def find(self, start: bool) -> None:
        self._send(vp.encode_find_device(start, self.profile))

    def local_mac_set(self) -> set:
        """返回本机蓝牙适配器的 MAC 集合（大写），用于识别多设备列表里的“本机”。

        非 Windows 或无适配器时返回空集合，UI 会退回到按名称判断。
        """
        out = set()
        try:
            for r in bt_local.list_radios():
                mac = (r.get("mac") or "").upper()
                if mac:
                    out.add(mac)
        except Exception:
            pass
        return out

    def is_local_device(self, name: str, mac: str = "") -> bool:
        """判断多设备列表中的某一条是否就是“本机”。

        优先用本机适配器 MAC 比对；Windows API 不可用时退回到名称启发
        （“这台电脑” / “本机” / 机器主机名）。
        """
        m = (mac or "").replace("-", ":").upper()
        if m:
            local = self.local_mac_set()
            if m in local:
                return True
        n = (name or "").strip().lower()
        if not n:
            return False
        if "这台电脑" in n or "本机" in n:
            return True
        try:
            import socket
            host = socket.gethostname().strip().lower()
            if host and host in n:
                return True
        except Exception:
            pass
        return False

    # ------------------------------------------------------------------ #
    # 多设备连接控制（任务 3）
    #
    # 两条路径：
    #  1) 本机（这台电脑）：走 Windows 标准蓝牙接口（bt_local），可靠；
    #  2) 其它设备：走私有 0x014A 全量下发，**实验性**（未在真机验证）。
    # ------------------------------------------------------------------ #
    def local_disconnect(self, mac: str) -> bool:
        """断开本机与该设备的连接（Windows 标准接口，非私有协议）。

        注意：这是本机侧操作，只影响这台电脑；需在真机确认效果。
        """
        res = bt_local.disconnect_local(mac)
        self.log.emit(f"[本机断开] {res.detail}")
        return bool(res.ok)

    def local_connect(self, mac: str) -> bool:
        """重新连接本机与该设备（Windows 标准接口，非私有协议）。"""
        res = bt_local.connect_local(mac)
        self.log.emit(f"[本机连接] {res.detail}")
        return bool(res.ok)

    def set_multi_connect_status(self, mac: str, connect: bool,
                                 timeout: float = 2.0) -> None:
        """实验性：通过私有命令 0x014A 翻转某台设备的连接状态。

        ⚠️ 协议层说明（如实告知，不夸大）：
        - 0x014A 是「全量列表下发」（每台 MAC[6]+state[1]），不是单台指令；
        - 上游只在单元测试里调用过该编码器，**产品代码从不调用**，
          也无「真机断开成功」的验证记录；ENABLE/REMOVE(0x014C/0x014D)
          更没有实现。因此这里标记为实验功能，可能不生效。

        流程：下发全量列表 → 等 timeout 秒 → 重新查询 0x0249，
        由上报结果自然刷新 UI；本方法不谎报成功。
        """
        if not self._mc_devices:
            self.log.emit("[实验] 无多设备列表，忽略切换请求。")
            return
        target = mac.replace("-", ":").upper()
        new_status = (vp.MC_CONNECTED_INACTIVE if connect
                      else vp.MC_DISCONNECTED)
        devices = []
        hit = False
        for d in self._mc_devices:
            cur = ":".join(f"{b:02X}" for b in d.mac)
            if cur.upper() == target:
                hit = True
                devices.append(vp.MultiConnectDevice(
                    mac=d.mac, raw3=d.raw3, status=int(new_status),
                    name=d.name))
            else:
                devices.append(d)
        if not hit:
            self.log.emit(f"[实验] 列表中找不到设备 {mac}，忽略。")
            return
        act = "连接" if connect else "断开"
        self._send(vp.encode_set_multi_connect(devices, self.profile))
        self.log.emit(f"[实验] 已下发 0x014A 全量列表（{act} {mac}）；"
                      f"{timeout:.1f}s 后回查 0x0249 确认。")
        # 延迟回查：超时后用一次查询让上报自然刷新状态
        def _recheck() -> None:
            if self._stop.wait(timeout):
                return
            self._send(vp.encode_query_multi_connect(self.profile))
        threading.Thread(target=_recheck, name="vivo-mc-recheck",
                         daemon=True).start()

    def _set_connected(self, value: bool) -> None:
        if self._is_connected != value:
            self._is_connected = value
            self.connected.emit(value)
