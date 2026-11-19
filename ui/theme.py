"""
Windows 11 Fluent 主题：深/浅色调色板 + QSS。
视觉对齐 Windows 11 控制中心（Quick Settings）：8px 圆角卡片、subtle 悬停层、
Accent 蓝选中态、Segoe UI 字体、紧凑型控制行。
"""

from __future__ import annotations

from dataclasses import dataclass


@dataclass
class FluentPalette:
    mode: str                 # "dark" / "light"
    bg: str                   # 卡片底层（亚克力之外的兜底色，带透明）
    card: str
    card_alt: str
    text: str
    text_sec: str
    text_dis: str
    stroke: str
    hover: str
    pressed: str
    accent: str
    accent_hover: str
    accent_text: str
    track: str
    track_on: str
    icon: str


DARK = FluentPalette(
    mode="dark",
    bg="rgba(32, 32, 32, 215)",
    card="rgba(255, 255, 255, 0.045)",
    card_alt="rgba(255, 255, 255, 0.07)",
    text="rgba(255, 255, 255, 0.96)",
    text_sec="rgba(255, 255, 255, 0.62)",
    text_dis="rgba(255, 255, 255, 0.36)",
    stroke="rgba(255, 255, 255, 0.08)",
    hover="rgba(255, 255, 255, 0.07)",
    pressed="rgba(255, 255, 255, 0.10)",
    accent="#4CC2FF",
    accent_hover="#6EBFFF",
    accent_text="#00314C",
    track="rgba(255, 255, 255, 0.18)",
    track_on="#4CC2FF",
    icon="rgba(255, 255, 255, 0.78)",
)

LIGHT = FluentPalette(
    mode="light",
    bg="rgba(243, 243, 243, 220)",
    card="rgba(0, 0, 0, 0.035)",
    card_alt="rgba(0, 0, 0, 0.06)",
    text="rgba(0, 0, 0, 0.90)",
    text_sec="rgba(0, 0, 0, 0.58)",
    text_dis="rgba(0, 0, 0, 0.34)",
    stroke="rgba(0, 0, 0, 0.08)",
    hover="rgba(0, 0, 0, 0.06)",
    pressed="rgba(0, 0, 0, 0.09)",
    accent="#0067C0",
    accent_hover="#1975C8",
    accent_text="#FFFFFF",
    track="rgba(0, 0, 0, 0.18)",
    track_on="#0067C0",
    icon="rgba(0, 0, 0, 0.72)",
)


