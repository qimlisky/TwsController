# VivoPods Controller · WPF 版

工程名 / 程序集名：`PodsController.Wpf`（解决方案 `PodsController.Wpf.sln`）。
运行时标识仍沿用 Python 版：配置目录 `%APPDATA%\VivoPodsCC`、日志 `%LOCALAPPDATA%\VivoPodsCC`、
单实例名 `Local\VivoPodsCC.*`、快捷方式「VivoPods Controller」，两边可共用同一份配置。

按你的要求，界面不再用 WinUI 3，改成 **WPF**，并把界面做成和 Python 版（PySide6 浮窗）
一样的结构，另加**深色 / 浅色主题切换**。

设备层（华为 SPP / OPPO 私有协议 / vivo 型号识别 / 蓝牙枚举 / 托盘 / 热键 / 控制中心跟随）
直接复用之前移植好的 C# 代码，只把 UI 从 WinUI 换成 WPF。

> 只产出源码：本目录**未编译、未运行**过。

---

## 工程形态

| 项目 | 设置 | 说明 |
| --- | --- | --- |
| 目标框架 | `net10.0-windows` | 需要 .NET 10 SDK（含 Windows Desktop） |
| UI 框架 | **WPF**（`UseWPF=true`） | 不依赖 Windows App SDK / WinUI 运行时 |
| 打包 | 非打包（普通 exe） | 双击 `PodsController.Wpf.exe` 直接跑 |
| .NET 运行时 | `SelfContained=false` | 目标机装 .NET 10 Desktop Runtime 即可 |
| 平台 | x64 / ARM64 | 不支持 AnyCPU |

---

## 界面（对着 Python 版做）

```
┌────────────────────────────────────────────┐
│ [耳机图] 设备名 / 连接状态      ⌄  📌  ⚙  ✕ │  ← 自绘标题栏，拖动整窗
├────────────────────────────────────────────┤
│  电量三卡：左耳 / 右耳 / 充电盒（百分比+进度条+充电⚡）│
│  佩戴状态卡片                                │
│  降噪：[降噪] [关闭] [通透]（三档实心选中）    │
│  空间音频  [实验]                        ◉─  │
│  低延迟游戏模式 [实验]                   ◉─  │
│  音效：默认 / 超重低音 / 清亮高音 / 清澈人声   │
│  让耳机响铃（查找耳机）                       │
│  多设备连接：每行 名称/状态 + [断开][连接]     │
│  固件版本                                    │
└────────────────────────────────────────────┘
```

* **折叠态**（标题栏 ⌄）：只留「左/右/盒 电量 + 降噪三档 + 点击 ⌃ 展开」。
* **无系统标题栏**：`WindowStyle=None` + 圆角 + 阴影，拖动走 `DragMove()`。
* **右下角贴边**：按当前显示器工作区（物理像素 → DIP 换算）。
* **点面板外面自动收起**（固定后不收起）；**自动弹出后 5 秒无操作自动收回**。
* **齿轮 ⚙ 菜单**：重新连接 / 选择设备（已配对的支持型号）/ 演示模式（vivo·华为·OPPO）/
  自动识别 / 识别后自动弹窗 / 自动收回 / **深色模式** / 跟随系统控制中心 / 关于 / 退出。
* **关于页**：耳机图 + 软件版本 V11.45.85 + 作者，返回控制页。
* 分区按能力矩阵显隐（无 ANC 的 FreeClip 2 不显示降噪；华为不显示查找/空间音频等）。

---

## 深色 / 浅色切换

两套配色各一本资源字典，控件一律 `DynamicResource`，切换只替换
`Application.Resources.MergedDictionaries[0]`，**不重建窗口、不重启程序**：

```
UI/Themes/Light.xaml   ← 默认
UI/Themes/Dark.xaml
```

| 入口 | 行为 |
| --- | --- |
| 浮窗 ⚙ 菜单 → 深色模式 | 勾选即时切换 |
| 托盘右键 → 深色模式 | 同一份设置，双向同步 |
| 配置文件 `dark = "true"` | 下次启动直接是深色 |

