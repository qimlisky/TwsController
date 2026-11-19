"""
Win11 Fluent 风格基础控件：SVG 图标、ToggleSwitch、分段按钮、控制行、电量胶囊。
"""

from __future__ import annotations

import os
import sys
from typing import List, Optional

from PySide6.QtCore import (
    QEasingCurve, QPoint, QPropertyAnimation, QRect, QRectF, QSize, Qt,
    Property, Signal,
)
from PySide6.QtGui import QColor, QFont, QPainter, QPainterPath, QPen, QPixmap
from PySide6.QtWidgets import (
    QButtonGroup, QFrame, QHBoxLayout, QLabel, QPushButton, QSizePolicy,
    QVBoxLayout, QWidget,
)
from PySide6.QtSvg import QSvgRenderer

from .theme import ICONS, FluentPalette


# --------------------------------------------------------------------------- #
# 资源路径（源码运行 / PyInstaller 单文件打包都可用）
# --------------------------------------------------------------------------- #

def resource_path(rel: str) -> str:
    """返回打包资源（如 assets/pods.png）的绝对路径。

    PyInstaller 单文件模式把 datas 解包到 sys._MEIPASS；源码运行时退回到
    本文件所在包目录的上一级（工程根目录）。
    """
    base = getattr(sys, "_MEIPASS", None)
    if base:
        return os.path.join(base, rel)
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    return os.path.join(root, rel)


# 耳机真机图（vivo TWS 5）。文件缺失时静默回退到矢量图标。
PODS_IMAGE_REL = os.path.join("assets", "pods.png")

# --------------------------------------------------------------------------- #
# 画框 / 取景参数（任务 A：实拍产品图取景微调）
# 耳机图标改为「正方形圆角画框」，默认把整张图完整放进画框、不裁切，
# 因此充电盒与两只耳机都能完整显示；如需微调构图，改下面几个常量即可。
# --------------------------------------------------------------------------- #
ICON_CORNER_RADIUS = 6   # 画框圆角半径（px）；设 0 即纯直角正方形
CROP_ZOOM = 1.0          # 缩放倍数：1.0=整图完整显示；<1 四周留白；>1 重新裁切
CROP_OFFSET_X = 0.0      # 水平偏移：-0.5~0.5，正数向右（仅 CROP_ZOOM>1 裁切时可见）
CROP_OFFSET_Y = 0.0      # 垂直偏移：-0.5~0.5，正数向下（仅 CROP_ZOOM>1 裁切时可见）
CROP_STROKE = True       # 是否绘制 1px 描边（沿正方形/圆角矩形，贴合 Fluent 卡片风格）


