"""
Windows 11 窗口特效：亚克力（Acrylic）背景 + 圆角 + 深色模式。

两条路径，均在 Windows 上可用，失败时静默回退（Qt 自绘半透明背景）：
1. Win11：DwmSetWindowAttribute 的 SYSTEMBACKDROP_TYPE（38）= Acrylic(3)，
   并设置 WINDOW_CORNER_PREFERENCE（33）= Round(2)。
2. Win10/11：未文档化但广泛使用的 user32!SetWindowCompositionAttribute，
   ACCENT_ENABLE_ACRYLICBLURBEHIND(4)。

非 Windows 平台为空操作，便于在其他系统开发时查看 UI（QSS 半透明兜底）。
"""

from __future__ import annotations

import sys

IS_WINDOWS = sys.platform == "win32"

if IS_WINDOWS:
    import ctypes
    from ctypes import wintypes

    # DWM 特性常量
    DWMWA_USE_IMMERSIVE_DARK_MODE = 20
    DWMWA_WINDOW_CORNER_PREFERENCE = 33
    DWMWA_SYSTEMBACKDROP_TYPE = 38
    DWMWA_BORDER_COLOR = 34

    DWMWCP_ROUND = 2
    DWMSBT_TRANSIENTWINDOW = 3   # Acrylic
    DWMWA_COLOR_NONE = 0xFFFFFFFE

    WCA_ACCENT_POLICY = 19
    ACCENT_ENABLE_ACRYLICBLURBEHIND = 4

    class _ACCENTPOLICY(ctypes.Structure):
        _fields_ = [
            ("AccentState", ctypes.c_int),
            ("AccentFlags", ctypes.c_int),
            ("GradientColor", ctypes.c_uint),
            ("AnimationId", ctypes.c_int),
        ]

    class _WINCOMPATTRDATA(ctypes.Structure):
        _fields_ = [
            ("nAttribute", ctypes.c_int),
            ("pData", ctypes.c_void_p),
            ("ulDataSize", ctypes.c_ulong),
        ]

    _dwm = ctypes.windll.dwmapi
    _user32 = ctypes.windll.user32
    _dwm.DwmSetWindowAttribute.argtypes = [
        wintypes.HWND, ctypes.c_uint, ctypes.c_void_p, ctypes.c_uint
    ]
    _dwm.DwmSetWindowAttribute.restype = ctypes.c_long


def _set_dwm(hwnd: int, attr: int, value: int) -> None:
    data = ctypes.c_int(value)
    _dwm.DwmSetWindowAttribute(
        hwnd, attr, ctypes.byref(data), ctypes.sizeof(data)
    )


def _acrylic_composition(hwnd: int, dark: bool) -> bool:
    """SetWindowCompositionAttribute 路径（Win10 1803+ / Win11 通用）。"""
    try:
        fn = getattr(_user32, "SetWindowCompositionAttribute", None)
        if fn is None:
            return False
        # 0xAABBGGRR：深色约 (32,32,32,216)，浅色约 (243,243,24,232)
        if dark:
            color = 0xD8202020
        else:
            color = 0xE8F3F3F3
        accent = _ACCENTPOLICY(ACCENT_ENABLE_ACRYLICBLURBEHIND, 2, color, 0)
        cad = _WINCOMPATTRDATA(
            WCA_ACCENT_POLICY,
            ctypes.cast(ctypes.byref(accent), ctypes.c_void_p),
            ctypes.sizeof(accent),
        )
        fn.argtypes = [wintypes.HWND, ctypes.c_void_p]
        return bool(fn(hwnd, ctypes.byref(cad)))
    except Exception:
        return False


def apply_acrylic(widget, dark: bool = True) -> str:
    """对一个已 show 的顶级窗口应用亚克力特效。返回实际生效的方式名。"""
    if not IS_WINDOWS:
        return "offscreen"
    try:
        hwnd = int(widget.winId())
    except Exception:
        return "offscreen"

    # 深色 / 浅色
    try:
        _set_dwm(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, 1 if dark else 0)
    except Exception:
        pass
    # Win11 原生圆角
    try:
        _set_dwm(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND)
        _set_dwm(hwnd, DWMWA_BORDER_COLOR, DWMWA_COLOR_NONE)
    except Exception:
        pass
    # Win11 原生 Acrylic backdrop（部分版本与无框架窗口组合不稳定，失败则走老路径）
    try:
        _set_dwm(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_TRANSIENTWINDOW)
    except Exception:
        pass
    # 老路径兜底（也是最可靠的亚克力路径）
    if _acrylic_composition(hwnd, dark):
        return "acrylic-legacy"
    return "dwm"


def force_topmost(widget) -> None:
    """重新把窗口提到最顶层（在 Win11 控制中心弹出后调用，可覆盖在其上方）。"""
    if not IS_WINDOWS:
        return
    try:
        hwnd = int(widget.winId())
        HWND_TOPMOST = -1
        SWP_NOMOVE = 0x0002
        SWP_NOSIZE = 0x0001
        SWP_NOACTIVATE = 0x0010
        _user32.SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                             SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)
    except Exception:
        pass
