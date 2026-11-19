using System.IO;

namespace PodsController.Core;

/// <summary>
/// 应用元信息（对应 appmeta.py）。
/// 版本号 / 作者只在这里写一次，关于页、托盘提示、日志都从这里取。
/// </summary>
public static class AppMeta
{
    /// <summary>对外展示的软件名（Python 版 v3.20 起统一叫这个名字）。</summary>
    public const string AppName = "TwsController";

    /// <summary>对外展示的软件版本（关于页第一行：软件版本 V11.45.85）。</summary>
    public const string AppVersion = "11.45.85";

    /// <summary>关于页第二行。</summary>
    public const string AppAuthor = "芝士火腿肠拌饭";

    public static string VersionText => $"软件版本 V{AppVersion}";

    public static string AuthorText => $"本软件作者：{AppAuthor}";
}

/// <summary>配置 / 日志 / 快捷方式使用的路径（保持与 Python 版一致，用户设置不丢）。</summary>
public static class AppPaths
{
    /// <summary>配置组织名（决定 config.ini 的目录，改名会丢用户设置）。</summary>
    public const string SettingsOrg = "VivoPodsCC";

    /// <summary>配置文件名（QSettings IniFormat：%APPDATA%\VivoPodsCC\config.ini）。</summary>
    public const string SettingsFileName = "config.ini";

    public static string RoamingDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), SettingsOrg);

    public static string LocalDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), SettingsOrg);

    /// <summary>日志目录：放在当前程序目录下的 log\，方便直接查看和打包带走。</summary>
    public static string LogDir => Path.Combine(AppContext.BaseDirectory, "log");

    public static string SettingsFile => Path.Combine(RoamingDir, SettingsFileName);

    public static string RuntimeLog => Path.Combine(LogDir, "runtime.log");

    public static string CrashLog => Path.Combine(LogDir, "crash.log");

    public static string CleanExitFlag => Path.Combine(LocalDir, "clean_exit");

    public static string TaskbarLog => Path.Combine(LogDir, "taskbar.log");

    public static string ControlCenterLog => Path.Combine(LogDir, "ccwatch.log");

    public static string AssetsDir => Path.Combine(AppContext.BaseDirectory, "assets");

    public static string IconPath => Path.Combine(AssetsDir, "app.ico");

    /// <summary>确保程序目录下的 log\ 存在（日志落地在这里）。</summary>
    public static void EnsureLogDir()
    {
        try { Directory.CreateDirectory(LogDir); } catch { /* 忽略：只影响日志 */ }
    }

    /// <summary>确保 %LOCALAPPDATA%\VivoPodsCC 存在（clean_exit 标记用）。</summary>
    public static void EnsureLocalDir()
    {
        try { Directory.CreateDirectory(LocalDir); } catch { /* 忽略 */ }
    }
}