def load_pods_photo(size: int, pal: FluentPalette,
                    dpr: float = 1.0) -> Optional[QPixmap]:
    """把 assets/pods.png 处理成「正方形圆角画框」图标：整图完整放入、不裁切。

    任务 A（v3.2）改动要点：
    - 缩放由 cover 改为 contain（KeepAspectRatio）：整张图完整放进画框，
      充电盒与两只耳机都完整显示（旧版 cover 会裁掉边缘）；
    - 遮罩由圆形（addEllipse）改为圆角矩形（addRoundedRect），
      圆角半径 ICON_CORNER_RADIUS，设 0 即纯直角正方形；
    - CROP_ZOOM：1.0=完整显示；<1 四周留白；>1 才重新裁切（此时
      CROP_OFFSET_X / CROP_OFFSET_Y 生效）；
    - CROP_STROKE 控制是否沿画框绘制 1px 描边（颜色取 pal.stroke，跟随主题）；
    - dpr（设备像素比）用于高 DPI 屏，避免 2x 屏发糊。

    任何一步失败（文件缺失 / 解码失败）都返回 None，调用方回退矢量图标。
    仅做缩放 / 裁切，不加滤镜 / 暗角等额外处理，保持原图观感。
    """
    path = resource_path(PODS_IMAGE_REL)
    src = QPixmap(path)
    if src.isNull():
        return None
    try:
        logical = int(size)
        dpr = float(dpr) if dpr and dpr > 0 else 1.0
        px = max(1, int(round(logical * dpr)))
        # 内容框边长：CROP_ZOOM=1 时等于画框，整图刚好铺满画框（短边方向留白）
        zoom = max(0.05, float(CROP_ZOOM))
        box = max(1, int(round(px * zoom)))
        # contain：整图等比缩放到完全放进 box×box，绝不裁切
        scaled = src.scaled(
            box, box,
            Qt.KeepAspectRatio,
            Qt.SmoothTransformation,
        )
        out = QPixmap(px, px)
        out.fill(Qt.transparent)
        p = QPainter(out)
        p.setRenderHint(QPainter.Antialiasing)
        p.setRenderHint(QPainter.SmoothPixmapTransform)
        # 正方形圆角画框（圆角半径随 dpr 换算物理像素）
        radius = max(0.0, float(ICON_CORNER_RADIUS) * dpr)
        clip = QPainterPath()
        clip.addRoundedRect(QRectF(0.0, 0.0, float(px), float(px)),
                            radius, radius)
        p.setClipPath(clip)
        # 居中放置；CROP_ZOOM>1 时图大于画框，由上面的 clip 裁切，
        # 此时偏移量才可见（以画框边长为 1.0）
        dx = (px - scaled.width()) / 2.0 - float(CROP_OFFSET_X) * px
        dy = (px - scaled.height()) / 2.0 - float(CROP_OFFSET_Y) * px
        p.drawPixmap(
            QRectF(dx, dy, float(scaled.width()), float(scaled.height())),
            scaled,
            QRectF(0.0, 0.0, float(scaled.width()), float(scaled.height())),
        )
        if CROP_STROKE:
            # 1px 描边：沿圆角矩形绘制（用逻辑像素换算，贴合当前 dpr）
            p.setClipping(False)
            stroke_w = max(1.0, dpr)
            pen = QPen(QColor(pal.stroke))
            pen.setWidthF(stroke_w)
            inset = stroke_w / 2.0
            p.setPen(pen)
            p.setBrush(Qt.NoBrush)
            rr = max(0.0, radius - inset)
            p.drawRoundedRect(
                QRectF(inset, inset, float(px) - stroke_w, float(px) - stroke_w),
                rr, rr)
        p.end()
        out.setDevicePixelRatio(dpr)
        return out
    except Exception:
        return None


# --------------------------------------------------------------------------- #
# SVG 图标
# --------------------------------------------------------------------------- #

def svg_icon(name: str, color: str, size: int = 18) -> QSvgRenderer:
    path = ICONS.get(name, "")
    svg = (
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" '
        f'viewBox="0 0 24 24" fill="none" stroke="rgb({_rgb(color)})" '
        f'stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round">'
        f'<path d="{path}"/></svg>'
    )
    return QSvgRenderer(svg.encode("utf-8"))


def _rgb(color: str) -> str:
    c = QColor(color)
    return f"{c.red()},{c.green()},{c.blue()}"


class Glyph(QWidget):
    """按当前主题色绘制的 SVG 图标。"""

    def __init__(self, name: str, pal: FluentPalette, size: int = 18,
                 color: Optional[str] = None, parent=None) -> None:
        super().__init__(parent)
        self._name = name
        self._pal = pal
        self._size = size
        self._color = color
        self.setFixedSize(size + 4, size + 4)

    def recolor(self, pal: FluentPalette, color: Optional[str] = None) -> None:
        self._pal = pal
        self._color = color
        self.update()

    def paintEvent(self, event) -> None:
        color = self._color or self._pal.icon
        renderer = svg_icon(self._name, color, self._size)
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        rect = QRectF(2, 2, self._size, self._size)
        renderer.render(p, rect)


class IconButton(QPushButton):
    def __init__(self, name: str, pal: FluentPalette, tip: str = "",
                 size: int = 32, parent=None) -> None:
        super().__init__(parent)
        self.setObjectName("icon")
        self._name = name
        self._pal = pal
        self.setFixedSize(size, size)
        self.setCursor(Qt.PointingHandCursor)
        self.setToolTip(tip)
        self._render()

    def recolor(self, pal: FluentPalette) -> None:
        self._pal = pal
        self._render()

    def set_icon_name(self, name: str) -> None:
        """切换图标（名字取自 theme.ICONS）。"""
        if name != self._name:
            self._name = name
            self._render()

    def _render(self) -> None:
        from PySide6.QtGui import QIcon, QPixmap
        pm = QPixmap(18, 18)
        pm.fill(Qt.transparent)
        p = QPainter(pm)
        p.setRenderHint(QPainter.Antialiasing)
        svg_icon(self._name, self._pal.icon, 18).render(p, QRectF(0, 0, 18, 18))
        p.end()
        self.setIcon(QIcon(pm))
        self.setIconSize(QSize(18, 18))


