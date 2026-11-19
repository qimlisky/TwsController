using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PodsController.Core;

/// <summary>
/// 空闲内存回收：浮窗隐藏到托盘后，先做一次紧凑 GC，再让系统修剪工作集。
///
/// 只在窗口不可见时执行，避免用户正在操作时触发 GC / 页面换出造成卡顿。
/// 下次打开浮窗时用到的页面会按需重新调入，代价是首次显示略慢。
/// </summary>
internal static class MemoryTrim
{
    private static int _pending;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

    /// <summary>延迟回收；重复调用只保留最后一次，避免频繁 GC。</summary>
    public static void Schedule(Func<bool>? canTrim = null, int delayMs = 3000)
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                if (canTrim is not null && !canTrim()) return;
                TrimNow();
            }
            catch
            {
                // 回收失败不影响主流程
            }
            finally
            {
                Volatile.Write(ref _pending, 0);
            }
        });
    }

    private static void TrimNow()
    {
        try
        {
            // 两轮：第一轮回收可回收对象，第二轮处理上一轮产生的终结器对象。
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Optimized, blocking: true, compacting: true);
        }
        catch
        {
            // 忽略
        }

        try
        {
            // -1 / -1：让系统把当前进程尽可能多的页面移出工作集。
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }
        catch
        {
            // 忽略
        }
    }
}
