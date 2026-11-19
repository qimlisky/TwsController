"""
系统托盘：常驻图标、左键唤出浮窗、右键快捷菜单（设备、演示、主题、自启、退出）。
"""

from __future__ import annotations

import sys

from PySide6.QtCore import QPoint, Qt
from PySide6.QtGui import QAction, QActionGroup, QIcon, QPainter, QPixmap
from PySide6.QtWidgets import QMenu, QSystemTrayIcon

from .theme import ICONS
from .widgets import svg_icon
from PySide6.QtCore import QRectF

IS_WINDOWS = sys.platform == "win32"


def build_app_icon() -> QIcon:
    pm = QPixmap(64, 64)
    pm.fill(Qt.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.Antialiasing)
    svg_icon("headphone", "#FFFFFF", 48).render(p, QRectF(8, 8, 48, 48))
    p.end()
    return QIcon(pm)


class TrayController(QSystemTrayIcon):
    def __init__(self, app, flyout, controller, settings, parent=None) -> None:
        super().__init__(parent)
        self.app = app
        self.flyout = flyout
        self.ctrl = controller
        self.settings = settings
        self.setIcon(build_app_icon())
        self.setToolTip("vivo 耳机控制台")
        self.activated.connect(self._on_activated)

        menu = QMenu()
        show_act = QAction("显示控制面板", menu)
        show_act.triggered.connect(lambda: flyout.show_flyout())
        menu.addAction(show_act)

        self.device_menu = menu.addMenu("连接设备")
        self.devices_note = QAction("点击刷新", menu)
        self.devices_note.triggered.connect(self.refresh_devices)
        self.device_menu.addAction(self.devices_note)
        self.device_menu.aboutToShow.connect(self.refresh_devices)

        demo_act = QAction("演示模式（无需耳机）", menu)
        demo_act.triggered.connect(controller.connect_demo)
        menu.addAction(demo_act)
        menu.addSeparator()

        theme_act = QAction("深色模式", menu, checkable=True)
        theme_act.setChecked(settings.value("dark", True, type=bool))
        theme_act.triggered.connect(flyout.toggle_theme)
        self.theme_act = theme_act
        menu.addAction(theme_act)

        follow_act = QAction("跟随 Win+A 弹出", menu, checkable=True)
        follow_act.setChecked(settings.value("follow_win_a", True, type=bool))
        follow_act.triggered.connect(lambda on: flyout._toggle_follow(on))
        menu.addAction(follow_act)

        cc_act = QAction("跟随系统控制中心弹出 / 关闭", menu, checkable=True)
        cc_act.setChecked(settings.value("follow_control_center", True, type=bool))
        cc_act.triggered.connect(flyout.set_follow_control_center)
        menu.addAction(cc_act)

        close_menu = menu.addMenu("关闭按钮行为")
        close_grp = QActionGroup(menu)
        close_grp.setExclusive(True)
        current = settings.value("close_action", "tray", type=str)
        for key, text in (("tray", "最小化到托盘（后台常驻）"),
                          ("exit", "直接退出程序")):
            act = QAction(text, menu, checkable=True)
            act.setChecked(current == key)
            act.triggered.connect(lambda _=False, k=key: flyout.set_close_action(k))
            close_grp.addAction(act)
            close_menu.addAction(act)

        auto_act = QAction("开机自启动", menu, checkable=True)
        auto_act.setChecked(self._autostart_enabled())
        auto_act.triggered.connect(self._toggle_autostart)
        if not IS_WINDOWS:
            auto_act.setEnabled(False)
        self.auto_act = auto_act
        menu.addAction(auto_act)

        menu.addSeparator()
        quit_act = QAction("退出", menu)
        quit_act.triggered.connect(self._quit)
        menu.addAction(quit_act)
        self.setContextMenu(menu)

    def _on_activated(self, reason) -> None:
        if reason == QSystemTrayIcon.Trigger:
            self.flyout.toggle_flyout()

    def refresh_devices(self) -> None:
        self.device_menu.clear()
        devices = self.ctrl.discover()
        if devices:
            for d in devices:
                act = QAction(d.name, self.device_menu)
                act.triggered.connect(lambda _=False, info=d: self.ctrl.connect_device(info))
                self.device_menu.addAction(act)
        else:
            act = QAction("未发现已配对的 vivo / iQOO 耳机", self.device_menu)
            act.setEnabled(False)
            self.device_menu.addAction(act)

    # ------------------------------------------------------------------ #
    @staticmethod
    def _autostart_key():
        if not IS_WINDOWS:
            return None
        import winreg
        return (winreg.HKEY_CURRENT_USER,
                r"Software\Microsoft\Windows\CurrentVersion\Run")

    def _autostart_enabled(self) -> bool:
        key = self._autostart_key()
        if key is None:
            return False
        import winreg
        try:
            with winreg.OpenKey(*key) as k:
                winreg.QueryValueEx(k, "VivoPodsCC")
                return True
        except OSError:
            return False

    def _toggle_autostart(self, on: bool) -> None:
        key = self._autostart_key()
        if key is None:
            return
        import winreg
        with winreg.OpenKey(key[0], key[1], 0, winreg.KEY_SET_VALUE) as k:
            if on:
                exe = sys.executable
                if not getattr(sys, "frozen", False):
                    # 源码运行：pythonw + app.py；打包成 EXE 后用自身路径
                    import os
                    exe = f'"{exe}" "{os.path.abspath(sys.argv[0])}"'
                winreg.SetValueEx(k, "VivoPodsCC", 0, winreg.REG_SZ, exe)
            else:
                try:
                    winreg.DeleteValue(k, "VivoPodsCC")
                except FileNotFoundError:
                    pass

    def _quit(self) -> None:
        cb = getattr(self, "quit_callback", None)
        if callable(cb):
            cb()
        else:
            from PySide6.QtWidgets import QApplication
            QApplication.quit()
