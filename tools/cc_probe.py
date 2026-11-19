#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Windows 11 控制中心窗口探测工具（诊断用，纯标准库）。

背景：Windows 没有公开 API 让第三方程序订阅“控制中心（快速设置）已打开 / 已关闭”。
本项目用「枚举顶层窗口 + 类名/标题匹配」来近似检测。但不同 Windows 版本的
控制中心窗口类名/标题并不统一，所以这里提供一个诊断脚本，帮你**看清楚**：
    打开控制中心的那一刻，屏幕上到底多出了哪个窗口（类名 / 标题 / 尺寸 / 进程）。

用法（在本工程根目录、用 Windows 的 Python 直接跑，无需装任何依赖）：
    python tools\\cc_probe.py            # 实时模式：每 300ms 枚举一次，标注新增/消失
    python tools\\cc_probe.py --once     # 快照模式：打印当前所有可见顶层窗口后退出
    python tools\\cc_probe.py --all      # 实时模式下也显示被过滤掉的窗口
    python tools\\cc_probe.py --interval 0.15

操作建议：
    1) 先跑 `python tools\\cc_probe.py`
    2) 让它稳定打印几秒（此时控制中心是关闭的）
    3) 然后按 Win+A 打开控制中心，观察输出里「首次出现（↑NEW）」的那一行：
       哪个 hwnd 冒出来了、它的 class_name 和 title 是什么、属于哪个进程。
    4) 把 ↑NEW 那几行复制回来，就能确定你的系统上控制中心窗口的特征。

