using PodsController.Devices.Huawei;
using PodsController.Devices.Oppo;

namespace PodsController.Core;

/// <summary>
/// 全品牌型号识别（对应 Python 版 pods/detect.py + vivo/vivo_models.py）。
///
/// 自动识别 / 手动列表只认「严格支持」的型号：手机、手表、笔记本一律挡在外面。
/// vivo 的三个数据表（型号 41 个、能力位）来自 docs/VIVO_SUPPORT.md，
/// 与 Python 版 vivo/vivo_models.py 同源。
/// </summary>
public static class DeviceCatalog
{
    // ------------------------------------------------------------------ //
    // vivo / iQOO
    // ------------------------------------------------------------------ //
    public static readonly string[] VivoModels =
    {
        "iQOO TWS 1", "iQOO TWS 1e", "iQOO TWS 1i", "iQOO TWS 2", "iQOO TWS 5",
        "iQOO TWS 5e", "iQOO TWS 5i", "iQOO TWS Air", "iQOO TWS Air Pro",
        "iQOO TWS Air2", "iQOO TWS Air3", "iQOO TWS Air3 Pro", "iQOO 耳机",
        "vivo TWS 1", "vivo TWS 2", "vivo TWS 2e", "vivo TWS 3", "vivo TWS 3 Pro",
        "vivo TWS 3e", "vivo TWS 3i", "vivo TWS 4", "vivo TWS 4 Hi-Fi",
        "vivo TWS 5", "vivo TWS 5 Hi-Fi", "vivo TWS 5 Pro", "vivo TWS 5e",
        "vivo TWS 5i", "vivo TWS A1", "vivo TWS A1 Pro", "vivo TWS A2",
        "vivo TWS A3", "vivo TWS A4", "vivo TWS A5", "vivo TWS Air",
        "vivo TWS Air Pro", "vivo TWS Air2", "vivo TWS Air3", "vivo TWS Air3 Pro",
        "vivo TWS Neo", "vivo TWS X1", "vivo 耳机",
    };

    private static readonly string[] VivoFamilyKeywords =
    {
        "vivo tws", "iqoo tws", "vivo 耳机", "iqoo 耳机", "vivo headphones", "iqoo headphones",
    };

    private static readonly HashSet<string> VivoNoiseOff = Set(
        "iQOO TWS 1i", "iQOO TWS 5i", "iQOO TWS Air", "iQOO TWS Air2", "iQOO TWS Air3",
        "vivo TWS 1", "vivo TWS 2e", "vivo TWS 3i", "vivo TWS 5i", "vivo TWS A1",
        "vivo TWS A2", "vivo TWS A3", "vivo TWS A4", "vivo TWS A5", "vivo TWS Air",
        "vivo TWS Air2", "vivo TWS Air3", "vivo TWS Neo");

    private static readonly HashSet<string> VivoSpatialOff = Set(
        "iQOO TWS 1", "iQOO TWS 1e", "iQOO TWS 1i", "iQOO TWS 2", "iQOO TWS 5i",
        "iQOO TWS Air", "iQOO TWS Air Pro", "iQOO TWS Air2", "iQOO TWS Air3",
        "iQOO TWS Air3 Pro", "vivo TWS 1", "vivo TWS 2", "vivo TWS 2e", "vivo TWS 3",
        "vivo TWS 3e", "vivo TWS 3i", "vivo TWS 5i", "vivo TWS A1", "vivo TWS A1 Pro",
        "vivo TWS A2", "vivo TWS A3", "vivo TWS A4", "vivo TWS A5", "vivo TWS Air",
        "vivo TWS Air Pro", "vivo TWS Air2", "vivo TWS Air3", "vivo TWS Air3 Pro",
        "vivo TWS Neo", "vivo TWS X1");

    private static readonly HashSet<string> VivoGameOff = Set(
        "iQOO TWS 1", "iQOO TWS 1e", "iQOO TWS 1i", "iQOO TWS Air", "iQOO TWS Air Pro",
        "iQOO TWS Air2", "vivo TWS 1", "vivo TWS 2", "vivo TWS 2e", "vivo TWS 3",
        "vivo TWS 3 Pro", "vivo TWS 3e", "vivo TWS 3i", "vivo TWS A1", "vivo TWS A1 Pro",
        "vivo TWS A2", "vivo TWS A3", "vivo TWS Air", "vivo TWS Air Pro", "vivo TWS Air2",
        "vivo TWS Neo", "vivo TWS X1");

    private static readonly HashSet<string> VivoDualOff = Set(
        "iQOO TWS 1", "iQOO TWS 1e", "iQOO TWS 1i", "iQOO TWS 2", "iQOO TWS Air",
        "iQOO TWS Air Pro", "iQOO TWS Air2", "iQOO TWS Air3", "iQOO TWS Air3 Pro",
        "vivo TWS 1", "vivo TWS 2", "vivo TWS 2e", "vivo TWS 3", "vivo TWS 3 Pro",
        "vivo TWS 3i", "vivo TWS 4", "vivo TWS 4 Hi-Fi", "vivo TWS A1", "vivo TWS A1 Pro",
        "vivo TWS A2", "vivo TWS A3", "vivo TWS A4", "vivo TWS Air", "vivo TWS Air Pro",
        "vivo TWS Air2", "vivo TWS Air3", "vivo TWS Air3 Pro", "vivo TWS Neo", "vivo TWS X1");

