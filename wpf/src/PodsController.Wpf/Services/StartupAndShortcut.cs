using System.IO;

using Microsoft.Win32;

using PodsController.Core;

namespace PodsController.Services;

/// <summary>
/// 开机自启动（对应 Python 版托盘里的「开机自启动」）。
/// 写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run，不需要管理员。
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VivoPodsCC";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                           ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return false;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Runtime($"开机自启动设置失败：{ex.Message}");
            return false;
        }
    }
}

/// <summary>
/// 桌面快捷方式（对应 Python 版 ui/shortcut.py）。
///
/// 通过 WScript.Shell COM 创建 .lnk；用户手动删掉后不再反复生成，
/// 换了目录则重新指向新位置（配置里记 shortcut_target）。
/// </summary>
public static class DesktopShortcut
{
    private const string LinkName = "TwsController.lnk";

    public static string? ExePath()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return null;
        // 从源码运行时路径是 dotnet.exe，此时不建快捷方式
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return null;
        return exe;
    }

    public static string LnkPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), LinkName);

    public static void Ensure(SettingsStore settings)
    {
        try
        {
            var exe = ExePath();
            if (exe is null) return;

            var link = LnkPath();
            var previous = settings.Get("shortcut_target");
            if (previous.Length > 0 &&
                string.Equals(previous, exe, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(link))
            {
                return;
            }

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = exe;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
            shortcut.IconLocation = exe + ",0";
            shortcut.Description = AppMeta.AppName;
            shortcut.Save();

            settings.Set("shortcut_target", exe);
            AppLog.Runtime($"桌面快捷方式就绪：{link}");
        }
        catch (Exception ex)
        {
            AppLog.Runtime($"桌面快捷方式创建失败：{ex}");
        }
    }
}