配色键：`PanelBackgroundBrush` / `PanelBorderBrush` / `CardBackgroundBrush` /
`TitleTextBrush` / `SubTextBrush` / `AccentBrush` / `ControlBackgroundBrush` /
`SwitchOffBrush` 等，浅色深色一一对应，想换配色只改这两个文件。

窗口尺寸 396×582（折叠 212），外圈留 8px 给圆角阴影，实际内容宽度仍是 380。

---

## 目录

```
wpf/
├─ PodsController.Wpf.sln
├─ assets/                       # 图标与耳机实物图（构建时复制到 exe 同级 assets/）
└─ src/PodsController.Wpf/
   ├─ PodsController.Wpf.csproj
   ├─ app.manifest               # PerMonitorV2 DPI
   ├─ App.xaml / App.xaml.cs     # 全局样式 + 单实例 → AppHost.Start()
   ├─ AppHost.cs                 # app.py 的 VivoPodsApp
   ├─ Core/                      # 元信息 / 日志 / 设置 / 型号能力 / 蓝牙枚举 / 自动识别
   ├─ Devices/                   # 华为会话、OPPO 会话、演示会话
   ├─ Interop/                   # P/Invoke、RFCOMM、隐藏消息窗口
   ├─ Services/                  # 托盘、热键、控制中心跟随、开机自启与快捷方式
   └─ UI/
      ├─ FlyoutWindow.xaml / .xaml.cs
      ├─ ThemeManager.cs
      └─ Themes/{Light,Dark}.xaml
```

配置与日志路径和 Python 版完全一致，老设置不丢：

* 配置 `%APPDATA%\VivoPodsCC\config.ini`（键名不变：`dark` / `auto_detect` /
  `last_device_mac` / `follow_win_a` / `pin` …）
* 日志 `%LOCALAPPDATA%\VivoPodsCC\{runtime,crash,taskbar,ccwatch}.log`

---

## 编译 / 运行

```powershell
dotnet build .\wpf\PodsController.Wpf.sln -c Release -p:Platform=x64

.\wpf\src\PodsController.Wpf\bin\x64\Release\net10.0-windows\PodsController.Wpf.exe
```

演示模式（不用真机看界面）：

```powershell
.\PodsController.Wpf.exe --demo            # vivo
.\PodsController.Wpf.exe --demo=huawei     # 华为 FreeBuds Pro 5
.\PodsController.Wpf.exe --demo=oppo       # OPPO Enco X4
```

`Platform` 必须显式给 `x64` 或 `ARM64`。

---

## 说明 / 边界

1. **vivo 真机控制仍未移植**：仓库里没有 `vivo/`、`pods/`、`ui/` 三个 Python 包
   （`app.py` 里 `from ui.flyout import FlyoutWindow` 等导入都指向不存在的目录），
   所以 vivo 只做型号识别 + 能力矩阵 + 演示模式；真机连接会提示协议未移植。
   界面是按 `README.md` 里描述的 Python 版行为重建的，不是从 Qt 源码一比一抠出来的。
2. **未编译验证**：本机没有 .NET SDK，按你的要求也没下载安装，首次编译若报错按提示改。
3. **托盘已按 Python 版补齐**：深浅色切换时托盘图标跟着换（`assets/app_dark.ico` 亮色图 / `app_light.ico` 灰色图，缺失回退 `app.ico`）；右键菜单含「显示面板（设备名）」「自动识别」「选择设备（枚举已配对的支持型号）」「三品牌演示」「深色模式」「开机自启动」「跟随 Win+A」「跟随系统控制中心」「关闭按钮：收起到托盘 / 退出程序」「退出」；左键单击唤出面板，睡眠唤醒后重挂图标，tooltip 跟着电量走。
4. 之前 `winui3/` 工程保留未动（里面的 `OppoProtocol` 常量/方法重名 CS0102 已一并改名修掉），
   可以随时删掉；新的入口是 `wpf/PodsController.Wpf.sln`。
