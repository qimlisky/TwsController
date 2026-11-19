"""
v3.1 回归验证（offscreen Qt，无需真机 / 无需耳机）：

  任务 B：折叠态一屏放下降噪切换、无滚动需求、降噪控件完整落在视口内；
  任务 C：充电盒电量「记忆 + 上次更新时间」在无 WEAR 上报时仍生效。

运行：QT_QPA_PLATFORM=offscreen python tests/verify_v31.py
"""
import json
import os
import sys

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from PySide6.QtCore import QSettings, QTimer
from PySide6.QtWidgets import QApplication

from ui.flyout import FlyoutWindow
from vivo.controller import DeviceInfo, EarbudController
from vivo.vivo_features import resolve as resolve_features

INI = "/tmp/vivo_v31_verify.ini"


def settle(app, n=6):
    for _ in range(n):
        app.processEvents()
        QTimer.singleShot(0, lambda: None)
    app.processEvents()


def build(device="vivo TWS 5", collapsed=True, settings_path=INI):
    app = QApplication.instance() or QApplication(sys.argv)
    for p in (settings_path,):
        if os.path.exists(p):
            os.remove(p)
    st = QSettings(settings_path, QSettings.IniFormat)
    ctrl = EarbudController()
    ctrl.device = DeviceInfo(device, "AA:BB:CC:DD:EE:FF")
    ctrl.features = resolve_features(device)
    w = FlyoutWindow(ctrl, st)
    w._connected = True
    w.disconnect_card.setVisible(False)
    w.scroll.setVisible(True)
    w._apply_collapsed(collapsed)
    w.show()
    settle(app)
    return app, ctrl, w, st


def measure(w):
    app = QApplication.instance()
    settle(app)
    sb = w.scroll.verticalScrollBar()
    vp = w.scroll.viewport()
    seg = w.collapsed_noise_seg
    sg = seg.geometry()
    return {
        "window_height": w.height(),
        "window_sizeHint_height": w.sizeHint().height(),
        "scroll_max": sb.maximum(),
        "scroll_viewport_h": vp.height(),
        "seg_visible": seg.isVisible(),
        "seg_geom[x,y,w,h]": [sg.x(), sg.y(), sg.width(), sg.height()],
        "seg_bottom": sg.y() + sg.height(),
        "battery_pill_h": w.bt_l.height(),
        "bt_c_geom[x,y,w,h]": [w.bt_c.geometry().x(), w.bt_c.geometry().y(),
                               w.bt_c.geometry().width(), w.bt_c.geometry().height()],
        "seg_fits_in_viewport": (sg.y() + sg.height()) <= vp.height(),
        "no_scroll_needed": sb.maximum() == 0,
    }


def case_semantics(ctrl, w):
    app = QApplication.instance()
    r = {}
    w._on_battery(90, False, 90, False, 65, False)   # 有效读数 65
    settle(app, 2)
    r["valid_65_shown"] = w.bt_c.value.text()
    r["caption_after_65"] = w.bt_c.caption.text()
    w._on_battery(88, False, 89, False, -1, False)   # 出仓，读不到
    settle(app, 2)
    r["out_of_case_shown"] = w.bt_c.value.text()     # 必须仍是 65%（不清空）
    r["out_of_case_caption"] = w.bt_c.caption.text()
    w._on_battery(80, False, 81, False, 62, False)   # 新的有效读数
    settle(app, 2)
    r["new_valid_62_shown"] = w.bt_c.value.text()
    r["persisted_snapshot"] = w.settings.value("case_battery_snapshot")
    r["persisted_updated_at"] = w.settings.value("case_battery_updated_at")
    r["left_live"] = w.bt_l.value.text()
    r["right_live"] = w.bt_r.value.text()
    # 重启：新窗口读同一 QSettings
    w.settings.sync()
    st2 = QSettings(INI, QSettings.IniFormat)
    w2 = FlyoutWindow(ctrl, st2)
    w2._connected = True
    w2._refresh_case_display()
    r["restart_shown"] = w2.bt_c.value.text()
    r["restart_caption"] = w2.bt_c.caption.text()
    # 从未收到读数：显示“未获取”
    st3 = QSettings("/tmp/vivo_v31_empty.ini", QSettings.IniFormat)
    if os.path.exists("/tmp/vivo_v31_empty.ini"):
        os.remove("/tmp/vivo_v31_empty.ini")
    st3 = QSettings("/tmp/vivo_v31_empty.ini", QSettings.IniFormat)
    w3 = FlyoutWindow(ctrl, st3)
    w3._connected = True
    w3._refresh_case_display()
    r["never_received_shown"] = w3.bt_c.value.text()
    return r


