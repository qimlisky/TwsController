using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace PodsController.Core;

/// <summary>
/// 运行日志（对应 Python 版 keepalive.log_runtime / log_heartbeat）。
///
/// 与 Python 版保持同样的落盘位置：%LOCALAPPDATA%\VivoPodsCC\
///   runtime.log    启动 / 连接 / 唤醒 / 热键等过程
///   crash.log      未处理异常
///   taskbar.log    托盘重挂、自动识别（不受「控制中心日志」开关影响）
///   ccwatch.log    控制中心跟随检测
///
/// 写盘策略：日志先入内存队列，后台每 1 秒合并写一次文件；
/// 崩溃日志立即 Flush；退出时 Flush，避免一行一次 Append 造成频繁 IO。
/// </summary>
public static class AppLog
{
    private sealed record Entry(string Path, string Tag, DateTime Time, string Message);

    private static readonly ConcurrentQueue<Entry> Queue = new();
    private static readonly object FlushGate = new();
    private static readonly object TimerGate = new();
    private static Timer? _timer;
    private static bool _scheduled;
    private const long MaxSize = 512 * 1024;

    /// <summary>控制中心检测日志开关（对应浮窗里的 ccwatch_log）。</summary>
    public static bool ControlCenterLogEnabled { get; set; } = true;

    public static void Runtime(string message) => Enqueue(AppPaths.RuntimeLog, "RUN", message);

    public static void Crash(string message)
    {
        Enqueue(AppPaths.CrashLog, "CRASH", message);
        Flush();
    }

    public static void Taskbar(string message) => Enqueue(AppPaths.TaskbarLog, "TB", message);

    public static void ControlCenter(string message)
    {
        if (!ControlCenterLogEnabled) return;
        Enqueue(AppPaths.ControlCenterLog, "CC", message);
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

    private static void Enqueue(string path, string tag, string message)
    {
        Queue.Enqueue(new Entry(path, tag, DateTime.Now, message));
        lock (TimerGate)
        {
            _timer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            if (!_scheduled)
            {
                _timer.Change(1000, Timeout.Infinite);
                _scheduled = true;
            }
        }
    }

    /// <summary>把队列里的日志合并写盘（定时器 / 崩溃 / 退出时调用）。</summary>
    public static void Flush()
    {
        if (Queue.IsEmpty)
        {
            lock (TimerGate) _scheduled = false;
            return;
        }

        lock (FlushGate)
        {
            try
            {
                AppPaths.EnsureLocalDir();
                var batches = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);

                while (Queue.TryDequeue(out var entry))
                {
                    if (!batches.TryGetValue(entry.Path, out var sb))
                    {
                        sb = new StringBuilder(256);
                        batches[entry.Path] = sb;
                    }
                    sb.Append(entry.Time.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                      .Append(" [").Append(entry.Tag).Append("] ")
                      .Append(entry.Message).Append(Environment.NewLine);
                }

                foreach (var (path, sb) in batches)
                {
                    Rotate(path);
                    File.AppendAllText(path, sb.ToString());
                }
            }
            catch
            {
                // 日志永远不能把主流程带崩
            }
        }

        lock (TimerGate)
        {
            _scheduled = false;
            if (!Queue.IsEmpty && _timer is not null)
            {
                _timer.Change(1000, Timeout.Infinite);
                _scheduled = true;
            }
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
