using System.Windows;

using PodsController.Core;

namespace PodsController;

/// <summary>
/// 应用入口（对应 Python 版 app.py 的 main()）。
/// 单实例守卫放在最前面：重复双击只唤出已有面板，不再起第二个托盘。
/// </summary>
public partial class App : Application
{
    private AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!SingleInstance.Acquire())
        {
            // 已有实例：把 show 信号发过去，本进程直接退出
            SingleInstance.SignalExisting();
            Shutdown();
            return;
        }

        _host = new AppHost(e.Args);
        _host.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 用户从托盘/浮窗退出时 AppHost 已经做过清理，这里兜住直接关机的路径
        _host?.Shutdown();
        base.OnExit(e);
    }
}
