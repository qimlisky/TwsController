using System.IO;

namespace PodsController.Core;

/// <summary>
/// 配置存储（对应 Python 版 QSettings(IniFormat, UserScope, "VivoPodsCC", "config")）。
///
/// 文件位置与键名必须和 Python 版完全一致，用户升级到 WinUI 版后
/// 「上次设备 / 深色模式 / 自动识别」等设置不会丢：
///   %APPDATA%\VivoPodsCC\config.ini
/// </summary>
public sealed class SettingsStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;

    public SettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.SettingsFile;
        Load();
    }

    public string FilePath => _path;

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            foreach (var raw in File.ReadAllLines(_path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#') || line.StartsWith('['))
                {
                    continue;
                }

                var idx = line.IndexOf('=');
                if (idx <= 0) continue;
                var key = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim();
                if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
                {
                    value = value[1..^1].Replace("\\\"", "\"");
                }
                _values[key] = value;
            }
        }
        catch (Exception ex)
        {
            AppLog.Runtime($"读取配置失败：{ex.Message}");
        }
    }

    public string Get(string key, string fallback = "")
    {
        lock (_gate)
        {
            return _values.TryGetValue(key, out var v) ? v : fallback;
        }
    }

    public bool GetBool(string key, bool fallback = false)
    {
        var text = Get(key, fallback ? "true" : "false").Trim().ToLowerInvariant();
        return text switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => fallback
        };
    }

    public int GetInt(string key, int fallback = 0)
    {
        return int.TryParse(Get(key), out var v) ? v : fallback;
    }

    public void Set(string key, object? value)
    {
        var text = Format(value);
        lock (_gate)
        {
            _values[key] = text;
            Save();
        }
    }

    private static string Format(object? value)
    {
        return value switch
        {
            null => "",
            bool b => b ? "true" : "false",
            int or long or double or float => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
            _ => $"\"{(value.ToString() ?? "").Replace("\"", "\\\"")}\""
        };
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var lines = new List<string> { "[General]" };
            foreach (var (key, value) in _values)
            {
                lines.Add($"{key}={value}");
            }
            File.WriteAllLines(_path, lines);
        }
        catch (Exception ex)
        {
            AppLog.Runtime($"写入配置失败：{ex.Message}");
        }
    }
}
