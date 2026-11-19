"""
Windows 11 控制中心风格的耳机快捷浮窗：

- 无边框、圆角、亚克力背景、置顶、不占任务栏；
- 锚定在屏幕右下角（Win+A 控制中心的位置），并可在系统控制中心弹出后
  重新置顶，视觉上叠加在控制中心上方；
- 淡入 / 上滑动画，失焦自动收起（可 pin 固定）。
"""

from __future__ import annotations

import ctypes
import sys
from datetime import datetime
from typing import Optional

from PySide6.QtCore import (
    QEasingCurve, QEvent, QPoint, QPropertyAnimation, QRect, QSize, Qt,
    QTimer,
)
from PySide6.QtGui import (
    QAction, QActionGroup, QColor, QFont, QGuiApplication, QPainter,
)
from PySide6.QtWidgets import (
    QApplication, QButtonGroup, QFrame, QGraphicsOpacityEffect, QHBoxLayout,
    QLabel, QMenu, QPushButton, QScrollArea, QSizePolicy, QVBoxLayout, QWidget,
)

from .acrylic import apply_acrylic, force_topmost
from .theme import DARK, LIGHT, FluentPalette, qss
from .widgets import (
    BatteryPill, ControlRow, FlowLayout, Glyph, IconButton, PodsPhoto,
    Segmented, ToggleSwitch, section_label,
)
from vivo import vivo_protocol as vp
from vivo.controller import DeviceInfo, EarbudController, UNKNOWN

IS_WINDOWS = sys.platform == "win32"
PANEL_WIDTH = 372
PANEL_HEIGHT = 540            # 展开态面板高度
COLLAPSED_MIN_HEIGHT = 180    # 折叠态最小高度
WIDGET_MAX_H = 16777215       # Qt 的 QWIDGETSIZE_MAX


