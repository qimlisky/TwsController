# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller 打包配置：把 VivoPodsCC 打成单文件 EXE。

用法（项目根目录）：
    pyinstaller --noconfirm --clean VivoPodsCC.spec
产物：dist\\VivoPodsCC.exe（64 位、单文件、无控制台窗口）

说明：
- ws2_32.dll / bthprops.cpl 是 Windows 系统组件，无需打包；
- 这里不用 --collect-all PySide6（会把整个 Qt 塞进去、体积巨大），
  而是用 PyInstaller 自带的 PySide6 hook 按实际 import 收集所需模块；
- 需要 PyInstaller >= 6.0（由 build.bat 自动安装）。
"""

from PyInstaller.utils.hooks import collect_submodules

hiddenimports = (
    # 项目自身的包
    collect_submodules("vivo")
    + collect_submodules("ui")
    # 仅在运行时被 Qt 内部引用到的 Qt 模块
    + collect_submodules("PySide6.QtSvg")
)

EXCLUDES = [
    "PySide6.QtWebEngineCore", "PySide6.QtWebEngineWidgets",
    "PySide6.QtQml", "PySide6.QtQuick", "PySide6.QtQuick3D",
    "PySide6.Qt3DCore", "PySide6.Qt3DRender",
    "PySide6.QtMultimedia", "PySide6.QtMultimediaWidgets",
    "PySide6.QtCharts", "PySide6.QtDataVisualization",
    "PySide6.QtPdf", "PySide6.QtPdfWidgets",
    "PySide6.QtBluetooth", "PySide6.QtNfc", "PySide6.QtPositioning",
    "PySide6.QtWebSockets", "PySide6.QtWebChannel",
    "tkinter", "pydoc_data",
]

a = Analysis(
    ["app.py"],
    pathex=[],
    binaries=[],
    datas=[("assets/pods.png", "assets")],
    hiddenimports=hiddenimports,
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=EXCLUDES,
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name="VivoPodsCC",
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_traceback=False,
    icon=None,
)