class PodsPhoto(QWidget):
    """耳机头像：优先绘制 assets/pods.png（正方形圆角画框真机图，
    任务 A：整图完整显示、不裁切），文件缺失或解码失败时自动回退到
    矢量 Glyph，绝不白屏。"""

    def __init__(self, pal: FluentPalette, size: int = 20, parent=None) -> None:
        super().__init__(parent)
        self._pal = pal
        self._size = int(size)
        side = self._size + 4
        self.setFixedSize(side, side)
        self._pm = None
        self._load()

    def _load(self) -> None:
        dpr = float(self.devicePixelRatioF()) or 1.0
        self._pm = load_pods_photo(self._size, self._pal, dpr)

    def recolor(self, pal: FluentPalette) -> None:
        self._pal = pal
        self._load()
        self.update()

    def set_size(self, size: int) -> None:
        self._size = int(size)
        side = self._size + 4
        self.setFixedSize(side, side)
        self._load()
        self.update()

    def paintEvent(self, event) -> None:
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        p.setRenderHint(QPainter.SmoothPixmapTransform)
        if self._pm is not None and not self._pm.isNull():
            # 居中绘制（QPixmap 已带 devicePixelRatio，逻辑尺寸即 size）
            w = self._pm.width() / max(1.0, self._pm.devicePixelRatio())
            h = self._pm.height() / max(1.0, self._pm.devicePixelRatio())
            x = (self.width() - w) / 2.0
            y = (self.height() - h) / 2.0
            p.drawPixmap(QRectF(x, y, w, h), self._pm,
                         QRectF(0, 0, self._pm.width(), self._pm.height()))
            return
        # 兜底：矢量耳机图标
        renderer = svg_icon("headphone", self._pal.icon, self._size)
        p.translate(2, 2)
        renderer.render(p, QRectF(0, 0, self._size, self._size))


# --------------------------------------------------------------------------- #
# Win11 ToggleSwitch
# --------------------------------------------------------------------------- #

class ToggleSwitch(QWidget):
    toggled = Signal(bool)

    def __init__(self, pal: FluentPalette, parent=None) -> None:
        super().__init__(parent)
        self._pal = pal
        self._on = False
        self._knob = 4.0
        self.setFixedSize(40, 20)
        self.setCursor(Qt.PointingHandCursor)
        self._anim = QPropertyAnimation(self, b"knob", self)
        self._anim.setDuration(140)
        self._anim.setEasingCurve(QEasingCurve.OutCubic)

    @Property(float)
    def knob(self) -> float:
        return self._knob

    @knob.setter
    def knob(self, v: float) -> None:
        self._knob = v
        self.update()

    def palette_changed(self, pal: FluentPalette) -> None:
        self._pal = pal
        self.update()

    def is_on(self) -> bool:
        return self._on

    def set_on(self, on: bool, animate: bool = True) -> None:
        """用户操作入口：更新视觉并发出 toggled。"""
        if on == self._on:
            return
        self._on = on
        self._anim.stop()
        self._anim.setStartValue(self._knob)
        self._anim.setEndValue(20.0 if on else 4.0)
        if animate:
            self._anim.start()
        else:
            self.knob = 20.0 if on else 4.0
        self.toggled.emit(on)

    def sync_state(self, on: bool) -> None:
        """外部状态同步入口：只更新视觉，不发 toggled（避免命令回环）。"""
        self._on = on
        self._anim.stop()
        self.knob = 20.0 if on else 4.0
        self.update()

    def mousePressEvent(self, event) -> None:
        if event.button() == Qt.LeftButton:
            self.set_on(not self._on)

    def paintEvent(self, event) -> None:
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        track_color = QColor(self._pal.track_on if self._on else self._pal.track)
        p.setPen(Qt.NoPen)
        p.setBrush(track_color)
        p.drawRoundedRect(0, 0, 40, 20, 10, 10)
        # 滑块
        p.setBrush(QColor("#FFFFFF"))
        x = self._knob
        p.drawEllipse(QRectF(x, 3.5, 13, 13))


# --------------------------------------------------------------------------- #
# 分段控件（降噪 | 关闭 | 通透）
# --------------------------------------------------------------------------- #

