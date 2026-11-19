"""
vivo / iQOO 型号能力矩阵（数据源：官方 App 反编译的 tws_config.json，
移植自 Zhaoyi-ya/TWS-Pods-PC，GPL-3.0-only）。

已知型号按声明能力显示控件；未知型号乐观全开，运行期靠查询超时收敛。
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import FrozenSet, Optional

from .vivo_models import normalize

NOISE = "noise"
FIND = "find"
SPATIAL = "spatial"
DUAL = "dual"
GAME = "game"
WEAR = "wear"
EQ = "eq"


# 归一化型号名 → 能力集合
_KNOWN: dict[str, FrozenSet[str]] = {
    "iqooheadphones": frozenset({NOISE, FIND, SPATIAL, DUAL, GAME, EQ}),
    "iqootws1": frozenset({NOISE, FIND}),
    "iqootws1e": frozenset({NOISE, FIND}),
    "iqootws2": frozenset({NOISE, FIND, GAME}),
    "iqootws5": frozenset({NOISE, FIND, SPATIAL, DUAL, GAME}),
    "iqootws5e": frozenset({NOISE, FIND, DUAL, GAME}),
    "iqootwsair": frozenset({FIND}),
    "iqootwsair3": frozenset({FIND, GAME}),
    "iqootwsair3pro": frozenset({NOISE, FIND, GAME}),
    "iqootwsairpro": frozenset({NOISE, FIND}),
    "vivoheadphones": frozenset({NOISE, FIND, SPATIAL, DUAL, GAME, EQ}),
    "vivotws2": frozenset({NOISE, FIND}),
    "vivotws2e": frozenset({FIND}),
    "vivotws3": frozenset({NOISE, FIND, WEAR}),
    "vivotws3e": frozenset({NOISE, FIND, WEAR, DUAL}),
    "vivotws3pro": frozenset({NOISE, FIND, SPATIAL}),
    "vivotws4": frozenset({NOISE, FIND, GAME, SPATIAL}),
    "vivotws4hifi": frozenset({NOISE, FIND, GAME, SPATIAL}),
    "vivotws5": frozenset({NOISE, FIND, SPATIAL, DUAL, GAME}),
    "vivotws5e": frozenset({NOISE, FIND, DUAL, GAME}),
    "vivotws5hifi": frozenset({NOISE, FIND, SPATIAL, DUAL, GAME}),
    "vivotws5pro": frozenset({NOISE, FIND, SPATIAL, DUAL, GAME}),
    "vivotwsair3pro": frozenset({NOISE, FIND, GAME}),
    "vivotwsairpro": frozenset({NOISE, FIND}),
    "vivotwsx1": frozenset({NOISE, FIND}),
}

# 硬件强制开启、无开关的功能（如 TWS 3e 双连常开）
_FORCED: dict[str, FrozenSet[str]] = {
    "vivotws3e": frozenset({DUAL}),
}

# 尚未完成全型号真机验证的命令（在 UI 上加「实验」角标）
EXPERIMENTAL = frozenset({SPATIAL, GAME})


@dataclass
class FeatureSet:
    supported: FrozenSet[str]
    forced: FrozenSet[str]

    def has(self, feature: str) -> bool:
        return feature in self.supported

    def visible(self, feature: str) -> bool:
        """有能力且非强制常开，才显示可操作控件。"""
        return feature in self.supported and feature not in self.forced

    def is_experimental(self, feature: str) -> bool:
        return feature in self.supported and feature in EXPERIMENTAL


def resolve(device_name: Optional[str]) -> FeatureSet:
    key = normalize(device_name or "")
    supported = _KNOWN.get(key)
    if supported is None:
        # 未知型号：乐观全开，让运行期查询决定
        supported = frozenset({NOISE, FIND, SPATIAL, DUAL, GAME, WEAR, EQ})
    forced = _FORCED.get(key, frozenset())
    return FeatureSet(supported=supported, forced=forced)
