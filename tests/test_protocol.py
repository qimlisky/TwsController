"""
协议层与演示传输的零硬件测试。直接运行：

    python tests/test_protocol.py

全部通过即代表帧编解码、命令构造、回包解析、会话交互与真机抓包记录一致。
"""

import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from vivo import vivo_protocol as vp
from vivo.demo_transport import DemoTransport
from vivo.vivo_models import is_family_name, select_profile
from vivo.vivo_features import resolve

PASS = 0
FAIL = 0


def check(name: str, cond: bool, detail: str = "") -> None:
    global PASS, FAIL
    if cond:
        PASS += 1
        print(f"  [PASS] {name}")
    else:
        FAIL += 1
        print(f"  [FAIL] {name}  {detail}")


def collect(transport: DemoTransport, dec: vp.Decoder, tries: int = 8):
    """把当前队列里的全部响应帧收集出来（复用同一个流式解码器）。"""
    frames = []
    for _ in range(tries):
        chunk = transport.recv(0.05)
        if not chunk:
            break
        frames.extend(dec.offer(chunk))
    return frames


def test_frames() -> None:
    print("== 帧编解码 ==")
    # 握手帧：v4 / GAIA vendor 0x000A / 0x0300
    h = vp.encode_handshake()
    check("握手帧字节", h.hex().upper() == "FF040000000A0300", h.hex())
    # 电量查询恒 v4
    b = vp.encode_query_battery()
    check("电量查询帧", b.hex().upper() == "FF040000001B0207", b.hex())
    # TWS3e 降噪设置：v3, [mode, 03]
    f = vp.encode_set_noise(vp.NoiseMode.TRANSPARENCY, vp.TWS_3E_V3)
    check("TWS3e 通透设置", f.hex().upper() == "FF030002001B01300203", f.hex())
    # Air3 Pro 降噪：v3, [mode, 04, 00]
    f = vp.encode_set_noise(vp.NoiseMode.ANC, vp.AIR3_PRO_CAPTURED)
    check("Air3Pro 降噪设置", f.hex().upper() == "FF030003001B0130000400", f.hex())
    # 家族默认 v4：[mode, 03, 01]
    f = vp.encode_set_noise(vp.NoiseMode.OFF, vp.FAMILY_DEFAULT_V4)
    check("家族v4 关闭设置", f.hex().upper() == "FF040003001B0130010301", f.hex())
    # 空间音频开关
    f = vp.encode_set_spatial(True, vp.FAMILY_DEFAULT_V4)
    check("空间音频开", f.hex().upper() == "FF040001001B013901", f.hex())
    f = vp.encode_set_spatial(False, vp.FAMILY_DEFAULT_V4)
    check("空间音频关", f.hex().upper() == "FF040001001B013900", f.hex())
    # 查找耳机响铃
    f = vp.encode_find_device(True, vp.FAMILY_DEFAULT_V4)
    check("查找响铃", f.hex().upper() == "FF040001001B012001", f.hex())
    # 时间应答：2026-08-06 11:12:29 → 14 1a 08 06 0b 0c 1d
    from datetime import datetime
    f = vp.encode_host_time(datetime(2026, 8, 6, 11, 12, 29), version=4)
    check("时间应答", f.hex().upper().endswith("001B0509141A08060B0C1D"), f.hex())