    /// <summary>vivo 音效 5 档（官方 App 的预设名）。</summary>
    public static readonly EqOption[] VivoEqOptions =
    {
        new(0, "默认"),
        new(1, "清澈人声"),
        new(2, "清亮高音"),
        new(3, "超重低音"),
        new(4, "增强低音"),
    };

    // ------------------------------------------------------------------ //
    // 公共入口
    // ------------------------------------------------------------------ //
    /// <summary>是否是本程序能识别（能接管）的耳机；同时给出品牌与规范型号名。</summary>
    public static bool TryDetect(string? name, out Vendor vendor, out string canonical)
    {
        vendor = Vendor.Unknown;
        canonical = "";

        if (string.IsNullOrWhiteSpace(name)) return false;

        if (VivoFindModel(name) is { } vivo)
        {
            vendor = Vendor.Vivo;
            canonical = vivo;
            return true;
        }

        if (HuaweiCatalog.FindModel(name) is { } huawei)
        {
            vendor = Vendor.Huawei;
            canonical = huawei;
            return true;
        }

        if (OppoCatalog.FindModel(name) is { } oppo)
        {
            vendor = Vendor.Oppo;
            canonical = oppo;
            return true;
        }

        return false;
    }

    /// <summary>严格判断（自动弹窗 / 自动连接只认这个）。</summary>
    public static bool IsSupported(string? name) => TryDetect(name, out _, out _);

    /// <summary>标题栏 / 日志里显示的规范名；认不出时退回原名。</summary>
    public static string DisplayName(string? name, Vendor vendor = Vendor.Unknown)
    {
        if (string.IsNullOrWhiteSpace(name)) return "未知设备";

        if (vendor == Vendor.Huawei || (vendor == Vendor.Unknown && HuaweiCatalog.IsFamilyName(name)))
        {
            return HuaweiCatalog.DisplayName(name);
        }
        if (vendor == Vendor.Oppo || (vendor == Vendor.Unknown && OppoCatalog.IsFamilyName(name)))
        {
            return OppoCatalog.DisplayName(name);
        }
        if (VivoFindModel(name) is { } vivo) return vivo;
        if (IsVivoFamily(name)) return "vivo / iQOO 耳机（家族兼容）";
        return name;
    }

    /// <summary>按型号解析能力集合。</summary>
    public static FeatureSet FeaturesFor(string? name, Vendor vendor)
    {
        return vendor switch
        {
            Vendor.Vivo => VivoFeatures(VivoFindModel(name)),
            Vendor.Huawei => HuaweiCatalog.Features(HuaweiCatalog.FindModel(name)),
            Vendor.Oppo => OppoCatalog.BaseFeatures(),
            _ => FeatureSet.Empty,
        };
    }

    // ------------------------------------------------------------------ //
    // vivo 细则
    // ------------------------------------------------------------------ //
    public static string? VivoFindModel(string? name)
    {
        var n = Normalize(name);
        if (n.Length == 0) return null;

        foreach (var model in VivoModels)
        {
            if (Normalize(model) == n) return model;
        }

        // 允许耳机只上报短名（去掉 vivo / iqoo 前缀）
        var bare = StripVendor(n);
        foreach (var model in VivoModels)
        {
            if (StripVendor(Normalize(model)) == bare) return model;
        }

        return null;
    }

    public static bool IsVivoFamily(string? name)
    {
        var n = Normalize(name);
        if (n.Length == 0) return false;
        return VivoFamilyKeywords.Any(k => n.StartsWith(Normalize(k)));
    }

    private static FeatureSet VivoFeatures(string? model)
    {
        if (model is null) return FeatureSet.Empty;

        var caps = new List<string> { Feature.Eq, Feature.Find };
        var key = Normalize(model);
        if (!VivoNoiseOff.Contains(key)) caps.Add(Feature.Noise);
        if (!VivoSpatialOff.Contains(key)) caps.Add(Feature.Spatial);
        if (!VivoGameOff.Contains(key)) caps.Add(Feature.Game);
        if (!VivoDualOff.Contains(key)) caps.Add(Feature.Dual);
        if (model == "vivo TWS 1") caps.Remove(Feature.Find);
        if (model == "vivo TWS 3e")
        {
            caps.Add(Feature.Wear);
            caps.Add(Feature.Dual); // 双连硬件常开，无法开关
        }

        var forced = model == "vivo TWS 3e" ? new[] { Feature.Dual } : Array.Empty<string>();
        var experimental = new[] { Feature.Spatial, Feature.Game };
        return new FeatureSet(caps, forced, experimental);
    }

    // ------------------------------------------------------------------ //
    private static HashSet<string> Set(params string[] items) =>
        new(items.Select(Normalize), StringComparer.Ordinal);

    private static string Normalize(string? value) =>
        new string((value ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string StripVendor(string normalized)
    {
        foreach (var prefix in new[] { "vivo", "iqoo" })
        {
            if (normalized.StartsWith(prefix)) return normalized[prefix.Length..];
        }
        return normalized;
    }
}