class Segmented(QFrame):
    changed = Signal(int)

    def __init__(self, pal: FluentPalette, items: List[tuple], parent=None,
                 compact: bool = False) -> None:
        """items: [(key, icon_name, text), ...]
        compact=True 时用于折叠态：更矮、更小的控件。"""
        super().__init__(parent)
        self.setObjectName("card")
        self._pal = pal
        self._compact = bool(compact)
        self._buttons: List[QPushButton] = []
        lay = QHBoxLayout(self)
        lay.setContentsMargins(3, 3, 3, 3)
        lay.setSpacing(2)
        self._group = QButtonGroup(self)
        self._group.setExclusive(True)
        for key, icon_name, text in items:
            btn = QPushButton(text)
            btn.setObjectName("segCompact" if self._compact else "seg")
            btn.setCheckable(True)
            btn.setCursor(Qt.PointingHandCursor)
            btn.setProperty("key", key)
            lay.addWidget(btn)
            self._group.addButton(btn, key)
            self._buttons.append(btn)
        self._group.idClicked.connect(self.changed.emit)

    def set_selected(self, key: int) -> None:
        btn = self._group.button(key)
        if btn and not btn.isChecked():
            btn.setChecked(True)

    def set_enabled(self, enabled: bool) -> None:
        for b in self._buttons:
            b.setEnabled(enabled)


# --------------------------------------------------------------------------- #
# 控制行（图标 + 标题 + 副标题 + 角标 + 右侧控件）
# --------------------------------------------------------------------------- #

class ControlRow(QFrame):
    def __init__(self, pal: FluentPalette, icon_name: str, title: str,
                 subtitle: str = "", badge: str = "", parent=None) -> None:
        super().__init__(parent)
        self.setObjectName("card")
        self._pal = pal
        self.setFixedHeight(48)
        lay = QHBoxLayout(self)
        lay.setContentsMargins(10, 6, 10, 6)
        lay.setSpacing(10)

        self.glyph = Glyph(icon_name, pal, 18)
        lay.addWidget(self.glyph)

        col = QVBoxLayout()
        col.setSpacing(0)
        title_row = QHBoxLayout()
        title_row.setSpacing(6)
        self.title_lab = QLabel(title)
        f = QFont()
        f.setWeight(QFont.DemiBold)
        self.title_lab.setFont(f)
        title_row.addWidget(self.title_lab)
        if badge:
            self.badge = QLabel(badge)
            self.badge.setObjectName("badge")
            title_row.addWidget(self.badge)
        title_row.addStretch(1)
        col.addLayout(title_row)
        if subtitle:
            self.sub_lab = QLabel(subtitle)
            self.sub_lab.setObjectName("subtitle")
            col.addWidget(self.sub_lab)
        else:
            col.addStretch(1)
        lay.addLayout(col, 1)
        self.right_holder = QHBoxLayout()
        self.right_holder.setSpacing(6)
        lay.addLayout(self.right_holder)

    def add_right(self, w: QWidget) -> None:
        self.right_holder.addWidget(w)


# --------------------------------------------------------------------------- #
# 电量胶囊
# --------------------------------------------------------------------------- #

