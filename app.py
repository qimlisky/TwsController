"""
vivo 耳机控制台（Windows）启动入口。

用法：
    python app.py            # 托盘常驻；有上次设备则自动连接，否则弹出浮窗
    python app.py --demo     # 直接进入演示模式（无需耳机、无需 Windows）

热键：
    Ctrl + Alt + V           唤出 / 收起浮窗
    Win + A                  系统控制中心弹出后，浮窗自动叠加在其上方（可关闭）
"""

from __future__ import annotations

import sys

from PySide6.QtCore import QSettings, QTimer, Qt
from PySide6.QtWidgets import QApplication

from vivo.controller import DeviceInfo, EarbudController
from ui.ccwatch import ControlCenterWatcher
from ui.flyout import FlyoutWindow
from ui.hotkey import HotkeyManager, IS_WINDOWS
from ui.tray import TrayController, build_app_icon


SETTINGS_ORG = "VivoPodsCC"
SETTINGS_APP = "config"


class VivoPodsApp:
    def __init__(self, argv: list[str]) -> None:
        QApplication.setHighDpiScaleFactorRoundingPolicy(
            Qt.HighDpiScaleFactorRoundingPolicy.PassThrough
        )
        self.qt = QApplication(argv)
        self.qt.setApplicationName("VivoPodsCC")
        self.qt.setQuitOnLastWindowClosed(False)
        self.qt.aboutToQuit.connect(self.shutdown)
        self.settings = QSettings(
            QSettings.IniFormat, QSettings.UserScope, SETTINGS_ORG, SETTINGS_APP
        )

        self.ctrl = EarbudController()
        self.flyout = FlyoutWindow(self.ctrl, self.settings)
        self.flyout.hotkeys = None

        self.tray = TrayController(self.qt, self.flyout, self.ctrl, self.settings)
        self.tray.show()

        # 全局热键 / Win+A 跟随
        self.hotkeys = HotkeyManager()
        self.hotkeys.set_follow_win_a(
            self.settings.value("follow_win_a", True, type=bool)
        )
        self.hotkeys.win_a_pressed.connect(self.flyout.show_after_win_a)
        self.hotkeys.toggle_requested.connect(self.flyout.toggle_flyout)
        self.flyout.hotkeys = self.hotkeys
        self.flyout.quit_callback = self.shutdown
        self.tray.quit_callback = self.shutdown
        if IS_WINDOWS:
            self._install_hotkeys()

        # 系统控制中心跟随：打开 → 叠加在其正上方；关闭 → 同步收起
        self.cc_watch = ControlCenterWatcher()
        self.cc_watch.log_enabled = self.settings.value(
            "ccwatch_log", True, type=bool)
        self.cc_watch.opened.connect(self.flyout.show_for_control_center)
        self.cc_watch.closed.connect(self.flyout.hide_for_control_center)
        self.flyout.cc_watch_callback = self._set_cc_watch
        self.flyout.cc_log_callback = self._set_cc_log
        if IS_WINDOWS and self.settings.value(
            "follow_control_center", True, type=bool
        ):
            self.cc_watch.start()
        # 记录 Win+A 热键兜底路径的工作状态（任务 4c）
        self._report_hotkey_status()

        # 连接成功后记住设备
        self.ctrl.connected.connect(self._on_connected)

        # 启动策略
        self._bootstrap("--demo" in argv)

    def _install_hotkeys(self) -> None:
        try:
            hwnd = int(self.flyout.winId())
            self.hotkeys.install(hwnd)
        except Exception as exc:
            print(f"热键安装失败：{exc}", file=sys.stderr)

    def _report_hotkey_status(self) -> None:
        """把 Win+A / Ctrl 热键的工作状态写进控制中心日志（任务 4c）。"""
        try:
            st = self.hotkeys.status()
            detail = (f"hotkey_registered={st.get('hotkey_registered')}, "
                      f"keyboard_hook_installed={st.get('keyboard_hook_installed')}, "
                      f"follow_win_a={st.get('follow_win_a')}")
            ok = bool(st.get("hotkey_registered")) and \
                bool(st.get("keyboard_hook_installed"))
            self.cc_watch.note_hotkey(ok, detail)
        except Exception as exc:
            try:
                self.cc_watch.note_hotkey(False, f"状态读取异常：{exc}")
            except Exception:
                pass

    def _bootstrap(self, demo: bool) -> None:
        if demo:
            self.ctrl.connect_demo()
            self.flyout.show_flyout()
            return
        last_mac = self.settings.value("last_device_mac", "")
        last_name = self.settings.value("last_device_name", "")
        if last_mac and last_name:
            self.ctrl.connect_device(DeviceInfo(last_name, last_mac))
        else:
            # 首次启动：展示浮窗引导连接
            self.flyout.show_flyout()

    def _on_connected(self, ok: bool) -> None:
        if ok and self.ctrl.device and not self.ctrl.device.is_demo:
            self.settings.setValue("last_device_mac", self.ctrl.device.mac)
            self.settings.setValue("last_device_name", self.ctrl.device.name)

    def _set_cc_watch(self, enabled: bool) -> None:
        """托盘 / 浮窗里切换「跟随系统控制中心」时启停检测器。"""
        if not IS_WINDOWS:
            return
        try:
            if enabled:
                self.cc_watch.start()
            else:
                self.cc_watch.stop()
        except Exception:
            pass

    def _set_cc_log(self, enabled: bool) -> None:
        """切换控制中心检测日志写入。"""
        try:
            self.cc_watch.log_enabled = bool(enabled)
            self.cc_watch._log(f"日志开关切换为：{bool(enabled)}")
        except Exception:
            pass

    def shutdown(self) -> None:
        try:
            self.cc_watch.stop()
        except Exception:
            pass
        try:
            self.hotkeys.uninstall()
        except Exception:
            pass
        try:
            self.ctrl.disconnect()
        except Exception:
            pass
        self.qt.quit()

    def run(self) -> int:
        return self.qt.exec()


def main() -> None:
    app = VivoPodsApp(sys.argv)
    sys.exit(app.run())


if __name__ == "__main__":
    main()
