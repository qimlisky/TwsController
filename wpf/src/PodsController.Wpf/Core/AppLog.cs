using System.IO;

namespace PodsController.Core;

/// <summary>
/// 运行日志（对应 Python 版 keepalive.log_runtime / log_heartbeat）。
///
/// 与 Python 版保持同样的落盘位置：%LOCALAPPDATA%\VivoPodsCC\
///   runtime.log    启动 / 连接 / 唤醒 / 热键等过程
///   crash.log      未处理异常
///   taskbar.log    托盘重挂、自动识别（不受「控制中心日志」开关影响）
///   ccwatch.log    控制中心跟随检测
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private const long MaxSize = 512 * 1024;

    /// <summary>控制中心检测日志开关（对应浮窗里的 ccwatch_log）。</summary>
    public static bool ControlCenterLogEnabled { get; set; } = true;

    public static void Runtime(string message) => Write(AppPaths.RuntimeLog, "RUN", message);

    public static void Crash(string message) => Write(AppPaths.CrashLog, "CRASH", message);

    public static void Taskbar(string message) => Write(AppPaths.TaskbarLog, "TB", message);

    public static void ControlCenter(string message)
    {
        if (!ControlCenterLogEnabled) return;
        Write(AppPaths.ControlCenterLog, "CC", message);
    }

    /// <summary>每 10 分钟一行心跳（含 RSS），用于区分「崩溃」还是「被外部结束」。</summary>
    public static void Heartbeat()
    {
        try
        {
            long rss = 0;
            using (var p = System.Diagnostics.Process.GetCurrentProcess())
            {
                rss = p.WorkingSet64 / (1024 * 1024);
            }
            Runtime($"heartbeat pid={Environment.ProcessId} rss={rss}MB");
        }
        catch
        {
            // 心跳失败不影响主流程
        }
    }

    private static void Write(string path, string tag, string message)
    {
        try
        {
            AppPaths.EnsureLocalDir();
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{tag}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                Rotate(path);
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // 日志永远不能把主流程带崩
        }
    }

    private static void Rotate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxSize) return;
            var old = path + ".1";
            if (File.Exists(old)) File.Delete(old);
            File.Move(path, old);
        }
        catch
        {
            // 忽略
        }
    }
}
