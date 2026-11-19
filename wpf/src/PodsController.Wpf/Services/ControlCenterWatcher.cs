using System.Text;

using PodsController.Core;
using PodsController.Interop;

namespace PodsController.Services;

/// <summary>
/// 跟随系统控制中心（对应 Python 版 ui/ccwatch.py 的 ControlCenterWatcher）。
///
/// Windows 没有公开的「控制中心打开 / 关闭」通知，只能轮询前台窗口：
///   * 前台窗口类名含 ControlCenter；
///   * 或由 ShellExperienceHost.exe 承载的空标题 CoreWindow（Win11 控制中心）。
/// 打开 → 浮窗叠加；关闭 → 同步收起。
/// </summary>
public sealed class ControlCenterWatcher
{
    private Thread? _thread;
    private volatile bool _stop;
    private bool _isOpen;

    public event Action? Opened;
    public event Action? Closed;

    public bool LogEnabled { get; set; } = true;

    public bool IsOpen => _isOpen;

    public void Start()
    {
        if (_thread is not null) return;
        _stop = false;
        _thread = new Thread(Loop) { IsBackground = true, Name = "control-center-watch" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        try { _thread?.Join(1000); } catch { /* 忽略 */ }
        _thread = null;
        if (_isOpen)
        {
            _isOpen = false;
            Closed?.Invoke();
        }
    }

    private void Loop()
    {
        while (!_stop)
        {
            try
            {
                var open = Detect();
                if (open != _isOpen)
                {
                    _isOpen = open;
                    Log($"控制中心{(open ? "打开" : "关闭")}");
                    if (open) Opened?.Invoke();
                    else Closed?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Log($"检测异常：{ex.Message}");
            }

            for (var i = 0; i < 5 && !_stop; i++) Thread.Sleep(100);
        }
    }

    private static bool Detect()
    {
        var foreground = Native.GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;

        var className = new StringBuilder(256);
        Native.GetClassName(foreground, className, className.Capacity);
        var cls = className.ToString();

        if (cls.Contains("ControlCenter", StringComparison.OrdinalIgnoreCase)) return true;

        if (string.Equals(cls, "Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase))
        {
            Native.GetWindowThreadProcessId(foreground, out var pid);
            try
            {
                var process = System.Diagnostics.Process.GetProcessById((int)pid);
                return string.Equals(process.ProcessName, "ShellExperienceHost", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private void Log(string message)
    {
        if (LogEnabled)
        {
            AppLog.ControlCenter(message);
        }
    }
}
