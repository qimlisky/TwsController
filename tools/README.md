# tools/ —— 诊断工具

这里放与主程序解耦的小工具，用于排查真机上难以复现的问题。**它们不参与
PyInstaller 打包**（`VivoPodsCC.spec` 只收集 `ui` / `vivo` 两个包），可以单独拷出去用。

---

## cc_probe.py —— Windows 11 控制中心窗口探测器

### 它是干什么的

Windows 没有公开 API 让第三方程序订阅「控制中心（快速设置）已打开 / 已关闭」。
本项目的浮窗跟随功能（`ui/ccwatch.py`）用「枚举顶层窗口 + 类名/标题匹配」近似检测，
但不同 Windows 版本上控制中心的**窗口类名/标题并不统一**——这正是「打开控制中心时
浮窗不跟随弹出」的常见原因。

`cc_probe.py` 帮你把那一刻屏幕上**到底多出了哪个窗口**看清楚：它的 `hwnd`、
窗口类名、标题、尺寸、以及它属于哪个进程。把这几个 ↑NEW 行发回来，就能确定你的
系统上控制中心窗口的特征，从而修正匹配规则。

### 前置条件

- **Windows 10/11**（其它平台会提示并退出）。
- **Python 3.8+**，**无需安装任何第三方依赖**（只用标准库 `ctypes`）。
- 不需要管理员权限。

### 精确运行命令

在本工程根目录（`VivoPodsCC\`，即含 `app.py` 的那一层）打开 CMD / PowerShell：

```
python tools\cc_probe.py
```

可选参数：

```
python tools\cc_probe.py --once          # 只打印一次当前窗口快照后退出
python tools\cc_probe.py --all           # 连被过滤掉的“噪音”窗口也显示
python tools\cc_probe.py --interval 0.15 # 加快轮询（默认 0.3 秒一次）
```

### 怎么用（关键步骤）

1. 运行上面的命令，让它稳定打印几秒（此时**先别动**，控制中心应是关闭的）。
2. 按 **Win + A** 打开控制中心。
3. 观察输出里**第一次出现的 `↑NEW`** 行——那就是控制中心弹出来时新建的窗口。
   重点看它的 `class="..."`、`title="..."`、`proc="..."`。
4. 把这几行 `↑NEW` 复制回来。

输出示例（示意）：

```
=== 实时模式 ===
14:02:11  ↑NEW  [new] hwnd=0x000A1B2C  class="ControlCenterWindow"  title=""  1080x720  proc="explorer.exe(pid 1234)"
14:02:11  ↑NEW  [CC?] hwnd=0x000A1B30  class="Windows.UI.Core.CoreWindow"  title="快速设置"  400x600  proc="ShellExperienceHost.exe(pid 5678)"
14:02:21  ·心跳· 可见顶层窗口 37 个，疑似控制中心 2 个
```

即使什么都没变化，脚本也会每 10 次轮询打一行 `·心跳·`，用来确认脚本仍在运行。

---

## 与主程序日志的关系

主程序的跟随检测（`ui/ccwatch.py`）会把**每次命中的窗口信息**、**兜底候选**、
**未命中时的心跳** 都写进日志，路径固定为：

```
%LOCALAPPDATA%\VivoPodsCC\ccwatch.log
```

在「设置菜单 → 跟随系统控制中心」开启时（默认开），可用 `cc_probe.py` 先确定
特征，再对照 `ccwatch.log` 里的命中/心跳记录，判断是「没匹配到」还是「没启动」。
