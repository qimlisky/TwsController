"""检测 Windows 11 控制中心（快速设置面板）的弹出与关闭。

Windows 没有公开 API 让第三方程序订阅「控制中心已打开 / 已关闭」，
这里用轻量轮询：枚举顶层窗口，先按窗口类名 / 标题白名单识别控制中心，
白名单之外再用「宽松兜底」规则命中（新出现的可见顶层窗口 + 尺寸合理 +
进程属于 explorer.exe / ShellExperienceHost.exe）。

为了让用户那边「什么都没发生」时也能定位，本模块会把**每次命中的窗口信息**、
**兜底候选**、以及**未命中时的心跳**（含当时可见窗口数）写入日志：
    %LOCALAPPDATA%\\VivoPodsCC\\ccwatch.log
日志开关是设置项 `ccwatch_log`（默认开），由 app.py 注入。

兼容两类实现：
- Win11 22H2 及更早：ShellExperienceHost.exe 的 XAML 岛窗口，
  类名 Windows.UI.Core.CoreWindow / XamlExplorerHostIslandWindow，
  标题为「Quick Settings / 快速设置」；
- Win11 24H2 及以后：「控制中心」为 explorer.exe 的 ControlCenterWindow。

非 Windows 平台为空实现（start() 不做事），便于跨平台开发。
"""

from __future__ import annotations

import os
import sys
import time

from PySide6.QtCore import QObject, QTimer, Signal

IS_WINDOWS = sys.platform == "win32"

# 控制中心可能出现的窗口类名（统一转小写后比较）——白名单
CC_CLASS_HINTS = (
    "controlcenterwindow",
    "windows.ui.core.corewindow",
    "xamlexplorerhostislandwindow",
)

# 标题关键字（中英）——用来把「音量 / 网络」等长得很像的浮岛窗排除掉
CC_TITLE_HINTS = ("quick settings", "control center", "快速设置", "控制中心")

# 面板最小尺寸（像素），用来过滤掉 0x0 的影子窗口
MIN_W, MIN_H = 200, 200
# 兜底规则里额外要求的上限（控制中心不会有整屏那么大；防误命中全屏窗口）
MAX_W_RATIO, MAX_H_RATIO = 0.98, 0.98

# 宽松兜底允许的宿主进程（控制中心由系统组件绘制）
FALLBACK_PROCS = ("explorer.exe", "shellexperiencehost.exe")

# 日志文件名（固定放在 %LOCALAPPDATA%\\VivoPodsCC 下）
LOG_DIR_NAME = "VivoPodsCC"
LOG_FILE_NAME = "ccwatch.log"
HEARTBEAT_TICKS = 40          # 每 N 次轮询记一条心跳（150ms * 40 ≈ 6s）


def log_path() -> str:
    """控制中心检测日志的绝对路径。"""
    base = os.environ.get("LOCALAPPDATA") or os.path.expanduser("~")
    return os.path.join(base, LOG_DIR_NAME, LOG_FILE_NAME)


