using System.IO;
using System.Text;
using System.Threading;

namespace PodsController.Core;

/// <summary>
/// 配置存储（对应 Python 版 QSettings(IniFormat, UserScope, "VivoPodsCC", "config")）。
///
/// 文件位置与键名必须和 Python 版完全一致，用户升级后设置不会丢：
///   %APPDATA%\VivoPodsCC\config.ini
///
/// 写盘策略：内存里立即生效，落盘做 700ms 合并（连续拖动 / 电量刷新只写一次），
/// 退出时 Flush，避免频繁 IO。
/// </summary>
public sealed class SettingsStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;

    private Timer? _flushTimer;
    private bool _dirty;

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

    public bool Contains(string key)
    {
        lock (_gate)
        {
            return _values.ContainsKey(key);
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
            if (_values.TryGetValue(key, out var old) && string.Equals(old, text, StringComparison.Ordinal))
            {
                return;
            }

            _values[key] = text;
            _dirty = true;

            // 合并短时间内的多次修改，700ms 后统一落盘一次。
            if (_flushTimer is null)
            {
                _flushTimer = new Timer(_ => Flush(), null, 700, Timeout.Infinite);
            }
            else
            {
                _flushTimer.Change(700, Timeout.Infinite);
            }
        }
    }

    /// <summary>把内存中的脏数据立即写盘（退出前调用）。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
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

            var sb = new StringBuilder(256);
            sb.Append("[General]").Append('\n');
            foreach (var (key, value) in _values)
            {
                sb.Append(key).Append('=').Append(value).Append('\n');
            }

            File.WriteAllText(_path, sb.ToString());
        }
        catch (Exception ex)
        {
            AppLog.Runtime($"写入配置失败：{ex.Message}");
        }
    }
}
