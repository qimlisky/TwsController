"""
全局热键与 Win+A 跟随。

- Ctrl+Alt+V：通过 RegisterHotKey 注册，在 Qt 主线程的 nativeEvent 里收 WM_HOTKEY。
- 跟随 Win+A：WH_KEYBOARD_LL 低级键盘钩子「只观察、不拦截」，检测到
  LWin/RWin + A 时发出信号（让系统控制中心照常弹出，浮窗稍后置顶叠加）。
  钩子只记录按键状态并立刻放行，不吞键、不影响系统行为。

非 Windows 平台为空实现（可在程序里用按钮触发同样的信号）。
"""

from __future__ import annotations

import sys

from PySide6.QtCore import QObject, QTimer, Signal

IS_WINDOWS = sys.platform == "win32"

if IS_WINDOWS:
    import ctypes
    from ctypes import wintypes

    WH_KEYBOARD_LL = 13
    WM_KEYDOWN = 0x0100
    WM_SYSKEYDOWN = 0x0104
    WM_KEYUP = 0x0101
    WM_SYSKEYUP = 0x0105
    WM_HOTKEY = 0x0312
    VK_LWIN = 0x5B
    VK_RWIN = 0x5C
    VK_A = 0x41
    VK_V = 0x56

    MOD_ALT = 0x0001
    MOD_CONTROL = 0x0002
    MOD_NOREPEAT = 0x4000

    user32 = ctypes.windll.user32
    kernel32 = ctypes.windll.kernel32

    class _KBDLLHOOKSTRUCT(ctypes.Structure):
        _fields_ = [
            ("vkCode", wintypes.DWORD),
            ("scanCode", wintypes.DWORD),
            ("flags", wintypes.DWORD),
            ("time", wintypes.DWORD),
            ("dwExtraInfo", ctypes.c_void_p),
        ]

    _LRESULT = ctypes.c_long if ctypes.sizeof(ctypes.c_void_p) == 4 else ctypes.c_longlong
    HOOKPROC = ctypes.WINFUNCTYPE(
        _LRESULT, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM
    )
    user32.SetWindowsHookExW.argtypes = [
        ctypes.c_int, HOOKPROC, wintypes.HINSTANCE, wintypes.DWORD
    ]
    user32.SetWindowsHookExW.restype = wintypes.HHOOK
    user32.CallNextHookEx.argtypes = [
        wintypes.HHOOK, ctypes.c_int, wintypes.WPARAM, wintypes.LPARAM
    ]
    user32.CallNextHookEx.restype = _LRESULT
    user32.RegisterHotKey.argtypes = [
        wintypes.HWND, ctypes.c_int, wintypes.UINT, wintypes.UINT
    ]
    user32.UnregisterHotKey.argtypes = [wintypes.HWND, ctypes.c_int]


class HotkeyManager(QObject):
    toggle_requested = Signal()       # Ctrl+Alt+V
    win_a_pressed = Signal()          # 用户按了 Win+A（系统控制中心会弹出）

    HOTKEY_ID = 0x0101

    def __init__(self, parent=None) -> None:
        super().__init__(parent)
        self._hook = None
        self._proc = None
        self._win_down = False
        self._hwnd = None
        self._follow_win_a = True
        self._registered = False        # RegisterHotKey 是否成功
        self._hook_installed = False    # 低级钩子是否安装成功
        # Win+A 之后稍等系统控制中心弹出，再叠加浮窗
        self._win_a_timer = QTimer(self)
        self._win_a_timer.setSingleShot(True)
        self._win_a_timer.setInterval(380)
        self._win_a_timer.timeout.connect(self.win_a_pressed.emit)

    # ------------------------------------------------------------------ #
    def install(self, hwnd: int) -> bool:
        """安装热键与低级钩子。返回是否成功（非 Windows 返回 False）。"""
        if not IS_WINDOWS:
            return False
        self._hwnd = hwnd
        ok = bool(user32.RegisterHotKey(
            hwnd, self.HOTKEY_ID,
            MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_V
        ))
        # 低级键盘钩子
        self._proc = HOOKPROC(self._low_level_callback)
        self._hook = user32.SetWindowsHookExW(
            WH_KEYBOARD_LL, self._proc,
            kernel32.GetModuleHandleW(None), 0
        )
        self._registered = bool(ok)
        self._hook_installed = bool(self._hook)
        return ok

    def status(self) -> dict:
        """返回热键 / 勾子的工作状态，供日志记录（任务 4c）。"""
        return {
            "hotkey_registered": self._registered,
            "keyboard_hook_installed": self._hook_installed,
            "follow_win_a": self._follow_win_a,
        }

    def uninstall(self) -> None:
        if not IS_WINDOWS:
            return
        if self._hwnd:
            user32.UnregisterHotKey(self._hwnd, self.HOTKEY_ID)
        if self._hook:
            user32.UnhookWindowsHookEx(self._hook)
            self._hook = None

    def set_follow_win_a(self, enabled: bool) -> None:
        self._follow_win_a = enabled
    def handle_native_event(self, msg) -> bool:
        """返回 True 表示消息已被消费。msg 是 Windows MSG 指针（PySide6 给 int）。"""
        if not IS_WINDOWS or msg is None:
            return False
        try:
            message = int(msg.message)
        except Exception:
            return False
        if message == WM_HOTKEY:
            self.toggle_requested.emit()
            return True
        return False

    # ------------------------------------------------------------------ #
    def _low_level_callback(self, n_code, w_param, l_param):
        try:
            if n_code >= 0 and self._follow_win_a:
                vk = self._kb_vk(l_param)
                if w_param in (WM_KEYDOWN, WM_SYSKEYDOWN):
                    if vk in (VK_LWIN, VK_RWIN):
                        self._win_down = True
                    elif vk == VK_A and self._win_down:
                        if not self._win_a_timer.isActive():
                            self._win_a_timer.start()
                elif w_param in (WM_KEYUP, WM_SYSKEYUP) and vk in (VK_LWIN, VK_RWIN):
                    self._win_down = False
        except Exception:
            pass
        return user32.CallNextHookEx(self._hook, n_code, w_param, l_param)

    @staticmethod
    def _kb_vk(l_param) -> int:
        kb = _KBDLLHOOKSTRUCT.from_address(
            ctypes.c_void_p(int(l_param)).value or 0
        )
        return int(kb.vkCode)
