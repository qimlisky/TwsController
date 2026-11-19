namespace PodsController.Core;

/// <summary>品牌（对应 Python 版的 VIVO / HUAWEI / OPPO 常量）。</summary>
public enum Vendor
{
    Unknown = 0,
    Vivo = 1,
    Huawei = 2,
    Oppo = 3,
    Redmi = 4
}

public static class VendorNames
{
    public static string ToKey(Vendor v) => v switch
    {
        Vendor.Vivo => "vivo",
        Vendor.Huawei => "huawei",
        Vendor.Oppo => "oppo",
        Vendor.Redmi => "redmi",
        _ => ""
    };

    public static Vendor FromKey(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
    {
        "vivo" or "iqoo" => Vendor.Vivo,
        "huawei" or "honor" or "hw" => Vendor.Huawei,
        "oppo" or "oneplus" or "realme" => Vendor.Oppo,
        "redmi" or "xiaomi" or "mi" => Vendor.Redmi,
        _ => Vendor.Unknown
    };

    public static string Label(Vendor v) => v switch
    {
        Vendor.Vivo => "vivo / iQOO",
        Vendor.Huawei => "华为 / HONOR",
        Vendor.Oppo => "OPPO / 一加 / realme",
        Vendor.Redmi => "Redmi / 小米",
        _ => "未知品牌"
    };
}

/// <summary>一只耳机（对应 Python 版 DeviceInfo）。</summary>
public sealed record DeviceInfo(string Name, string Mac, Vendor Vendor, bool IsDemo = false)
{
    /// <summary>无分隔、小写 MAC（上游协议要的格式）。</summary>
    public string PlainMac => new string((Mac ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>AA:BB:CC:DD:EE:FF。</summary>
    public string PrettyMac
    {
        get
        {
            var plain = PlainMac;
            if (plain.Length != 12) return Mac ?? "";
            return string.Join(":", Enumerable.Range(0, 6).Select(i => plain.Substring(i * 2, 2).ToUpperInvariant()));
        }
    }
}

/// <summary>UI 音效选项（序号 + 中文名）。</summary>
public sealed record EqOption(int Code, string Name);

/// <summary>多设备（双连）列表的一行。</summary>
public class MultiPointRow
{
    public string Name { get; init; } = "";
    public string Mac { get; init; } = "";
    /// <summary>0 未连接 / 1 已连接未出声 / 2 已连接正在出声。</summary>
    public int Status { get; init; }
    public bool Active => Status == 2;
    public bool Connected => Status > 0;

    public string StatusText => Status switch
    {
        2 => "正在使用",
        1 => "已连接",
        _ => "未连接"
    };
}

/// <summary>能力位名称，与 Python 版字符串逐字对齐。</summary>
public static class Feature
{
    public const string Noise = "noise";
    public const string Find = "find";
    public const string Spatial = "spatial";
    public const string Dual = "dual";
    public const string Game = "game";
    public const string Wear = "wear";
    public const string Eq = "eq";
}

/// <summary>型号能力集合（对应 FeatureSet.has / visible / is_experimental）。</summary>
public sealed class FeatureSet
{
    private readonly HashSet<string> _supported;
    private readonly HashSet<string> _forced;
    private readonly HashSet<string> _experimental;

    public FeatureSet(IEnumerable<string>? supported = null,
                      IEnumerable<string>? forced = null,
                      IEnumerable<string>? experimental = null)
    {
        _supported = new HashSet<string>(supported ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _forced = new HashSet<string>(forced ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        _experimental = new HashSet<string>(experimental ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    public static FeatureSet Empty { get; } = new();

    public IReadOnlyCollection<string> Supported => _supported;
    public IReadOnlyCollection<string> Forced => _forced;

    public bool Has(string feature) => _supported.Contains(feature);

    /// <summary>有能力且非强制常开，才显示可操作控件。</summary>
    public bool Visible(string feature) => _supported.Contains(feature) && !_forced.Contains(feature);

    public bool IsExperimental(string feature) => _supported.Contains(feature) && _experimental.Contains(feature);

    public FeatureSet With(params string[] caps)
    {
        var next = new HashSet<string>(_supported, StringComparer.OrdinalIgnoreCase);
        foreach (var cap in caps) next.Add(cap);
        return new FeatureSet(next, _forced, _experimental);
    }

    public override string ToString() => string.Join("/", _supported.OrderBy(x => x));
}

/// <summary>
/// 一只耳机的实时状态快照。
/// 会话层（HuaweiSession / OppoSession / DemoSession）只改这个对象，
/// UI 收到一次 StateChanged 后整体重绘 —— 比逐个信号简单，也不会出现半新半旧。
/// </summary>
public sealed class DeviceState
{
    public bool Connected { get; set; }
    public string Status { get; set; } = "未连接";

    // 电量：-1 表示未知（与 Python 版 UNKNOWN 一致）
    public int LeftPercent { get; set; } = -1;
    public bool LeftCharging { get; set; }
    public int RightPercent { get; set; } = -1;
    public bool RightCharging { get; set; }
    public int CasePercent { get; set; } = -1;
    public bool CaseCharging { get; set; }

    /// <summary>0 降噪 / 1 关闭 / 2 通透；-1 未知。</summary>
    public int NoiseMode { get; set; } = -1;
    public int Spatial { get; set; } = -1;
    public int Game { get; set; } = -1;
    public int EqMode { get; set; } = -1;

    public string WearLeft { get; set; } = "unknown";
    public string WearRight { get; set; } = "unknown";
    public string Firmware { get; set; } = "";

    public List<MultiPointRow> MultiPoint { get; } = new();
    public List<EqOption> EqOptions { get; } = new();
    public FeatureSet Features { get; set; } = FeatureSet.Empty;

    public void CopyFrom(DeviceState other)
    {
        Connected = other.Connected;
        Status = other.Status;
        LeftPercent = other.LeftPercent; LeftCharging = other.LeftCharging;
        RightPercent = other.RightPercent; RightCharging = other.RightCharging;
        CasePercent = other.CasePercent; CaseCharging = other.CaseCharging;
        NoiseMode = other.NoiseMode; Spatial = other.Spatial;
        Game = other.Game; EqMode = other.EqMode;
        WearLeft = other.WearLeft; WearRight = other.WearRight;
        Firmware = other.Firmware;
        Features = other.Features;
        MultiPoint.Clear();
        MultiPoint.AddRange(other.MultiPoint);
        EqOptions.Clear();
        EqOptions.AddRange(other.EqOptions);
    }
}