def qss(p: FluentPalette) -> str:
    return f"""
* {{
    font-family: "Segoe UI Variable Display", "Segoe UI", "Microsoft YaHei UI",
                 "Microsoft YaHei", "PingFang SC", sans-serif;
    font-size: 12.5px;
    color: {p.text};
    outline: none;
}}
#root {{
    background: {p.bg};
    border-radius: 10px;
    border: 1px solid {p.stroke};
}}
#card {{
    background: {p.card};
    border: 1px solid {p.stroke};
    border-radius: 8px;
}}
#title {{ font-size: 14px; font-weight: 600; }}
#subtitle {{ font-size: 11.5px; color: {p.text_sec}; }}
#section {{ font-size: 11.5px; font-weight: 600; color: {p.text_sec}; }}
#footnote {{ font-size: 10.5px; color: {p.text_dis}; }}
#status {{ font-size: 11px; color: {p.text_sec}; }}

/* ---- 分段控件（降噪 | 关闭 | 通透）---- */
QPushButton#seg {{
    background: transparent;
    border: 1px solid transparent;
    border-radius: 5px;
    padding: 8px 6px;
    color: {p.text};
    font-weight: 500;
}}
QPushButton#seg:hover {{ background: {p.hover}; }}
QPushButton#seg:pressed {{ background: {p.pressed}; }}
QPushButton#seg:checked {{
    background: {p.accent};
    color: {p.accent_text};
    font-weight: 600;
}}
QPushButton#seg:disabled {{ color: {p.text_dis}; background: transparent; }}

/* ---- 普通行内按钮 / 图标按钮 ---- */
QPushButton#icon {{
    background: transparent;
    border: 1px solid transparent;
    border-radius: 5px;
    padding: 6px;
    color: {p.icon};
}}
QPushButton#icon:hover {{ background: {p.hover}; }}
QPushButton#icon:pressed {{ background: {p.pressed}; }}
QPushButton#icon:disabled {{ color: {p.text_dis}; }}

QPushButton#accent {{
    background: {p.accent};
    color: {p.accent_text};
    border: none;
    border-radius: 5px;
    padding: 8px 12px;
    font-weight: 600;
}}
QPushButton#accent:hover {{ background: {p.accent_hover}; }}
QPushButton#accent:disabled {{ background: {p.card_alt}; color: {p.text_dis}; }}

QPushButton#ghost {{
    background: {p.card};
    border: 1px solid {p.stroke};
    border-radius: 5px;
    padding: 7px 10px;
}}
QPushButton#ghost:hover {{ background: {p.hover}; }}
QPushButton#ghost:checked {{
    background: {p.accent};
    color: {p.accent_text};
    border-color: transparent;
}}

/* ---- EQ chip ---- */
QPushButton#chip {{
    background: {p.card};
    border: 1px solid {p.stroke};
    border-radius: 13px;
    padding: 6px 12px;
    color: {p.text};
}}
QPushButton#chip:hover {{ background: {p.hover}; }}
QPushButton#chip:checked {{
    background: {p.accent};
    color: {p.accent_text};
    border-color: transparent;
    font-weight: 600;
}}
QPushButton#chip:disabled {{ color: {p.text_dis}; }}

/* ---- Win11 风格 ToggleSwitch（QCheckBox）---- */
QCheckBox#switch {{ spacing: 0; }}
QCheckBox#switch::indicator {{
    width: 40px; height: 20px;
    border-radius: 10px;
    background: {p.track};
}}
QCheckBox#switch::indicator:checked {{
    background: {p.track_on};
}}

/* ---- 电量 ---- */
#batteryValue {{ font-size: 16px; font-weight: 600; }}
#batteryLabel {{ font-size: 10.5px; color: {p.text_sec}; }}
#batteryCaption {{ font-size: 9.5px; color: {p.text_dis}; }}
/* 折叠态紧凑电量（任务 B：同屏放下电量 + 降噪切换）*/
#batteryValueC {{ font-size: 13.5px; font-weight: 600; }}
#batteryLabelC {{ font-size: 10px; color: {p.text_sec}; }}
#batteryCaptionC {{ font-size: 9px; color: {p.text_dis}; }}
/* 折叠态紧凑分段控件 */
QPushButton#segCompact {{
    background: transparent;
    border: 1px solid transparent;
    border-radius: 5px;
    padding: 5px 4px;
    color: {p.text};
    font-weight: 500;
    font-size: 11.5px;
}}
QPushButton#segCompact:hover {{ background: {p.hover}; }}
QPushButton#segCompact:pressed {{ background: {p.pressed}; }}
QPushButton#segCompact:checked {{
    background: {p.accent};
    color: {p.accent_text};
    font-weight: 600;
}}
QPushButton#segCompact:disabled {{ color: {p.text_dis}; background: transparent; }}
QLabel#badge {{
    background: {p.card_alt};
    color: {p.text_sec};
    border-radius: 4px;
    padding: 1px 5px;
    font-size: 10px;
}}
/* 多设备列表行内的操作按钮（本机=普通；其它设备=实验）*/
QPushButton#devAction {{
    background: {p.card_alt};
    border: 1px solid {p.stroke};
    border-radius: 4px;
    padding: 3px 9px;
    font-size: 11px;
}}
QPushButton#devAction:hover {{ background: {p.hover}; }}
QPushButton#devAction:disabled {{ color: {p.text_dis}; }}
QPushButton#devActionExp {{
    background: transparent;
    border: 1px solid {p.stroke};
    border-radius: 4px;
    padding: 3px 8px;
    font-size: 10.5px;
    color: {p.text_sec};
}}
QPushButton#devActionExp:hover {{ background: {p.hover}; }}
QPushButton#devActionExp:disabled {{ color: {p.text_dis}; }}

/* ---- 菜单 / 工具提示 ---- */
QMenu {{
    background: {p.bg};
    border: 1px solid {p.stroke};
    border-radius: 8px;
    padding: 6px;
}}
QMenu::item {{ padding: 7px 22px 7px 14px; border-radius: 5px; }}
QMenu::item:selected {{ background: {p.hover}; }}
QToolTip {{
    background: {p.bg};
    color: {p.text};
    border: 1px solid {p.stroke};
    border-radius: 6px;
    padding: 5px 8px;
}}
QScrollArea, QScrollArea > QWidget, QScrollArea > QWidget > QWidget {{
    background: transparent;
}}
QScrollBar:vertical {{ width: 0; background: transparent; }}
QScrollBar::add-line, QScrollBar::sub-line {{ height: 0; background: transparent; }}
"""


ICONS = {
    "anc": "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zm0 0c-3 3-3 15 0 18 3-3 3-15 0-18z",
    "off": "M12 4a8 8 0 1 0 8 8h-2.2A5.8 5.8 0 1 1 12 6.2z",
    "trans": "M12 4c2.5 3.2 4 6.2 4 8.8a4 4 0 1 1-8 0C8 10.2 9.5 7.2 12 4z",
    "spatial": "M12 3l8 3.5v5c0 5-3.4 8-8 9.5-4.6-1.5-8-4.5-8-9.5v-5L12 3z",
    "game": "M6 9h12a3 3 0 0 1 3 3 3 3 0 0 1-5 2.2l-.6-.7H8.6l-.6.7A3 3 0 1 1 3 12a3 3 0 0 1 3-3z",
    "find": "M11 4a7 7 0 1 0 4.2 12.6l4.1 4.1 1.4-1.4-4.1-4.1A7 7 0 0 0 11 4z",
    "eq": "M5 10v4h2v-4H5zm4-3v10h2V7H9zm4 4v6h2v-6h-2zm4-5v14h2V6h-2z",
    "settings": "M12 9.5A2.5 2.5 0 1 0 12 14.5 2.5 2.5 0 0 0 12 9.5z",
    "pin": "M14 2v6l3 3v2h-4v7l-1 1-1-1v-7H7v-2l3-3V2z",
    "pin_off": "M14 2v6l3 3v2h-4v7l-1 1-1-1v-7",
    "close": "M7 7l10 10M17 7L7 17",
    "refresh": "M12 5a7 7 0 1 0 6.7 9",
    "headphone": "M4 14v-2a8 8 0 0 1 16 0v2M4 14a2 2 0 0 1 2-2h1v6H6a2 2 0 0 1-2-2zm16 0a2 2 0 0 0-2-2h-1v6h1a2 2 0 0 0 2-2z",
    "bolt": "M13 2L5 13h5l-1 9 8-11h-5z",
    "chevron_up": "M6 14.5l6-6 6 6",
    "chevron_down": "M6 9.5l6 6 6-6",
    "dot": "M12 9.2a2.8 2.8 0 1 0 0 5.6 2.8 2.8 0 0 0 0-5.6z",
    "check": "M5 12.6l4.2 4.2L19 7",
    "devices": "M3 6.5h11v8H3zM16.5 9h3.5v9.5h-3.5zM5.5 18.5h8",
}
