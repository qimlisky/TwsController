"""
vivo / iQOO 型号识别与 wire Profile 选择。
移植自 Zhaoyi-ya/TWS-Pods-PC（GPL-3.0-only）。
"""

from __future__ import annotations

from typing import Optional

from .vivo_protocol import AIR3_PRO_CAPTURED, FAMILY_DEFAULT_V4, TWS_3E_V3, Profile

VIVO_TWS_PREFIX = "vivotws"
IQOO_TWS_PREFIX = "iqootws"


def normalize(value: str) -> str:
    return "".join(c for c in value.lower() if c.isalnum())


_MODELS = [
    "vivo TWS 5 Pro", "vivo TWS 5 Hi-Fi", "vivo TWS 5", "vivo TWS 5e",
    "vivo TWS 4 Hi-Fi", "vivo TWS 4",
    "vivo TWS Air3 Pro", "vivo TWS Air3",
    "vivo TWS 3 Pro", "vivo TWS 3e", "vivo TWS 3",
    "vivo TWS 2e", "vivo TWS 2",
    "vivo TWS A1 Pro", "vivo TWS A1",
    "vivo TWS Air2", "vivo TWS Air Pro", "vivo TWS Air",
    "vivo TWS Neo", "vivo TWS X1", "vivo TWS 1", "vivo TWS",
    "iQOO TWS 5", "iQOO TWS 2", "iQOO TWS 1e", "iQOO TWS 1",
    "iQOO TWS Air3 Pro", "iQOO TWS Air Pro", "iQOO TWS Air",
]

MODEL_PROFILES = {
    "vivo TWS Air3 Pro": AIR3_PRO_CAPTURED,
    "vivo TWS 3e": TWS_3E_V3,
}


def is_family_name(device_name: Optional[str]) -> bool:
    if not device_name:
        return False
    n = normalize(device_name)
    return n.startswith(VIVO_TWS_PREFIX) or n.startswith(IQOO_TWS_PREFIX)


def find_model(device_name: Optional[str]) -> Optional[str]:
    if not device_name:
        return None
    n = normalize(device_name)
    for canonical in _MODELS:
        if n == normalize(canonical):
            return canonical
    return None


def select_profile(device_name: Optional[str]) -> Profile:
    return MODEL_PROFILES.get(find_model(device_name) or "", FAMILY_DEFAULT_V4)


def display_name(device_name: Optional[str]) -> str:
    exact = find_model(device_name)
    if exact:
        return exact
    if is_family_name(device_name):
        return "vivo / iQOO TWS（家族兼容）"
    return device_name or "未知设备"