def main():
    app, ctrl, w, st = build("vivo TWS 5", collapsed=True)
    b = measure(w)
    print("=== B: collapsed layout (vivo TWS 5) ===")
    print(json.dumps(b, ensure_ascii=False, indent=2))
    okB = b["no_scroll_needed"] and b["seg_fits_in_viewport"] and b["seg_visible"]
    print("TASK_B_PASS =", okB)

    c = case_semantics(ctrl, w)
    print("=== C: case battery memory (no WEAR events, TWS5) ===")
    print(json.dumps(c, ensure_ascii=False, indent=2))
    okC = (c["out_of_case_shown"] == "65%"
           and c["new_valid_62_shown"] == "62%"
           and str(c["persisted_snapshot"]) == "62"
           and c["restart_shown"] == "62%"
           and c["left_live"] == "80%"
           and c["never_received_shown"] == "未获取")
    print("TASK_C_PASS =", okC)

    # 回归 1：无 ANC 型号（iQOO TWS Air）折叠态应隐藏降噪控件、窗口正常
    app2, ctrl2, w2, st2 = build("iQOO TWS Air", collapsed=True,
                                 settings_path="/tmp/vivo_v31_air.ini")
    reg1 = {
        "collapsed_noise_seg_visible": w2.collapsed_noise_seg.isVisible(),
        "window_height": w2.height(),
        "scroll_max": w2.scroll.verticalScrollBar().maximum(),
    }
    print("=== regression: iQOO TWS Air (no ANC) collapsed ===")
    print(json.dumps(reg1, ensure_ascii=False, indent=2))
    okR1 = (reg1["collapsed_noise_seg_visible"] is False
            and reg1["scroll_max"] == 0)

    # 回归 2：展开态仍固定高度、可滚动、多设备分区可见
    app3, ctrl3, w3, st3 = build("vivo TWS 5", collapsed=False,
                                 settings_path="/tmp/vivo_v31_exp.ini")
    w3._on_multipoint([
        {"name": "这台电脑", "status": 2, "active": True, "mac": "11:22:33:44:55:66"},
        {"name": "vivo X200 Pro", "status": 1, "active": False, "mac": "AA:BB:CC:11:22:33"},
        {"name": "旧手机", "status": 0, "active": False, "mac": "DE:AD:BE:EF:00:01"},
    ])
    settle(app3)
    reg2 = {
        "expanded_height": w3.height(),
        "multi_label_visible": w3.multi_label.isVisible(),
        "multi_box_visible": w3.multi_box.isVisible(),
        "multi_rows": len(w3._multi_rows),
        "noise_seg_visible": w3.noise_seg.isVisible(),
        "battery_pill_h": w3.bt_l.height(),
    }
    print("=== regression: expanded state ===")
    print(json.dumps(reg2, ensure_ascii=False, indent=2))
    okR2 = (reg2["expanded_height"] == 540 and reg2["multi_label_visible"]
            and reg2["multi_rows"] == 3 and reg2["battery_pill_h"] == 78)

    print("ALL_PASS =", bool(okB and okC and okR1 and okR2))


if __name__ == "__main__":
    main()