if IS_WINDOWS:
    import ctypes
    from ctypes import wintypes

    _user32 = ctypes.windll.user32
    _kernel32 = ctypes.windll.kernel32

    WNDENUMPROC = ctypes.WINFUNCTYPE(
        wintypes.BOOL, wintypes.HWND, wintypes.LPARAM
    )

    class _RECT(ctypes.Structure):
        _fields_ = [
            ("left", ctypes.c_long),
            ("top", ctypes.c_long),
            ("right", ctypes.c_long),
            ("bottom", ctypes.c_long),
        ]

    _PROCESS_QUERY_LIMITED_INFORMATION = 0x1000

    def _class_name(hwnd) -> str:
        buf = ctypes.create_unicode_buffer(256)
        _user32.GetClassNameW(hwnd, buf, 256)
        return buf.value

    def _window_title(hwnd) -> str:
        n = int(_user32.GetWindowTextLengthW(hwnd))
        buf = ctypes.create_unicode_buffer(max(8, n + 2))
        _user32.GetWindowTextW(hwnd, buf, len(buf))
        return buf.value

    def _title_matches(title: str) -> bool:
        low = title.lower()
        return any(h in low for h in CC_TITLE_HINTS)

    def _pid_of(hwnd) -> int:
        pid = wintypes.DWORD(0)
        _user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        return int(pid.value)

    def _process_name(pid: int) -> str:
        if not pid:
            return ""
        h = _kernel32.OpenProcess(
            _PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
        if not h:
            return ""
        try:
            size = wintypes.DWORD(1024)
            buf = ctypes.create_unicode_buffer(size.value)
            if not _kernel32.QueryFullProcessImageNameW(
                    h, 0, buf, ctypes.byref(size)):
                return ""
            return os.path.basename(buf.value)
        finally:
            _kernel32.CloseHandle(h)

    def _screen_bounds():
        """返回主屏尺寸 (w, h)，供兜底规则过滤全屏窗口。"""
        try:
            w = int(_user32.GetSystemMetrics(0))   # SM_CXSCREEN
            h = int(_user32.GetSystemMetrics(1))   # SM_CYSCREEN
            return w, h
        except Exception:
            return 0, 0

    def _enum_candidates():
        """枚举可见顶层窗口，返回 (rect, class, title, proc, w, h) 列表。"""
        out: list = []
        sw, sh = _screen_bounds()

        def _cb(hwnd, _lparam):
            if not _user32.IsWindowVisible(hwnd):
                return True
            rect = _RECT()
            if not _user32.GetWindowRect(hwnd, ctypes.byref(rect)):
                return True
            w = int(rect.right) - int(rect.left)
            h = int(rect.bottom) - int(rect.top)
            if w < MIN_W or h < MIN_H:
                return True
            cls = _class_name(hwnd)
            pid = _pid_of(hwnd)
            out.append({
                "hwnd": int(hwnd),
                "class": cls,
                "title": _window_title(hwnd),
                "proc": _process_name(pid).lower(),
                "rect": (int(rect.left), int(rect.top),
                         int(rect.right), int(rect.bottom)),
                "w": w,
                "h": h,
                "screen": (sw, sh),
            })
            return True

        try:
            _user32.EnumWindows(WNDENUMPROC(_cb), 0)
        except Exception:
            return []
        return out

    def _is_whitelisted(win: dict) -> bool:
        cls = win["class"].lower()
        if cls in CC_CLASS_HINTS:
            return True
        return _title_matches(win["title"])

    def _is_fallback(win: dict, new_hwnds: set) -> bool:
        """宽松兜底：新出现的可见顶层窗口 + 尺寸合理 + 进程属系统组件。

        new_hwnds 是“本次轮询相比上次新出现”的 hwnd 集合——这是区分控制中心
        与「一直开着的资源管理器窗口」的关键。
        """
        if win["hwnd"] not in new_hwnds:
            return False
        if win["proc"] not in FALLBACK_PROCS:
            return False
        sw, sh = win["screen"]
        if sw and sh:
            if win["w"] >= int(sw * MAX_W_RATIO) and \
                    win["h"] >= int(sh * MAX_H_RATIO):
                return False        # 整屏窗口，不像控制中心
        return True

    def find_control_center(new_hwnds: set | None = None):
        """返回控制中心面板的 (left, top, right, bottom) 与命中详情；
        未打开则 (None, None)。

        先白名单，再宽松兜底。返回的详情用于写日志。
        """
        new_hwnds = new_hwnds or set()
        wins = _enum_candidates()
        # 1) 白名单（类名 / 标题）
        for win in wins:
            if _is_whitelisted(win):
                return win["rect"], {"how": "whitelist", **win}
        # 2) 宽松兜底
        for win in wins:
            if _is_fallback(win, new_hwnds):
                return win["rect"], {"how": "fallback", **win}
        return None, None
else:
    def find_control_center(new_hwnds: set | None = None):
        return None, None


class ControlCenterWatcher(QObject):
    """轮询检测控制中心开关，状态变化时抛 opened(rect) / closed()。"""

    opened = Signal(object)   # (left, top, right, bottom)
    closed = Signal()

    INTERVAL_MS = 150
    CLOSE_DEBOUNCE = 2        # 连续 2 次检测不到才判定关闭，避开收起动画抖动

    def __init__(self, parent=None) -> None:
        super().__init__(parent)
        self._timer = QTimer(self)
        self._timer.setInterval(self.INTERVAL_MS)
        self._timer.timeout.connect(self._tick)
        self._active = False
        self._misses = 0
        self.last_rect = None
        # 日志（由 app.py 注入开关；默认开）
        self.log_enabled = True
        self._logfn = self._default_log
        self._seen_hwnds: set = set()
        self._ticks = 0
        self._last_heartbeat = 0.0

    # ------------------------------------------------------------------ #
    # 日志
    # ------------------------------------------------------------------ #
    def set_logger(self, fn) -> None:
        """允许外部注入 logger（例如同时写 stderr）。"""
        if callable(fn):
            self._logfn = fn

    def _default_log(self, msg: str) -> None:
        if not self.log_enabled:
            return
        line = f"[{time.strftime('%Y-%m-%d %H:%M:%S')}] {msg}\n"
        try:
            path = log_path()
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "a", encoding="utf-8") as f:
                f.write(line)
        except Exception:
            pass

    def _log(self, msg: str) -> None:
        try:
            self._logfn(msg)
        except Exception:
            pass

    # ------------------------------------------------------------------ #
    def start(self) -> None:
        """开始检测（非 Windows 或已在运行时为空操作）。"""
        if not IS_WINDOWS or self._timer.isActive():
            return
        self._misses = 0
        self._seen_hwnds = set()
        self._ticks = 0
        self._log(f"检测启动：interval={self.INTERVAL_MS}ms，"
                  f"whitelist={CC_CLASS_HINTS}，fallback_procs={FALLBACK_PROCS}")
        self._timer.start()
        self._tick()

    def stop(self) -> None:
        if self._timer.isActive():
            self._log("检测停止。")
        self._timer.stop()
        self._active = False
        self._misses = 0
        self.last_rect = None
        self._seen_hwnds = set()

    @property
    def active(self) -> bool:
        return self._active

    def note_hotkey(self, ok: bool, detail: str = "") -> None:
        """记录 Win+A 热键兜底路径的工作状态（任务 4c）。"""
        state = "可用" if ok else "不可用"
        self._log(f"Win+A 热键兜底路径：{state}。{detail}")

    # ------------------------------------------------------------------ #
    def _tick(self) -> None:
        self._ticks += 1
        # 记录本次新出现的窗口（供兜底规则使用）
        if IS_WINDOWS:
            try:
                cur = {w["hwnd"] for w in _enum_candidates()}
            except Exception:
                cur = set()
        else:
            cur = set()
        new_hwnds = cur - self._seen_hwnds
        self._seen_hwnds = cur

        rect, detail = find_control_center(new_hwnds)
        if rect is not None:
            self._misses = 0
            if not self._active:
                self._active = True
                self.last_rect = rect
                self._log("命中控制中心："
                          f"how={detail.get('how')} "
                          f"class={detail.get('class')!r} "
                          f"title={detail.get('title')!r} "
                          f"size={detail.get('w')}x{detail.get('h')} "
                          f"proc={detail.get('proc')!r} rect={rect}")
                self.opened.emit(rect)
            return
        if not self._active:
            # 未命中也要留痕：周期性心跳（含当时可见窗口数），
            # 便于区分「没启动」和「启动了但没匹配到」。
            now = time.monotonic()
            if self._ticks == 1 or (now - self._last_heartbeat) >= \
                    HEARTBEAT_TICKS * (self.INTERVAL_MS / 1000.0):
                self._last_heartbeat = now
                self._log(f"心跳：未命中控制中心。当前可见顶层窗口 "
                          f"{len(cur)} 个，本轮新增 {len(new_hwnds)} 个。")
            return
        self._misses += 1
        if self._misses >= self.CLOSE_DEBOUNCE:
            self._active = False
            self.last_rect = None
            self._misses = 0
            self._log("控制中心关闭。")
            self.closed.emit()