def test_parse() -> None:
    print("== 回包解析 ==")
    dec = vp.Decoder()
    # 电量：85% 94% 仓无效(FF)，未充电
    frames = dec.offer(bytes.fromhex("FF040005001B820700555EFF00"))
    s = vp.parse_battery(frames[0])
    check("电量解析", s is not None and s.left == 85 and s.right == 94 and
          s.case is None and not s.left_charging, str(s))
    # 降噪：通透
    frames = dec.offer(bytes.fromhex("FF030004001B823000020301"))
    m = vp.parse_noise(frames[0])
    check("降噪模式=通透", m == vp.NoiseMode.TRANSPARENCY, str(m))
    # 空间音频回包 [00][01]
    frames = dec.offer(bytes.fromhex("FF040002001B82390001"))
    check("空间音频开解析", vp.parse_spatial(frames[0]) is True)
    # 游戏模式 [00][00]
    frames = dec.offer(bytes.fromhex("FF040002001B82510000"))
    check("游戏模式关解析", vp.parse_low_latency(frames[0]) is False)
    # EQ
    frames = dec.offer(bytes.fromhex("FF040002001B82180002"))
    check("EQ=超重低音", vp.parse_eq(frames[0]) == 0x02)
    # 查找 ACK
    frames = dec.offer(bytes.fromhex("FF040002001B81200001"))
    check("查找响铃ACK", vp.parse_find(frames[0]) is True)
    # 双耳佩戴：flags=0x0C
    frames = dec.offer(bytes.fromhex("FF040002001B820D000C"))
    w = vp.parse_wear(frames[0])
    check("双耳佩戴", w is not None and w.left == "worn" and w.right == "worn", str(w))
    # 固件 00 01 02 03 04 05 09 0C → 2.5.9
    frames = dec.offer(bytes.fromhex("FF040008001B821C000102030405090C"))
    check("固件版本 2.5.9", vp.parse_firmware(frames[0]) == "2.5.9",
          str(vp.parse_firmware(frames[0])))
    # 多设备（双连）：两台主机，PC1 使用中 / 手机待机
    frames = dec.offer(bytes.fromhex(
        "FF040021001B8249" + "0002" +
        "AABBCC1122330000000203504331" +
        "1122334455660000000104E6898BE69CBA"))
    mc = vp.parse_multi_connect(frames[0])
    check("多设备（双连）解析",
          mc is not None and len(mc.devices) == 2
          and mc.devices[0].name == "PC1" and mc.devices[0].active
          and mc.devices[1].status == vp.MC_CONNECTED_INACTIVE,
          str(mc))


def test_demo_session() -> None:
    print("== Demo 会话全流程 ==")
    t = DemoTransport()
    t.connect()
    dec = vp.Decoder()
    # 先取走 connect 时耳机主动推送的时间请求
    collect(t, dec)
    frames = []
    # 初始查询
    for cmd in vp.initial_commands(vp.FAMILY_DEFAULT_V4, spatial=True, game=True):
        t.send(cmd)
        frames.extend(collect(t, dec, tries=3))
    check("收到握手 ACK", any(f.command == 0x8300 for f in frames))
    check("收到电量上报", any(f.command == 0x8207 for f in frames))
    check("收到降噪上报", any(f.command == 0x8230 for f in frames))
    check("收到空间音频上报", any(f.command == 0x8239 for f in frames))
    check("收到固件上报", any(f.command == 0x821C for f in frames))

    # 切降噪 → 应有 ACK 与 REPORT
    t.send(vp.encode_set_noise(vp.NoiseMode.ANC, vp.FAMILY_DEFAULT_V4))
    r = collect(t, dec)
    modes = [vp.parse_noise(f) for f in r if vp.parse_noise(f) is not None]
    check("切降噪后回包=降噪", modes and all(m == vp.NoiseMode.ANC for m in modes), str(modes))

    # 开空间音频
    t.send(vp.encode_set_spatial(True, vp.FAMILY_DEFAULT_V4))
    r = collect(t, dec)
    vals = [vp.parse_spatial(f) for f in r if vp.parse_spatial(f) is not None]
    check("开空间音频 ACK", vals and vals[0] is True, str(vals))

    # 连接后耳机主动索要时间（connect 放入队列）
    t2 = DemoTransport()
    t2.connect()
    dec2 = vp.Decoder()
    first = collect(t2, dec2)
    check("耳机主动索时间", any(vp.is_peer_time_request(f) for f in first), str(first))


def test_models_features() -> None:
    print("== 型号与能力 ==")
    check("家族识别", is_family_name("vivo TWS 4 Hi-Fi"))
    check("iQOO 识别", is_family_name("iQOO TWS 2"))
    check("非家族排除", not is_family_name("Sony WH-1000XM5"))
    check("TWS3e 用 v3", select_profile("vivo TWS 3e").gaia_version == 3)
    check("TWS5 用 v4", select_profile("vivo TWS 5").gaia_version == 4)
    fs = resolve("vivo TWS 5")
    check("TWS5 支持空间音频", fs.has("spatial"))
    check("TWS5 空间音频非强制", fs.visible("spatial"))
    check("TWS3e 不显示空间音频", not resolve("vivo TWS 3e").has("spatial"))
    check("TWS3e 双连强制不显示控件", not resolve("vivo TWS 3e").visible("dual"))


if __name__ == "__main__":
    test_frames()
    test_parse()
    test_demo_session()
    test_models_features()
    print(f"\n结果：{PASS} 通过 / {FAIL} 失败")
    sys.exit(1 if FAIL else 0)