class BatteryPill(QFrame):
    def __init__(self, pal: FluentPalette, label: str, parent=None) -> None:
        super().__init__(parent)
        self.setObjectName("card")
        self._pal = pal
        self._compact = False
        self.setFixedHeight(78)
        self.setMinimumWidth(92)
        lay = QVBoxLayout(self)
        lay.setContentsMargins(8, 7, 8, 6)
        lay.setSpacing(0)
        self._lay = lay

        top = QHBoxLayout()
        top.setSpacing(3)
        self.lab = QLabel(label)
        self.lab.setObjectName("batteryLabel")
        top.addWidget(self.lab)
        top.addStretch(1)
        self.bolt = Glyph("bolt", pal, 11, color=pal.accent)
        self.bolt.setVisible(False)
        top.addWidget(self.bolt)
        lay.addLayout(top)

        self.value = QLabel("—")
        self.value.setObjectName("batteryValue")
        lay.addWidget(self.value)

        # 盒电量“上次更新时间”小字（左右耳不显示，保持空白）
        self.caption = QLabel("")
        self.caption.setObjectName("batteryCaption")
        self.caption.setVisible(False)
        lay.addWidget(self.caption)
        lay.addStretch(1)

    def set_compact(self, compact: bool) -> None:
        """折叠态紧凑模式（任务 B）：更矮的胶囊 + 更小的字号，避免挤掉降噪控件。"""
        compact = bool(compact)
        if compact == self._compact:
            return
        self._compact = compact
        self._lay.setContentsMargins(*(8, 4, 8, 4) if compact else (8, 7, 8, 6))
        self.setFixedHeight(56 if compact else 78)
        self.setMinimumWidth(78 if compact else 92)
        self.lab.setObjectName("batteryLabelC" if compact else "batteryLabel")
        self.value.setObjectName("batteryValueC" if compact else "batteryValue")
        self.caption.setObjectName("batteryCaptionC" if compact else "batteryCaption")
        for w in (self.lab, self.value, self.caption):
            w.style().unpolish(w)
            w.style().polish(w)

    def set_caption(self, text: str) -> None:
        """设置电量下方的小字（如「上次更新 12:34」）；空串则隐藏。"""
        self.caption.setText(text or "")
        self.caption.setVisible(bool(text))

    def set_value(self, percent: int, charging: bool, stale: bool,
                  unknown_text: str = "—") -> None:
        if percent < 0:
            self.value.setText(unknown_text)
        else:
            self.value.setText(f"{percent}%")
        self.bolt.setVisible(charging)
        color = self._pal.text_dis if stale else self._pal.text
        self.value.setStyleSheet(f"color: {color};")

    def recolor(self, pal: FluentPalette) -> None:
        self._pal = pal
        self.bolt.recolor(pal, pal.accent)
        self.update()


# --------------------------------------------------------------------------- #
# 卡片标题
# --------------------------------------------------------------------------- #

def section_label(text: str) -> QLabel:
    lab = QLabel(text)
    lab.setObjectName("section")
    return lab


# --------------------------------------------------------------------------- #
# FlowLayout（自动换行，用于 EQ chip 等横向排不下的控件）
# --------------------------------------------------------------------------- #

from PySide6.QtWidgets import QLayout, QSizePolicy


class FlowLayout(QLayout):
    def __init__(self, parent=None, margin=0, hspace=6, vspace=6) -> None:
        super().__init__(parent)
        self._items: list = []
        self._hspace = hspace
        self._vspace = vspace
        self.setContentsMargins(margin, margin, margin, margin)

    def addItem(self, item) -> None:
        self._items.append(item)

    def horizontalSpacing(self) -> int:
        return self._hspace

    def verticalSpacing(self) -> int:
        return self._vspace

    def count(self) -> int:
        return len(self._items)

    def itemAt(self, index: int):
        return self._items[index] if 0 <= index < len(self._items) else None

    def takeAt(self, index: int):
        if 0 <= index < len(self._items):
            return self._items.pop(index)
        return None

    def expandingDirections(self):
        return Qt.Orientations(Qt.Orientation(0))

    def hasHeightForWidth(self) -> bool:
        return True

    def heightForWidth(self, width: int) -> int:
        return self._do_layout(QRect(0, 0, width, 0), test_only=True)

    def setGeometry(self, rect: QRect) -> None:
        super().setGeometry(rect)
        self._do_layout(rect, test_only=False)

    def sizeHint(self) -> QSize:
        return self.minimumSize()

    def minimumSize(self) -> QSize:
        size = QSize()
        for item in self._items:
            size = size.expandedTo(item.minimumSize())
        m = self.contentsMargins()
        size += QSize(m.left() + m.right(), m.top() + m.bottom())
        return size

    def _do_layout(self, rect: QRect, test_only: bool) -> int:
        m = self.contentsMargins()
        effective = rect.adjusted(m.left(), m.top(), -m.right(), -m.bottom())
        x, y = effective.x(), effective.y()
        line_height = 0
        for item in self._items:
            w = item.sizeHint().width()
            h = item.sizeHint().height()
            next_x = x + w + self._hspace
            if next_x - self._hspace > effective.right() and line_height > 0:
                x = effective.x()
                y = y + line_height + self._vspace
                next_x = x + w + self._hspace
                line_height = 0
            if not test_only:
                item.setGeometry(QRect(QPoint(x, y), item.sizeHint()))
            x = next_x
            line_height = max(line_height, h)
        return y + line_height - rect.y() + m.bottom()
