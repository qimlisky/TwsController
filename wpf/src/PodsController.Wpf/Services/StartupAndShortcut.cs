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
    private const string ValueName = "TwsController";
    private const string LegacyValueName = "VivoPodsCC";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var current = key?.GetValue(ValueName) as string;
            var legacy = key?.GetValue(LegacyValueName) as string;
            var command = !string.IsNullOrWhiteSpace(current) ? current : legacy;
            if (string.IsNullOrWhiteSpace(command)) return false;

            var exe = CurrentExePath();
            // 从 dotnet 运行时无法知道发布后的 exe 路径，只按注册值是否存在判断。
            if (exe is null) return true;
            return CommandTargets(command, exe);
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

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
                return true;
            }

            var exe = CurrentExePath();
            if (exe is null)
            {
                AppLog.Runtime("开机自启动：当前不是发布版 exe，跳过注册");
                return false;
            }

            key.SetValue(ValueName, $"\"{exe}\" --startup");
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Runtime($"开机自启动设置失败：{ex.Message}");
            return false;
        }
    }

    private static string? CurrentExePath()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return null;
        // 源码调试时进程是 dotnet.exe，注册它没有意义。
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return exe;
    }

    private static bool CommandTargets(string command, string exe)
    {
        try
        {
            var trimmed = command.Trim();
            string path;
            if (trimmed.StartsWith('"'))
            {
                var end = trimmed.IndexOf('"', 1);
                if (end <= 1) return false;
                path = trimmed[1..end];
            }
            else
            {
                var gap = trimmed.IndexOf(' ');
                path = gap < 0 ? trimmed : trimmed[..gap];
            }

            return string.Equals(
                Path.GetFullPath(path.Trim()),
                Path.GetFullPath(exe),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
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