class FlyoutWindow(QWidget):
    def __init__(self, controller: EarbudController, settings, parent=None) -> None:
        super().__init__(parent)
        self.ctrl = controller
        self.settings = settings
        self.dark = self.settings.value("dark", True, type=bool)
        self.pal: FluentPalette = DARK if self.dark else LIGHT
        self.pinned = False
        # 跟随 Win+A 叠加时不随失焦收起（系统控制中心会成为前台）
        self._overlay_mode = False
        # 跟随系统控制中心：控制中心关闭时同步收起（用户点过浮窗后不再跟随）
        self._cc_follow = False
        # UI 两态：折叠态只显示电量 + 紧凑降噪快捷切换（多设备列表在展开态）
        self.collapsed = bool(self.settings.value("collapsed", False, type=bool))
        self._multi_rows: list[QWidget] = []
        self.quit_callback = None

        # 充电盒电量「记忆 + 上次更新时间」（任务 C / v3.1 简化语义）：
        #   - 不再依赖 WEAR 状态机（TWS 5 无 WEAR 能力、0x820D 只在变化时上报，
        #     时机极易错过，实测导致“不记忆”）；
        #   - 只要收到有效仓电量读数就刷新快照并持久化到 QSettings；
        #   - 耳机出仓后读不到（-1）就一直显示记住的值，绝不清空；
        #   - 从未收到过任何有效读数则明确显示“未获取”，绝不编造。
        self._case_last_valid = self._load_int_setting(
            "case_battery_snapshot", UNKNOWN)   # 最近一次有效读数
        self._case_snapshot = self._case_last_valid   # 对外显示的“上次”值
        self._case_charging = False
        self._case_updated_at = self.settings.value(
            "case_battery_updated_at", "", type=str) or ""
        # 任务 C：按「入仓事件」刷新时间戳的状态机
        #   _case_valid_streak：连续有效读数次数（0=出仓/未上报状态）
        #   _case_gap        ：连续无有效读数次数（>=2 才判出仓，去抖）
        self._case_valid_streak = 0
        self._case_gap = 0

        self.setWindowTitle("vivo 耳机控制台")
        self.setWindowFlags(
            Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.Tool
        )
        self.setAttribute(Qt.WA_TranslucentBackground)
        self.resize(PANEL_WIDTH, PANEL_HEIGHT)

        self._opacity = QGraphicsOpacityEffect(self)
        self._opacity.setOpacity(0.0)
        self._anim = QPropertyAnimation(self._opacity, b"opacity", self)
        self._anim.setDuration(160)
        self._anim.setEasingCurve(QEasingCurve.OutCubic)

        self._build_ui()
        self.apply_theme()

        # 失焦自动隐藏
        self._defer_timer = QTimer(self)
        self._defer_timer.setSingleShot(True)
        self._defer_timer.setInterval(220)
        self._defer_timer.timeout.connect(self._maybe_hide)

        # 查找耳机响铃自动停止
        self._find_timer = QTimer(self)
        self._find_timer.setSingleShot(True)
        self._find_timer.setInterval(20_000)
        self._find_timer.timeout.connect(lambda: self._stop_find())

        # 状态
        self._connected = False
        self._set_connected_ui(False)
        self._refresh_case_display()   # 重启后仍显示上次记忆值 + 更新时间

        self._wire_controller()

    # ------------------------------------------------------------------ #
    # UI 构建
    # ------------------------------------------------------------------ #
    def _build_ui(self) -> None:
        outer = QVBoxLayout(self)
        outer.setContentsMargins(8, 8, 8, 8)
        self.root = QFrame()
        self.root.setObjectName("root")
        outer.addWidget(self.root)

        root_lay = QVBoxLayout(self.root)
        root_lay.setContentsMargins(12, 10, 12, 12)
        root_lay.setSpacing(8)

        # ---- 标题栏 ----
        title = QHBoxLayout()
        title.setSpacing(8)
        self.head_glyph = PodsPhoto(self.pal, 20)
        title.addWidget(self.head_glyph)
        title_col = QVBoxLayout()
        title_col.setSpacing(0)
        self.name_lab = QLabel("vivo 耳机")
        self.name_lab.setObjectName("title")
        title_col.addWidget(self.name_lab)
        # 佩戴状态：移到标题下方（任务 B），折叠 / 展开态都可见
        self.wear_lab = QLabel("佩戴状态未知")
        self.wear_lab.setObjectName("footnote")
        title_col.addWidget(self.wear_lab)
        self.fw_lab = QLabel("未连接")
        self.fw_lab.setObjectName("subtitle")
        title_col.addWidget(self.fw_lab)
        title.addLayout(title_col, 1)

        self.expand_btn = IconButton("chevron_up", self.pal, "收起面板")
        self.expand_btn.clicked.connect(self.toggle_collapsed)
        self.pin_btn = IconButton("pin", self.pal, "固定浮窗（不随失焦收起）")
        self.pin_btn.setCheckable(True)
        self.pin_btn.clicked.connect(self._toggle_pin)
        self.menu_btn = IconButton("settings", self.pal, "设置")
        self.menu_btn.clicked.connect(self._show_menu)
        self.close_btn = IconButton("close", self.pal, "收起")
        self.close_btn.clicked.connect(self._on_close_clicked)
        title.addWidget(self.expand_btn)
        title.addWidget(self.pin_btn)
        title.addWidget(self.menu_btn)
        title.addWidget(self.close_btn)
        root_lay.addLayout(title)

        # ---- 未连接卡片 ----
        self.disconnect_card = QFrame()
        self.disconnect_card.setObjectName("card")
        dl = QVBoxLayout(self.disconnect_card)
        dl.setContentsMargins(14, 18, 14, 16)
        dl.setSpacing(10)
        dl.setAlignment(Qt.AlignCenter)
        big = PodsPhoto(self.pal, 44)
        big.setFixedSize(48, 48)
        dl.addWidget(big, alignment=Qt.AlignCenter)
        tip = QLabel("未连接到耳机")
        tip.setAlignment(Qt.AlignCenter)
        f = QFont(); f.setPointSize(11); f.setWeight(QFont.DemiBold)
        tip.setFont(f)
        dl.addWidget(tip)
        self.connect_btn = QPushButton("选择 vivo / iQOO 耳机")
        self.connect_btn.setObjectName("accent")
        self.connect_btn.setCursor(Qt.PointingHandCursor)
        self.connect_btn.clicked.connect(self._choose_device)
        dl.addWidget(self.connect_btn)
        self.demo_btn = QPushButton("进入演示模式（无需耳机）")
        self.demo_btn.setObjectName("ghost")
        self.demo_btn.setCursor(Qt.PointingHandCursor)
        self.demo_btn.clicked.connect(self.ctrl.connect_demo)
        dl.addWidget(self.demo_btn)
        self.error_lab = QLabel("")
        self.error_lab.setObjectName("subtitle")
        self.error_lab.setWordWrap(True)
        self.error_lab.setAlignment(Qt.AlignCenter)
        dl.addWidget(self.error_lab)
        root_lay.addWidget(self.disconnect_card)

        # ---- 连接后内容（可滚动） ----
        self.scroll = QScrollArea()
        self.scroll.setWidgetResizable(True)
        self.scroll.setFrameShape(QFrame.NoFrame)
        self.scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        self.scroll.setVerticalScrollBarPolicy(Qt.ScrollBarAsNeeded)
        body = QWidget()
        self.body_lay = QVBoxLayout(body)
        self.body_lay.setContentsMargins(0, 0, 0, 0)
        self.body_lay.setSpacing(8)

        # 电量
        self.batt_label = section_label("电量")
        self.body_lay.addWidget(self.batt_label)
        batt = QHBoxLayout()
        batt.setSpacing(8)
        self.bt_l = BatteryPill(self.pal, "左耳")
        self.bt_r = BatteryPill(self.pal, "右耳")
        self.bt_c = BatteryPill(self.pal, "充电盒")
        for b in (self.bt_l, self.bt_r, self.bt_c):
            batt.addWidget(b)
        self.body_lay.addLayout(batt)

        # ---- 折叠态专用：紧凑版三档降噪快捷切换（任务 5）----
        self.collapsed_noise_seg = Segmented(self.pal, [
            (int(vp.NoiseMode.ANC), "anc", "降噪"),
            (int(vp.NoiseMode.OFF), "off", "关闭"),
            (int(vp.NoiseMode.TRANSPARENCY), "trans", "通透"),
        ], compact=True)
        self.collapsed_noise_seg.changed.connect(self.ctrl.set_noise)
        self.collapsed_noise_seg.setVisible(False)
        self.body_lay.addWidget(self.collapsed_noise_seg)

        # 多设备连接（双连主机）：仅在展开态显示（任务 5 把它从折叠态移除）
        # 任务 D：标题右侧加区级「实验」角标；行内按钮不再带「·实验」
        self.multi_label = QWidget()
        mh = QHBoxLayout(self.multi_label)
        mh.setContentsMargins(0, 0, 0, 0)
        mh.setSpacing(6)
        mh.addWidget(section_label("多设备连接"))
        self.multi_badge = QLabel("实验")
        self.multi_badge.setObjectName("badge")
        mh.addWidget(self.multi_badge)
        mh.addStretch(1)
        self.body_lay.addWidget(self.multi_label)
        self.multi_box = QWidget()
        self.multi_lay = QVBoxLayout(self.multi_box)
        self.multi_lay.setContentsMargins(0, 0, 0, 0)
        self.multi_lay.setSpacing(4)
        self.multi_empty = QLabel("等待耳机上报…")
        self.multi_empty.setObjectName("subtitle")
        self.multi_lay.addWidget(self.multi_empty)
        self.body_lay.addWidget(self.multi_box)

        # 出声模式
        self.noise_label = section_label("出声模式")
        self.body_lay.addWidget(self.noise_label)
        self.noise_seg = Segmented(self.pal, [
            (int(vp.NoiseMode.ANC), "anc", "降噪"),
            (int(vp.NoiseMode.OFF), "off", "关闭"),
            (int(vp.NoiseMode.TRANSPARENCY), "trans", "通透"),
        ])
        self.noise_seg.changed.connect(self.ctrl.set_noise)
        self.body_lay.addWidget(self.noise_seg)

        # 空间音频
        self.spatial_row = ControlRow(
            self.pal, "spatial", "空间音频", "3D 全景声（头部固定）", badge="实验"
        )
        self.spatial_switch = ToggleSwitch(self.pal)
        self.spatial_switch.toggled.connect(self._on_spatial)
        self.spatial_row.add_right(self.spatial_switch)
        self.body_lay.addWidget(self.spatial_row)

        # 低延迟游戏
        self.game_row = ControlRow(
            self.pal, "game", "低延迟游戏", "缩短音频延迟", badge="实验"
        )
        self.game_switch = ToggleSwitch(self.pal)
        self.game_switch.toggled.connect(self.ctrl.set_game)
        self.game_row.add_right(self.game_switch)
        self.body_lay.addWidget(self.game_row)

        # EQ（FlowLayout 自动换行）
        self.eq_label = section_label("音效（EQ）")
        self.body_lay.addWidget(self.eq_label)
        eq_box = QWidget()
        self.eq_box = eq_box
        eq_flow = FlowLayout(eq_box, margin=0, hspace=6, vspace=6)
        self.eq_group = QButtonGroup(self)
        self.eq_group.setExclusive(True)
        self.eq_buttons: list[QPushButton] = []
        for code, name in vp.AUDIO_EFFECT_CODES.items():
            chip = QPushButton(name)
            chip.setObjectName("chip")
            chip.setCheckable(True)
            chip.setCursor(Qt.PointingHandCursor)
            chip.clicked.connect(lambda _=False, c=code: self.ctrl.set_eq(c))
            self.eq_group.addButton(chip, code)
            self.eq_buttons.append(chip)
            eq_flow.addWidget(chip)
        self.body_lay.addWidget(eq_box)

        # 查找耳机
        self.find_btn = QPushButton("查找耳机（响铃）")
        self.find_btn.setObjectName("ghost")
        self.find_btn.setCheckable(True)
        self.find_btn.setCursor(Qt.PointingHandCursor)
        self.find_btn.toggled.connect(self._on_find)
        self.body_lay.addWidget(self.find_btn)

        self.body_lay.addStretch(1)
        self.scroll.setWidget(body)
        root_lay.addWidget(self.scroll, 1)
        self.scroll.hide()

        # ---- 底部信息行（固件版本；佩戴状态已移到标题下方，任务 B） ----
        self.footer = QWidget()
        fl = QHBoxLayout(self.footer)
        fl.setContentsMargins(2, 0, 2, 0)
        fl.setSpacing(8)
        self.fw_version_lab = QLabel("")
        self.fw_version_lab.setObjectName("footnote")
        self.fw_version_lab.setAlignment(Qt.AlignRight)
        fl.addStretch(1)
        fl.addWidget(self.fw_version_lab)
        root_lay.addWidget(self.footer)
        self.footer.hide()

    # ------------------------------------------------------------------ #
    # 主题
    # ------------------------------------------------------------------ #
    def apply_theme(self) -> None:
        self.pal = DARK if self.dark else LIGHT
        self.setStyleSheet(qss(self.pal))
        for w in self.findChildren(Glyph):
            w.recolor(self.pal)
        for w in self.findChildren(PodsPhoto):
            w.recolor(self.pal)
        for w in self.findChildren(IconButton):
            w.recolor(self.pal)
        for b in (self.bt_l, self.bt_r, self.bt_c):
            b.recolor(self.pal)
        self.spatial_switch.palette_changed(self.pal)
        self.game_switch.palette_changed(self.pal)
        if IS_WINDOWS and self.isVisible():
            apply_acrylic(self, self.dark)
        self.settings.setValue("dark", self.dark)

    def toggle_theme(self) -> None:
        self.dark = not self.dark
        self.apply_theme()

    # ------------------------------------------------------------------ #
    # 控制器信号
    # ------------------------------------------------------------------ #
    def _wire_controller(self) -> None:
        c = self.ctrl
        c.connected.connect(self._on_connected)
        c.status.connect(self.fw_lab.setText)
        c.battery.connect(self._on_battery)
        c.noise_mode.connect(self._on_noise_mode)
        c.spatial.connect(self._on_spatial_state)
        c.game_mode.connect(self._on_game_state)
        c.eq_mode.connect(self._on_eq_state)
        c.wear.connect(self._on_wear)
        c.firmware.connect(self._on_firmware)
        c.multipoint.connect(self._on_multipoint)
        c.error.connect(self._on_error)

    def _on_connected(self, ok: bool) -> None:
        self._connected = ok
        self._set_connected_ui(ok)
        if ok and self.ctrl.device:
            self.name_lab.setText(self.ctrl.device.name)
            self._apply_feature_visibility()
            self._refresh_case_display()

    def _apply_feature_visibility(self) -> None:
        """按型号能力矩阵 + 当前折叠状态决定各分区显隐。"""
        fs = self.ctrl.features
        expanded = not self.collapsed
        # 折叠态：电量胶囊改紧凑，并隐掉「电量」分区标题以省空间（任务 B）
        for pill in (self.bt_l, self.bt_r, self.bt_c):
            pill.set_compact(not expanded)
        self.batt_label.setVisible(expanded)
        # 折叠态让内容与窗口底边留一点呼吸空间
        self.body_lay.setContentsMargins(0, 0, 0, 0 if expanded else 4)
        # 展开态：原有布局
        self.noise_label.setVisible(expanded)
        self.noise_seg.setVisible(expanded and fs.visible("noise"))
        self.spatial_row.setVisible(expanded and fs.visible("spatial"))
        self.game_row.setVisible(expanded and fs.visible("game"))
        self.eq_label.setVisible(expanded)
        self.eq_box.setVisible(expanded)
        self.find_btn.setVisible(expanded)
        # 多设备列表：移到展开态（任务 5）；折叠态不再显示
        self.multi_label.setVisible(expanded and fs.has("dual"))
        self.multi_box.setVisible(expanded and fs.has("dual"))
        # 折叠态：只保留电量 + 紧凑降噪切换（保留 fs.visible("noise") 门控）
        self.collapsed_noise_seg.setVisible(
            (not expanded) and fs.visible("noise"))
        self.footer.setVisible(expanded)

    def _set_connected_ui(self, ok: bool) -> None:
        self.disconnect_card.setVisible(not ok)
        self.scroll.setVisible(ok)
        self.expand_btn.setVisible(ok)
        if not ok:
            self.name_lab.setText("vivo 耳机")
            self.fw_lab.setText("未连接")
            self._clear_multipoint()
        self._apply_collapsed(self.collapsed)

    # ------------------------------------------------------------------ #
    # 佩戴 / 电量（含充电盒电量记忆，任务 2）
    # ------------------------------------------------------------------ #
    def _on_wear(self, left: str, right: str) -> None:
        """佩戴状态仅用于底部文案。

        v3.1 起，充电盒电量记忆不再依赖 wear 状态机（见 _on_battery），
        因此这里不再触发任何快照逻辑。
        """
        if left == "worn" and right == "worn":
            text = "双耳佩戴中"
        elif left == "worn":
            text = "仅左耳佩戴"
        elif right == "worn":
            text = "仅右耳佩戴"
        elif left == "in_case" and right == "in_case":
            text = "双耳在充电盒中"
        elif left == "in_case" or right == "in_case":
            text = "单耳在充电盒中"
        else:
            text = "未佩戴"
        self.wear_lab.setText(text)

    def _stamp_case_time(self) -> None:
        """把当前仓电量 + 时间戳持久化到 QSettings（键名与读取一致）。"""
        now = datetime.now()
        self._case_updated_at = now.strftime("%Y-%m-%d %H:%M")
        try:
            self.settings.setValue("case_battery_snapshot",
                                   int(self._case_snapshot))
            self.settings.setValue("case_battery_updated_at",
                                   self._case_updated_at)
            self.settings.sync()
        except Exception:
            pass
        self._refresh_case_caption()

    def _fmt_case_time(self, raw: str) -> str:
        """把持久化的时间戳格式化为「12:34」（当天）/「10-24 12:34」（跨天）。"""
        if not raw:
            return ""
        try:
            dt = datetime.strptime(raw, "%Y-%m-%d %H:%M")
        except Exception:
            return ""
        if dt.date() == datetime.now().date():
            return dt.strftime("%H:%M")
        return dt.strftime("%m-%d %H:%M")

    def _refresh_case_caption(self) -> None:
        shown = self._fmt_case_time(self._case_updated_at)
        if shown:
            self.bt_c.set_caption(f"上次更新 {shown}")
        elif self._case_snapshot == UNKNOWN:
            self.bt_c.set_caption("尚未获取到读数")
        else:
            self.bt_c.set_caption("")

    def _load_int_setting(self, key: str, default: int) -> int:
        try:
            return int(self.settings.value(key, default))
        except Exception:
            return default

    def _on_firmware(self, ver: str) -> None:
        self.fw_version_lab.setText(f"固件 {ver}")

    def _on_battery(self, l, lc, r, rc, case, cc) -> None:
        # 左右耳始终实时显示（绝不被仓电量快照影响）
        self.bt_l.set_value(l, lc, l < 0)
        self.bt_r.set_value(r, rc, r < 0)
        # 任务 C：仓电量按「入仓事件」刷新时间戳（不再按值变化/每帧刷新）：
        #   - 上一轮无有效读数 → 本轮有 = 入仓（放回仓）事件：
        #     无论数值是否变化，都刷新「值 + 时间戳」；
        #   - 持续在仓且数值未变：时间戳不动（避免每 30s 轮询把时间戳刷新）；
        #   - 数值变化（如出仓后重新入仓、电量变化）：刷新值 + 时间戳；
        #   - 无有效读数（出仓/未上报）：连续 2 次才判定「出仓」，去抖。
        if case >= 0:
            was_in = self._case_valid_streak > 0
            self._case_valid_streak += 1
            self._case_gap = 0
            value_changed = (case != self._case_snapshot)
            if (not was_in) or value_changed:
                # 入仓事件 或 值确实变化 → 刷新记忆值 + 时间戳
                self._case_last_valid = case
                self._case_snapshot = case
                self._case_charging = bool(cc)
                self._stamp_case_time()      # 持久化（含 sync）
            # 持续在仓且值未变：什么都不做，时间戳保持
        else:
            # 无有效读数：累计 gap，连续 2 次才判定出仓
            self._case_gap += 1
            if self._case_gap >= 2 and self._case_valid_streak > 0:
                self._case_valid_streak = 0   # 出仓；记忆值保留、不再刷时间戳
        self._refresh_case_display()

    def _refresh_case_display(self) -> None:
        """刷新盒电量显示：始终显示最近一次有效读数（记忆值）；
        从未收到过有效读数则明确显示「未获取」，绝不编造数值。"""
        shown = self._case_snapshot
        if shown == UNKNOWN:
            shown = self._case_last_valid
        if shown == UNKNOWN:
            self.bt_c.set_value(UNKNOWN, False, True, unknown_text="未获取")
        else:
            self.bt_c.set_value(int(shown), bool(self._case_charging), False)
        self._refresh_case_caption()

    def _on_noise_mode(self, mode: int) -> None:
        """降噪模式变化：同步展开态分段控件与折叠态紧凑控件的选中态。"""
        self.noise_seg.set_selected(mode)
        self.collapsed_noise_seg.set_selected(mode)

    def _on_spatial_state(self, v: int) -> None:
        self.spatial_switch.sync_state(v == 1)

    def _on_game_state(self, v: int) -> None:
        self.game_switch.sync_state(v == 1)

    def _on_eq_state(self, code: int) -> None:
        btn = self.eq_group.button(code)
        if btn:
            btn.setChecked(True)

    def _on_spatial(self, on: bool) -> None:
        self.ctrl.set_spatial(on)

    def _on_error(self, text: str) -> None:
        self.error_lab.setText(text)
        self.fw_lab.setText(text)

    # ------------------------------------------------------------------ #
    # 查找耳机
    # ------------------------------------------------------------------ #
    def _on_find(self, on: bool) -> None:
        self.ctrl.find(on)
        self.find_btn.setText("停止响铃" if on else "查找耳机（响铃）")
        if on:
            self._find_timer.start()
        else:
            self._find_timer.stop()

    def _stop_find(self) -> None:
        self.find_btn.setChecked(False)

    # ------------------------------------------------------------------ #
    # 折叠 / 展开（两态 UI）
    # ------------------------------------------------------------------ #
    def toggle_collapsed(self) -> None:
        self._apply_collapsed(not self.collapsed)

    def _apply_collapsed(self, collapsed: bool) -> None:
        self.collapsed = bool(collapsed)
        try:
            self.settings.setValue("collapsed", self.collapsed)
        except Exception:
            pass
        self.expand_btn.set_icon_name(
            "chevron_down" if self.collapsed else "chevron_up"
        )
        self.expand_btn.setToolTip("展开面板" if self.collapsed else "收起面板")
        if self._connected:
            self._apply_feature_visibility()
        else:
            for w in (self.noise_label, self.noise_seg, self.spatial_row,
                      self.game_row, self.eq_label, self.eq_box,
                      self.find_btn, self.multi_label, self.multi_box,
                      self.collapsed_noise_seg):
                w.setVisible(False)
            self.footer.setVisible(False)
        self._fit_window()

    def _fit_window(self) -> None:
        """折叠态：窗口高度 = 内容实际高度，且关掉纵向滚动条（一屏放下）；
        展开态：回到固定高度、内容超出时可滚动。"""
        if self._connected and self.collapsed:
            # 折叠态不允许出现任何滚动（任务 B）
            self.scroll.setVerticalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
            self.scroll.setMinimumHeight(0)
            self.scroll.setMaximumHeight(WIDGET_MAX_H)
            inner = self.scroll.widget()
            inner.adjustSize()
            self.scroll.setFixedHeight(inner.sizeHint().height())
            self.layout().activate()
            target = self.sizeHint().height()
        else:
            self.scroll.setVerticalScrollBarPolicy(Qt.ScrollBarAsNeeded)
            self.scroll.setMinimumHeight(0)
            self.scroll.setMaximumHeight(WIDGET_MAX_H)
            self.layout().activate()
            target = PANEL_HEIGHT
        target = max(COLLAPSED_MIN_HEIGHT, min(WIDGET_MAX_H, target))
        self.resize(PANEL_WIDTH, target)
        if self.isVisible():
            self.setGeometry(self._anchor_geometry())

    def showEvent(self, event) -> None:
        """窗口显示后布局才最终确定，再校正一次折叠态高度（避免预显示时
        sizeHint 尚未收敛导致底部留白）。"""
        super().showEvent(event)
        QTimer.singleShot(0, self._fit_window)

    # ------------------------------------------------------------------ #
    # 多设备连接（双连主机）
    # ------------------------------------------------------------------ #
    def _clear_multipoint(self) -> None:
        for row in self._multi_rows:
            self.multi_lay.removeWidget(row)
            row.deleteLater()
        self._multi_rows.clear()
        self.multi_empty.setVisible(True)

    def _on_multipoint(self, devices: list) -> None:
        self._clear_multipoint()
        if not devices:
            self.multi_empty.setText("耳机未上报多设备信息")
            self.multi_empty.setVisible(True)
            self._fit_window()
            return
        self.multi_empty.setVisible(False)
        for info in devices:
            row = self._make_device_row(info)
            self.multi_lay.addWidget(row)
            self._multi_rows.append(row)
        self._fit_window()

    def _make_device_row(self, info: dict) -> QWidget:
        status = int(info.get("status", 0))
        mac = str(info.get("mac") or "")
        active = bool(info.get("active", status == int(vp.MC_CONNECTED_ACTIVE)))
        if status == int(vp.MC_DISCONNECTED):
            text, color, icon = "未连接", self.pal.text_dis, "dot"
        elif active:
            text, color, icon = "使用中", self.pal.accent, "check"
        else:
            text, color, icon = "已连接", self.pal.text_sec, "dot"
        row = QFrame()
        row.setObjectName("card")
        row.setFixedHeight(38)
        lay = QHBoxLayout(row)
        lay.setContentsMargins(10, 3, 10, 3)
        lay.setSpacing(8)
        lay.addWidget(Glyph(icon, self.pal, 14, color=color))
        name = QLabel(str(info.get("name") or "未知设备"))
        name.setToolTip(name.text())
        lay.addWidget(name, 1)
        chip = QLabel(text)
        chip.setObjectName("badge")
        chip.setStyleSheet(f"color: {color};")
        lay.addWidget(chip)
        # ---- 断开 / 连接按钮（任务 3）----
        connected = status != int(vp.MC_DISCONNECTED)
        is_local = self._is_local_row(info)
        btn = QPushButton("断开" if connected else "连接")
        btn.setCursor(Qt.PointingHandCursor)
        if is_local:
            btn.setObjectName("devAction")
            btn.setToolTip("使用 Windows 标准蓝牙接口操作本机连接"
                           "（需在真机确认一次）")
            btn.clicked.connect(
                lambda _=False, m=mac, c=connected: self._on_local_action(m, c))
        else:
            btn.setObjectName("devActionExp")
            btn.setText("断开" if connected else "连接")
            btn.setToolTip("实验功能：使用私有命令 0x014A 下发，"
                           "未在真机验证、可能不生效。")
            btn.clicked.connect(
                lambda _=False, m=mac, c=connected: self._on_remote_action(m, c))
        lay.addWidget(btn)
        return row

    def _is_local_row(self, info: dict) -> bool:
        try:
            return bool(self.ctrl.is_local_device(
                str(info.get("name") or ""), str(info.get("mac") or "")))
        except Exception:
            return False

    def _on_local_action(self, mac: str, was_connected: bool) -> None:
        """本机行：走 Windows 标准接口断开 / 连接（非私有协议）。"""
        if not mac:
            self._toast("缺少设备 MAC，无法操作。")
            return
        ok = (self.ctrl.local_disconnect(mac) if was_connected
              else self.ctrl.local_connect(mac))
        self._toast("已下发本机%s请求（需在真机确认）。"
                    % ("断开" if was_connected else "连接"))

    def _on_remote_action(self, mac: str, was_connected: bool) -> None:
        """其它设备行：实验性地走 0x014A 全量下发；不谎报成功。"""
        if not mac:
            self._toast("缺少设备 MAC，无法操作。")
            return
        self.ctrl.set_multi_connect_status(mac, connect=not was_connected)
        self._toast("已下发实验命令，2 秒后回查；命令可能未生效。")

    def _toast(self, text: str) -> None:
        """轻量提示：写到状态栏副标题，5 秒后恢复连接状态。"""
        self.fw_lab.setText(text)
        QTimer.singleShot(
            5000, lambda: self.fw_lab.setText("已连接"
                                              if self._connected else "未连接"))

    # ------------------------------------------------------------------ #
    # 关闭行为（最小化到托盘 / 直接退出）
    # ------------------------------------------------------------------ #
    def _on_close_clicked(self) -> None:
        if self.settings.value("close_action", "tray", type=str) == "exit":
            self._quit_app()
        else:
            self.hide_flyout()

    def closeEvent(self, event) -> None:
        if self.settings.value("close_action", "tray", type=str) == "exit":
            event.accept()
            self._quit_app()
        else:
            event.ignore()
            self.hide_flyout()

    def set_close_action(self, key: str) -> None:
        self.settings.setValue(
            "close_action", "exit" if key == "exit" else "tray"
        )

    def _quit_app(self) -> None:
        cb = getattr(self, "quit_callback", None)
        if callable(cb):
            cb()
        else:
            QApplication.quit()

    # ------------------------------------------------------------------ #
    # 跟随系统控制中心（Win11 快速设置）
    # ------------------------------------------------------------------ #
    def set_follow_control_center(self, on: bool) -> None:
        self.settings.setValue("follow_control_center", bool(on))
        cb = getattr(self, "cc_watch_callback", None)
        if callable(cb):
            cb(bool(on))

    def _toggle_follow_cc(self, on: bool) -> None:
        self.set_follow_control_center(on)

    def _toggle_cc_log(self, on: bool) -> None:
        """控制中心检测日志开关（默认开）。"""
        self.settings.setValue("ccwatch_log", bool(on))
        cb = getattr(self, "cc_log_callback", None)
        if callable(cb):
            cb(bool(on))

    def show_for_control_center(self, rect=None) -> None:
        """系统控制中心打开：叠加到它正上方，并在其关闭时同步收起。"""
        self._cc_follow = True
        self._overlay_mode = True
        if rect:
            self._anchor_above(rect)
        else:
            self.setGeometry(self._anchor_geometry())
        if not self.isVisible():
            self.show()
            if IS_WINDOWS:
                apply_acrylic(self, self.dark)
        self.raise_()
        if IS_WINDOWS:
            force_topmost(self)
        self._anim.stop()
        self._anim.setStartValue(self._opacity.opacity())
        self._anim.setEndValue(1.0)
        self._anim.start()
        QTimer.singleShot(120, lambda: IS_WINDOWS and force_topmost(self))

    def hide_for_control_center(self) -> None:
        """系统控制中心关闭：同步收起浮窗（用户已在操作浮窗时不跟随）。"""
        if self._cc_follow and self.isVisible():
            self.hide_flyout()

    def _anchor_above(self, rect) -> None:
        """把浮窗放到控制中心面板正上方的右侧，并夹在屏幕可视区内。"""
        try:
            _left, top, right, _bottom = (int(v) for v in rect)
        except Exception:
            self.setGeometry(self._anchor_geometry())
            return
        screen = QGuiApplication.screenAt(QPoint(right, top))
        if screen is None:
            screen = QGuiApplication.primaryScreen()
        geo = screen.availableGeometry()
        w, h = self.size().width(), self.size().height()
        x = max(geo.left() + 8, min(right - w, geo.right() - w - 8))
        y = max(geo.top() + 8, min(top - h - 8, geo.bottom() - h - 8))
        self.setGeometry(QRect(x, y, w, h))

    # ------------------------------------------------------------------ #
    # 设备选择 / 菜单
    # ------------------------------------------------------------------ #
    def _choose_device(self) -> None:
        self.error_lab.setText("正在扫描已配对设备…")
        QApplication.processEvents()
        devices = self.ctrl.discover()
        menu = QMenu(self)
        if devices:
            for d in devices:
                act = QAction(d.name, self)
                act.triggered.connect(lambda _=False, info=d: self.ctrl.connect_device(info))
                menu.addAction(act)
        else:
            none = QAction("未发现已配对的 vivo / iQOO 耳机", self)
            none.setEnabled(False)
            menu.addAction(none)
        menu.addSeparator()
        demo = QAction("演示模式（无需耳机）", self)
        demo.triggered.connect(self.ctrl.connect_demo)
        menu.addAction(demo)
        menu.exec(self.connect_btn.mapToGlobal(QPoint(0, self.connect_btn.height() + 6)))

    def _show_menu(self) -> None:
        menu = QMenu(self)
        reconnect = QAction("重新连接当前设备", self)
        reconnect.triggered.connect(self._reconnect)
        menu.addAction(reconnect)
        choose = QAction("选择其他设备…", self)
        choose.triggered.connect(self._choose_device)
        menu.addAction(choose)
        demo = QAction("进入演示模式", self)
        demo.triggered.connect(self.ctrl.connect_demo)
        menu.addAction(demo)
        menu.addSeparator()
        theme_act = QAction("深色模式", self, checkable=True)
        theme_act.setChecked(self.dark)
        theme_act.triggered.connect(self.toggle_theme)
        menu.addAction(theme_act)
        follow_act = QAction("跟随 Win+A 弹出", self, checkable=True)
        follow_act.setChecked(self.settings.value("follow_win_a", True, type=bool))
        follow_act.triggered.connect(self._toggle_follow)
        menu.addAction(follow_act)
        cc_act = QAction("跟随系统控制中心弹出 / 关闭", self, checkable=True)
        cc_act.setChecked(
            self.settings.value("follow_control_center", True, type=bool))
        cc_act.triggered.connect(self._toggle_follow_cc)
        menu.addAction(cc_act)
        cc_log_act = QAction("记录控制中心检测日志", self, checkable=True)
        cc_log_act.setChecked(self.settings.value("ccwatch_log", True, type=bool))
        cc_log_act.triggered.connect(self._toggle_cc_log)
        menu.addAction(cc_log_act)
        collapse_act = QAction("折叠面板", self, checkable=True)
        collapse_act.setChecked(self.collapsed)
        collapse_act.triggered.connect(self._apply_collapsed)
        menu.addAction(collapse_act)

        close_menu = menu.addMenu("关闭按钮行为")
        close_grp = QActionGroup(close_menu)
        close_grp.setExclusive(True)
        current = self.settings.value("close_action", "tray", type=str)
        for key, text in (("tray", "最小化到托盘（后台常驻）"),
                          ("exit", "直接退出程序")):
            act = QAction(text, close_menu, checkable=True)
            act.setChecked(current == key)
            act.triggered.connect(lambda _=False, k=key: self.set_close_action(k))
            close_grp.addAction(act)
            close_menu.addAction(act)

        pin_act = QAction("固定浮窗", self, checkable=True)
        pin_act.setChecked(self.pinned)
        pin_act.triggered.connect(self._toggle_pin)
        menu.addAction(pin_act)
        menu.addSeparator()
        quit_act = QAction("退出", self)
        quit_act.triggered.connect(self._quit_app)
        menu.addAction(quit_act)
        menu.exec(self.menu_btn.mapToGlobal(QPoint(0, self.menu_btn.height() + 6)))

    def _reconnect(self) -> None:
        last = self.settings.value("last_device_mac", "")
        name = self.settings.value("last_device_name", "")
        if last and name:
            self.ctrl.connect_device(DeviceInfo(name, last))
        else:
            self._choose_device()

    def _toggle_follow(self, on: bool) -> None:
        self.settings.setValue("follow_win_a", on)
        if hasattr(self, "hotkeys"):
            self.hotkeys.set_follow_win_a(on)

    def _toggle_pin(self) -> None:
        self.pinned = not self.pinned
        self.pin_btn.setChecked(self.pinned)
        self.pin_btn.setToolTip("取消固定" if self.pinned else "固定浮窗（不随失焦收起）")

    # ------------------------------------------------------------------ #
    # 显示 / 隐藏 / 定位
    # ------------------------------------------------------------------ #
    def _anchor_geometry(self) -> QRect:
        screen = QGuiApplication.screenAt(QCursor_pos()) or QGuiApplication.primaryScreen()
        geo = screen.availableGeometry()
        w, h = self.size().width(), self.size().height()
        x = geo.right() - w - 8
        y = geo.bottom() - h - 8
        return QRect(x, y, w, h)

    def show_flyout(self, overlay: bool = False) -> None:
        """overlay=True 表示跟随 Win+A：弹出后再置顶一次，盖在系统控制中心上。"""
        self._overlay_mode = overlay
        self._cc_follow = bool(overlay)
        self.setGeometry(self._anchor_geometry())
        if not self.isVisible():
            self.show()
            if IS_WINDOWS:
                apply_acrylic(self, self.dark)
        self.raise_()
        if IS_WINDOWS:
            force_topmost(self)
        self._anim.stop()
        self._anim.setStartValue(self._opacity.opacity())
        self._anim.setEndValue(1.0)
        self._anim.start()
        if overlay:
            QTimer.singleShot(120, lambda: IS_WINDOWS and force_topmost(self))

    def hide_flyout(self) -> None:
        self._anim.stop()
        self._anim.setStartValue(self._opacity.opacity())
        self._anim.setEndValue(0.0)
        self._anim.start()
        QTimer.singleShot(180, self._safe_hide)

    def _safe_hide(self) -> None:
        if self._opacity.opacity() <= 0.02:
            self.hide()

    def toggle_flyout(self) -> None:
        if self.isVisible() and self._opacity.opacity() > 0.5:
            self.hide_flyout()
        else:
            self.show_flyout()

    def show_after_win_a(self) -> None:
        """系统控制中心已弹出（Win+A），在它上方叠加浮窗；
        再次按 Win+A 关闭控制中心时同步收起浮窗。"""
        if self._overlay_mode and self.isVisible() and self._opacity.opacity() > 0.5:
            self.hide_flyout()
        else:
            self._cc_follow = True
            self.show_flyout(overlay=True)

    # ------------------------------------------------------------------ #
    # 事件
    # ------------------------------------------------------------------ #
    def event(self, e: QEvent) -> bool:
        if e.type() == QEvent.ActivationChange:
            if self.isActiveWindow():
                # 用户点击了浮窗：退出叠加模式，恢复正常失焦收起，
                # 同时不再跟随系统控制中心的关闭
                self._overlay_mode = False
                self._cc_follow = False
            else:
                self._defer_timer.start()
        return super().event(e)

    def _maybe_hide(self) -> None:
        if self.pinned or self._overlay_mode or not self.isVisible():
            return
        aw = QApplication.activeWindow()
        pop = QApplication.activePopupWidget()
        if aw is not self and pop is None:
            self.hide_flyout()

    def mousePressEvent(self, event) -> None:
        if event.button() == Qt.LeftButton and event.position().y() <= 42:
            self._drag_pos = event.globalPosition().toPoint() - self.frameGeometry().topLeft()
            event.accept()

    def mouseMoveEvent(self, event) -> None:
        if hasattr(self, "_drag_pos") and event.buttons() & Qt.LeftButton:
            self.move(event.globalPosition().toPoint() - self._drag_pos)
            event.accept()

    def nativeEvent(self, eventType, message):
        if IS_WINDOWS and eventType == b"windows_generic_MSG":
            msg = wintypes_MSG(message)
            if msg.message == 0x0312:  # WM_HOTKEY
                self.toggle_flyout()
                return True, 0
        return super().nativeEvent(eventType, message)


def QCursor_pos() -> QPoint:
    from PySide6.QtGui import QCursor
    return QCursor.pos()


def wintypes_MSG(message):
    """把 PySide6 传入的 MSG 指针转为 ctypes 结构。"""
    from ctypes import wintypes
    return wintypes.MSG.from_address(int(message))
