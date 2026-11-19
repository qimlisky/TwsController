namespace PodsController.Core;

/// <summary>
/// 单实例守卫（对应 Python 版 keepalive.acquire_single_instance /
/// install_instance_activator）。
///
/// 命名互斥体判断「是否已有实例」，命名事件把「唤出面板」的请求交给已有实例。
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\VivoPodsCC.SingleInstance";
    private const string EventName = @"Local\VivoPodsCC.Activate";

    private static Mutex? _mutex;
    private static EventWaitHandle? _event;
    private static Thread? _listener;
    private static volatile bool _stopping;

    /// <summary>尝试成为唯一实例；false 表示已有实例在跑。</summary>
    public static bool Acquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            _event = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            return createdNew;
        }
        catch
        {
            // 创建失败时不阻止启动（宁可多开，也不要打不开）
            return true;
        }
    }

    /// <summary>第二个实例：请求已有实例把面板唤出来。</summary>
    public static void SignalExisting()
    {
        try { _event?.Set(); } catch { /* 忽略 */ }
    }

    /// <summary>已有实例在后台等激活信号。</summary>
    public static void Listen(Action onActivate)
    {
        _stopping = false;
        _listener = new Thread(() =>
        {
            while (!_stopping)
            {
                try
                {
                    if (_event is not null && _event.WaitOne(1000))
                    {
                        onActivate();
                    }
                }
                catch
                {
                    Thread.Sleep(300);
                }
            }
        })
        {
            IsBackground = true,
            Name = "single-instance-listener"
        };
        _listener.Start();
    }

    public static void Release()
    {
        _stopping = true;
        try { _mutex?.ReleaseMutex(); } catch { /* 忽略 */ }
        try { _mutex?.Dispose(); } catch { /* 忽略 */ }
        try { _event?.Dispose(); } catch { /* 忽略 */ }
        _mutex = null;
        _event = null;
    }
}
