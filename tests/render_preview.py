"""
无头环境渲染检查：用 offscreen 平台实例化浮窗、跑演示会话并截图，
检查 UI 是否正常构建、控件是否在可视范围内。
"""

import os
import sys

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from PySide6.QtCore import QSettings, QTimer
from PySide6.QtWidgets import QApplication

from vivo.controller import EarbudController
from ui.flyout import FlyoutWindow

OUT = os.path.dirname(os.path.abspath(__file__))

app = QApplication([])
settings = QSettings(QSettings.IniFormat, QSettings.UserScope, "VivoPodsCC", "test")
settings.setValue("dark", True)
settings.setValue("follow_win_a", False)

ctrl = EarbudController()
win = FlyoutWindow(ctrl, settings)
win.resize(372, 540)
win.show()
ctrl.connect_demo()


def capture_dark():
    # 操作：开空间音频、选通透、选 EQ
    win._on_spatial_state(1)
    win._on_game_state(1)
    win.noise_seg.set_selected(2)
    win._on_eq_state(2)
    win._on_battery(85, False, 94, True, 72, False)
    app.processEvents()
    pm = win.grab()
    p1 = os.path.join(OUT, "preview_dark.png")
    pm.save(p1)
    print("saved", p1, pm.size().width(), pm.size().height())

    # 检查所有子控件是否在面板边界内
    overflow = []
    for child in win.findChildren(__import__("PySide6.QtWidgets", fromlist=["QWidget"]).QWidget):
        if child.isVisible():
            r = child.geometry()
            if r.right() > 372 or r.bottom() > 600:
                overflow.append((type(child).__name__, r))
    print("overflow widgets:", overflow[:5])

    # 浅色 + 未连接
    win.dark = False
    win.apply_theme()
    ctrl.disconnect()
    app.processEvents()
    pm2 = win.grab()
    p2 = os.path.join(OUT, "preview_light.png")
    pm2.save(p2)
    print("saved", p2)
    app.quit()


QTimer.singleShot(2600, capture_dark)
sys.exit(app.exec())