仅支持 Windows；在其它平台上会直接提示并退出。
"""

from __future__ import annotations

import argparse
import ctypes
import os
import sys
import time
from ctypes import wintypes

IS_WINDOWS = sys.platform == "win32"

# 尺寸过滤：控制中心面板通常远大于这个下限（过滤掉 0x0 影子窗口 / 微小工具窗）
MIN_W, MIN_H = 200, 200

# 与 ui/ccwatch.py 白名单保持一致的类名提示（仅用于打标，不影响枚举）
CC_CLASS_HINTS = (
    "controlcenterwindow",
    "windows.ui.core.corewindow",
    "xamlexplorerhostislandwindow",
)
CC_TITLE_HINTS = ("quick settings", "control center", "快速设置", "控制中心")

# 已知的“噪音”窗口类名（桌面、托盘、输入法等），--all 之外默认折叠成一行提示
NOISE_CLASSES = (
    "progman", "shell_traywnd", "shell_secondarytraywnd",
    "windowsterminal", "consolewindowclass", "ime", "msctfime uiwindow",
    "tasklist_thumnailwnd", "tooltips_class32",
)


if IS_WINDOWS:
    _user32 = ctypes.windll.user32
    _kernel32 = ctypes.windll.kernel32
    _psapi = ctypes.windll.psapi

    WNDENUMPROC = ctypes.WINFUNCTYPE(
        wintypes.BOOL, wintypes.HWND, wintypes.LPARAM
    )

    class RECT(ctypes.Structure):
        _fields_ = [
            ("left", ctypes.c_long), ("top", ctypes.c_long),
            ("right", ctypes.c_long), ("bottom", ctypes.c_long),
        ]

    PROCESS_QUERY_LIMITED_INFORMATION = 0x1000

    def _class_name(hwnd) -> str:
        buf = ctypes.create_unicode_buffer(256)
        _user32.GetClassNameW(hwnd, buf, 256)
        return buf.value

    def _window_title(hwnd) -> str:
        n = int(_user32.GetWindowTextLengthW(hwnd))
        buf = ctypes.create_unicode_buffer(max(8, n + 2))
        _user32.GetWindowTextW(hwnd, buf, len(buf))
        return buf.value

    def _rect(hwnd):
        r = RECT()
        if not _user32.GetWindowRect(hwnd, ctypes.byref(r)):
            return None
        return (int(r.left), int(r.top), int(r.right), int(r.bottom))

    def _pid_of(hwnd) -> int:
        pid = wintypes.DWORD(0)
        _user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        return int(pid.value)

    def _process_name(pid: int) -> str:
        """用 QueryFullProcessImageNameW 拿进程可执行文件名（普通权限即可）。"""
        if not pid:
            return ""
        h = _kernel32.OpenProcess(
            PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
        if not h:
            return f"pid:{pid}"
        try:
            size = wintypes.DWORD(1024)
            buf = ctypes.create_unicode_buffer(size.value)
            ok = _kernel32.QueryFullProcessImageNameW(
                h, 0, buf, ctypes.byref(size))
            if not ok:
                return f"pid:{pid}"
            return os.path.basename(buf.value)
        finally:
            _kernel32.CloseHandle(h)

    def enum_visible_toplevel() -> list[dict]:
        """枚举所有可见、非最小化的顶层窗口，返回信息字典列表。"""
        out: list[dict] = []

        def _cb(hwnd, _lparam):
            if not _user32.IsWindowVisible(hwnd):
                return True
            if _user32.IsIconic(hwnd):        # 最小化的窗口不算“冒出来”
                return True
            r = _rect(hwnd)
            if r is None:
                return True
            w, h = r[2] - r[0], r[3] - r[1]
            if w <= 0 or h <= 0:
                return True
            pid = _pid_of(hwnd)
            out.append({
                "hwnd": int(hwnd),
                "class_name": _class_name(hwnd),
                "title": _window_title(hwnd),
                "rect": r,
                "w": w, "h": h,
                "pid": pid,
                "proc": _process_name(pid),
            })
            return True

        try:
            _user32.EnumWindows(WNDENUMPROC(_cb), 0)
        except Exception as exc:                 # pragma: no cover
            print(f"[warn] EnumWindows 失败：{exc}", file=sys.stderr)
        return out
else:                                            # 非 Windows：占位
    def enum_visible_toplevel() -> list[dict]:
        return []


def _cc_score(win: dict) -> str:
    """给窗口打个标记：疑似控制中心 / 普通 / 噪音。"""
    cls = win["class_name"].lower()
    title = win["title"].lower()
    if cls in CC_CLASS_HINTS:
        return "CC?"
    if any(h in title for h in CC_TITLE_HINTS):
        return "CC?"
    if cls in NOISE_CLASSES:
        return "noise"
    return ""


def _fmt_win(win: dict, mark: str = "") -> str:
    tag = f"[{mark}] " if mark else ""
    title = win["title"] or "(无标题)"
    return (f'{tag}hwnd=0x{win["hwnd"]:08X}  class="{win["class_name"]}"  '
            f'title="{title}"  {win["w"]}x{win["h"]}  '
            f'proc="{win["proc"]}(pid {win["pid"]})"')


def snapshot(show_all: bool = False) -> None:
    wins = enum_visible_toplevel()
    wins.sort(key=lambda w: (w["class_name"].lower(), w["hwnd"]))
    print(f"=== 可见顶层窗口快照：共 {len(wins)} 个（{time.strftime('%H:%M:%S')}）===")
    for win in wins:
        mark = _cc_score(win)
        if mark == "noise" and not show_all:
            continue
        print(_fmt_win(win, mark))
    print("=== 快照结束 ===")


def watch(interval: float, show_all: bool = False) -> None:
    print("实时模式：每 %.2fs 枚举一次。现在去按 Win+A 打开控制中心，观察 ↑NEW 行。" %
          interval)
    print("（Ctrl+C 退出）\n")
    seen: dict[int, dict] = {}
    tick = 0
    try:
        while True:
            wins = {w["hwnd"]: w for w in enum_visible_toplevel()}
            appeared = [h for h in wins if h not in seen]
            disappeared = [h for h in seen if h not in wins]
            if appeared or disappeared:
                stamp = time.strftime("%H:%M:%S")
                for h in disappeared:
                    old = seen[h]
                    if _cc_score(old) == "noise" and not show_all:
                        continue
                    print(f"{stamp}  ↓GONE {_fmt_win(old, 'GONE')}")
                for h in appeared:
                    win = wins[h]
                    mark = _cc_score(win)
                    if mark == "noise" and not show_all:
                        continue
                    cc = mark or "new"
                    print(f"{stamp}  ↑NEW  {_fmt_win(win, cc)}")
            seen = wins
            tick += 1
            # 心跳：即使什么都没变，也周期性打印一行，方便确认脚本还活着
            if tick % 10 == 0:
                cc_like = [w for w in wins.values() if _cc_score(w) == "CC?"]
                print(f"{time.strftime('%H:%M:%S')}  ·心跳· 可见顶层窗口 {len(wins)} 个，"
                      f"疑似控制中心 {len(cc_like)} 个")
            time.sleep(max(0.05, interval))
    except KeyboardInterrupt:
        print("\n已退出。")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(
        description="Windows 11 控制中心窗口探测器（诊断用，纯标准库）")
    ap.add_argument("--once", action="store_true", help="快照模式：打印一次后退出")
    ap.add_argument("--all", action="store_true", help="显示被过滤掉的噪音窗口")
    ap.add_argument("--interval", type=float, default=0.3,
                    help="实时模式的轮询间隔秒（默认 0.3）")
    args = ap.parse_args(argv)

    if not IS_WINDOWS:
        print("本工具仅支持 Windows（需要 user32/kernel32 的窗口 API）。")
        return 2

    if args.once:
        snapshot(show_all=args.all)
    else:
        watch(interval=args.interval, show_all=args.all)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
