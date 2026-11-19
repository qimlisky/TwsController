# TwsController · WPF 版
基于[vivotwscontrollerforwin11](https://github.com/NobleS205/vivotwscontrollerforwin11)的WPF版本  
可前往release下载 
运行环境：请安装[.NET 10](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)[x64](https://dotnet.microsoft.com/zh-cn/download/dotnet/thank-you/runtime-10.0.15-windows-x64-installer)|[x86](https://dotnet.microsoft.com/zh-cn/download/dotnet/thank-you/runtime-10.0.15-windows-x86-installer)|[Arm64](https://dotnet.microsoft.com/zh-cn/download/dotnet/thank-you/runtime-10.0.15-windows-arm64-installer)**运行时**或[开发环境生成应用-SDK](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)  
后台内存大约20mb左右，完全不连接大约10mb以内。  
# VivoPodsCC — vivo / iQOO 耳机 Windows 控制台

一个仿 **Windows 11 控制中心** 风格的 vivo TWS 耳机控制面板：后台常驻系统托盘，
按 `Ctrl + Alt + V` 或点击托盘图标，在屏幕右下角弹出快捷浮窗；也可以设置为
**跟随 `Win + A`**——系统控制中心弹出时，浮窗自动叠加在它的上方。

支持降噪 / 通透 / 关闭切换、空间音频、低延迟游戏模式、EQ、电量（双耳+充电盒）、
查找耳机响铃、佩戴状态、固件版本查看等。

> 本项目仅供学习与个人使用，与 vivo / iQOO 官方无关。耳机控制协议为社区逆向成果，
> 非官方功能，使用风险自负。

---

## 功能一览

| 功能 | 状态 | 说明 |
| --- | --- | --- |
| 双耳 / 充电盒电量、充电状态 | 真机验证命令 | 实时查询 |
| 降噪 / 关闭 / 通透 三档切换 | 真机验证命令 | 不同型号后缀字节已分别适配 |
| EQ 音效（标准 / 清澈人声 / 超重低音 / 清亮高音 / 悠扬听书） | 真机验证命令 | |
| 查找耳机（响铃 / 停止） | 真机验证命令 | 20 秒自动停止 |
| 佩戴状态（双耳 / 单耳 / 在盒） | 真机验证命令 | |
| 固件版本查看 | 真机验证命令 | |
| **空间音频开关**（3D 全景声） | **实验性** | 来自 App 反编译与社区实现，未在所有型号验证 |
| **低延迟游戏模式** | **实验性** | 同上，UI 上有「实验」角标 |
| Win+A 跟随叠加、亚克力、深浅色 | 仅 Windows 生效 | 协议层与 UI 已在无硬件环境测试 |

> 空间音频的剧场 / 音乐厅等模式预设（疑似命令 0x013E）、双击/长按手势自定义
> 仍未实现，规划在后续版本。

## 第二版新增

| 能力 | 说明 |
| --- | --- |
| **打包成 EXE** | 内置 `VivoPodsCC.spec` 与 `build.bat`，双击一键生成 `dist\VivoPodsCC.exe`（单文件、无控制台窗口、目标机无需装 Python） |
| **关闭行为可自定义** | 右上角 ✕ 与 Alt+F4 的行为可选「最小化到托盘（后台常驻）」或「直接退出程序」，托盘菜单与浮窗设置菜单里都能切换 |
| **两态 UI** | 折叠态只显示「耳机电量 + 多设备连接」，点标题栏的 ⌄ / ⌃ 展开或收起；折叠时窗口高度自动收缩到内容高度 |
| **多设备连接（双连）上线** | 折叠态直接列出耳机当前连接的多台设备及在线状态（使用中 / 已连接 / 未连接），数据来自协议 `0x0249 / 0x8249` |
| **跟随 Win11 控制中心** | 打开控制中心时浮窗自动叠加到其正上方；关闭控制中心时浮窗同步收起。可在托盘菜单里开关 |

## 支持型号

按蓝牙名称自动识别并选择协议版本，内置能力矩阵自动隐藏耳机不支持的控件：

- **TWS 5 家族（v4 协议）**：vivo TWS 5、TWS 5 Hi-Fi、iQOO TWS 2 等
- **TWS 4 / 4 Hi-Fi（v4 协议）**：能力按 TWS 4 家族开启，空间音频以真机为准
- **TWS 3e、Air3 Pro（v3 协议）**：无空间音频，双连为强制能力（不显示控件）
- 未识别的 vivo / iQOO 新型号：按家族默认 **v4 协议 + 乐观开启全部能力**，
  可正常使用，但个别命令可能无效

> 请在系统「蓝牙和设备」里查看耳机的**蓝牙名称**。如果你的型号不在列表里，
> 首次连接后把日志（或无效的功能）反馈出来即可补充适配。

## 快速开始（源码运行）

需要 Windows 10 / 11 与 Python 3.9+（64 位）。

```bat
:: 1. 先在 Windows 设置里把耳机配对好（蓝牙开关打开、与手机断开连接）
:: 2. 安装依赖
pip install -r requirements.txt

:: 3. 正常运行（托盘常驻）
python app.py

:: 4. 不连耳机先看界面：演示模式
python app.py --demo
```

也可以双击 `run.bat`（首次自动安装依赖）。

- 启动后若曾连接过耳机，会自动重连；首次启动弹出未连接卡片，点「选择 vivo / iQOO 耳机」。
- 浮窗弹出方式：`Ctrl + Alt + V`、左键点托盘图标、或按 `Win + A`（默认开启跟随）。
- 托盘右键菜单可切换深浅色、开机自启、进入演示模式、选择设备、
  **关闭按钮行为**、**是否跟随系统控制中心**。
- 浮窗标题栏的 ⌄ / ⌃ 可切换**折叠 / 展开**两态：折叠态只显示耳机电量与多设备连接。

## 真机连接要点

1. 耳机必须先与 Windows **完成配对**（设置 → 蓝牙和设备 → 添加设备）。
2. 控制通道是蓝牙经典 **RFCOMM/SPP**，使用 vivo 私有服务 UUID
   `00000837-d102-11e1-9b23-00025b00a5a5`（与安卓 vivo 耳机 App 是同一条通道）。
   程序会自动做 SDP 查询找通道，无需手动填端口。
3. 耳机通常只允许少量设备同时占用控制通道：连接前建议在手机上**关闭/断开耳机 App**
   或暂时关掉手机蓝牙。
4. 若配对后设备列表为空：程序默认用 `BluetoothFindFirstDevice` API 枚举（免管理员）；
   个别精简系统该 API 不可用时会回退读注册表，若提示权限不足，可用管理员身份运行一次。

## 打包成 EXE（一键）

```bat
:: 在项目根目录双击，或命令行执行\.build.bat
```

脚本会自动完成三步：① 安装 PySide6 → ② 安装 / 更新 PyInstaller（≥6.0）
→ ③ 按 `VivoPodsCC.spec` 打包，产物为 **`dist\VivoPodsCC.exe`**
（单文件、64 位、无控制台窗口）。

- 目标机**无需安装 Python**，双击 exe 即可运行；
- 托盘菜单的「开机自启动」在 EXE 模式下写入 exe 自身路径到
  当前用户 `HKCU\...\Run`（不需要管理员）；
- 手动打包等价于：`pyinstaller --noconfirm --clean VivoPodsCC.spec`。

> 注意：请用 **64 位 Python** 打包。`bthprops.cpl` / `ws2_32.dll` 是系统组件、
> 不需要打包，但 exe 必须是 64 位才能正常调用蓝牙接口。

## 工作原理（简版）

- `vivo/vivo_protocol.py`：GAIA 风格私有协议帧的编解码（握手、注册、电量、降噪、
  EQ、空间音频、查找、佩戴、固件、时间同步等），与真机抓包字节逐一对齐。
- `vivo/transport.py`：纯 ctypes 调 `ws2_32.dll` 建立 RFCOMM 连接，零第三方蓝牙库；
  `bthprops.cpl` 枚举已配对设备。
- `vivo/controller.py`：后台线程负责连接、初始化、读循环与命令发送，通过 Qt 信号更新 UI。
- `ui/`：PySide6 无边框圆角亚克力浮窗（`flyout.py`）、Win11 Fluent 风格 QSS、
  全局热键与低级键盘钩子（只观察 `Win+A`、不拦截系统按键）。

## 无硬件自测

协议层与 UI 逻辑不依赖真实耳机和 Windows 蓝牙：

```bat
python tests\test_protocol.py        :: 帧编解码/回包解析/会话流程，34 项
```

## 致谢与许可

vivo tws controller for win11
v11.45.14
本项目作者：芝士火腿肠拌饭

本项目的协议层参考并移植自以下开源项目（在此致谢）：

- **Zhaoyi-ya/TWS-Pods-PC**（Windows + Python，GPL-3.0-only）
- **FxRayHughes/vivopods-mac**（macOS 实现，含 BLE GATT 通道与真机验证）
- **FLuoXue/VivoPodsManager**（C# / Avalonia 实现）
- 协议来源：HyperEars（LSPosed 模块）及枫溪博客《给 vivo 耳机写一个 Mac 控制台：
  VivoPods 与 VivoTWS 协议》

- https://github.com/NobleS205/vivotwscontrollerforwin11  GPL-3.0-only 基于此项目重构

- https://github.com/FallenLeeee/MiBudsController  MIT


完整版权与许可原文见仓库根目录 `THIRD_PARTY_NOTICES.md`。程序「关于」页也标注了该来源。

由于移植了 GPL-3.0 代码，本项目同样以 **GPL-3.0-only** 发布（见 [LICENSE](LICENSE)）。
