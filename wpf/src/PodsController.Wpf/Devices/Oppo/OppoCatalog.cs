using System.Text.RegularExpressions;

using PodsController.Core;

namespace PodsController.Devices.Oppo;

/// <summary>
/// OPPO / 一加 / realme 型号识别与基础能力（对应 oppo/oppo_models.py +
/// oppo/oppo_features.py）。
///
/// 能力不按型号猜：连上后由 0x810C（降噪）/ 0x812A（空间音频）/ 0x810D
/// （功能开关）的实际回包「发现」能力，避免把 Air3 当成有 ANC。
/// </summary>
public static class OppoCatalog
{
    public static readonly string[] Models =
    {
        // ---- OPPO Enco ----
        "OPPO Enco X4", "OPPO Enco X3", "OPPO Enco X2", "OPPO Enco X",
        "OPPO Enco Free4", "OPPO Enco Free3", "OPPO Enco Free2", "OPPO Enco Free2i",
        "OPPO Enco Free",
        "OPPO Enco Air5 Pro", "OPPO Enco Air5s", "OPPO Enco Air5",
        "OPPO Enco Air4 Pro", "OPPO Enco Air4", "OPPO Enco Air3 Pro", "OPPO Enco Air3",
        "OPPO Enco Air2 Pro", "OPPO Enco Air2s", "OPPO Enco Air2", "OPPO Enco Air",
        "OPPO Enco Buds3 Pro", "OPPO Enco Buds3", "OPPO Enco Buds2", "OPPO Enco Buds",
        "OPPO Enco R3", "OPPO Enco R2",
        "OPPO Enco W51", "OPPO Enco W31",
        "OPPO Enco M32", "OPPO Enco M31",
        // ---- 一加 ----
        "OnePlus Buds Pro 3", "OnePlus Buds Pro 2", "OnePlus Buds Pro",
        "OnePlus Buds 4", "OnePlus Buds 3", "OnePlus Buds",
        "OnePlus Nord Buds 3 Pro", "OnePlus Nord Buds 3", "OnePlus Nord Buds 2r",
        "OnePlus Nord Buds 2", "OnePlus Nord Buds", "OnePlus Buds N",
        "OnePlus Buds Z2", "OnePlus Buds Z",
        // ---- realme ----
        "realme Buds Air 7 Pro", "realme Buds Air 6 Pro", "realme Buds Air 6",
        "realme Buds Air 5 Pro", "realme Buds Air 5", "realme Buds Air 4 Pro",
        "realme Buds Air 4", "realme Buds Air 3 Pro", "realme Buds Air 3",
        "realme Buds Air 2 Pro", "realme Buds Air 2", "realme Buds Air",
        "realme Buds T300", "realme Buds T110", "realme Buds T100",
        "realme Buds Q2s",
    };

    private static readonly string[] FamilyPrefixes =
    {
        "oppo enco", "oneplus buds", "oneplus nord buds", "realme buds",
    };

    private static readonly string[] VendorPrefixes = { "oppo ", "oneplus ", "realme " };

    private static readonly Regex SuffixRegex = new(
        @"\s*(?:\([^)]*\)|\[[^\]]*\]|（[^）]*）|\b(?:l|r|left|right|le|ble|audio)\b)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, string> Canonical = Models
        .ToDictionary(m => m.ToLowerInvariant(), m => m, StringComparer.Ordinal);

    /// <summary>大师调音预设（不同型号子集不同，UI 按型号过滤）。</summary>
    public static readonly EqOption[] EqPresets =
    {
        new(0, "丹拿原声"),
        new(1, "清亮高音"),
        new(2, "纯享人声"),
        new(3, "澎湃低音"),
        new(7, "丹拿高解析"),
    };

    // ------------------------------------------------------------------ //
    public static string? FindModel(string? name)
    {
        var n = Normalize(name);
        if (n.Length == 0) return null;

        var candidates = new HashSet<string>(StringComparer.Ordinal)
        {
            n, StripSuffix(n), StripVendor(n), StripSuffix(StripVendor(n))
        };

        foreach (var candidate in candidates)
        {
            if (Canonical.TryGetValue(candidate, out var hit)) return hit;
        }
        return null;
    }

    public static bool IsFamilyName(string? name)
    {
        var n = Normalize(name);
        if (n.Length == 0) return false;
        return FamilyPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal));
    }

    public static string DisplayName(string? name)
    {
        if (FindModel(name) is { } model) return model;
        if (IsFamilyName(name)) return "OPPO / 一加 / realme 耳机（家族兼容）";
        return string.IsNullOrWhiteSpace(name) ? "未知设备" : name;
    }

    /// <summary>连上就有的基础能力：EQ / 佩戴 / 查找。</summary>
    public static FeatureSet BaseFeatures() =>
        new(new[] { Feature.Eq, Feature.Wear, Feature.Find }, null,
            new[] { Feature.Spatial, Feature.Game });

    // ------------------------------------------------------------------ //
    private static string Normalize(string? name)
    {
        var n = (name ?? "").Trim().ToLowerInvariant().Replace('_', ' ');
        return Regex.Replace(n, @"\s+", " ");
    }

    private static string StripVendor(string name)
    {
        foreach (var prefix in VendorPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal)) return name[prefix.Length..];
        }
        return name;
    }

    private static string StripSuffix(string name)
    {
        var previous = "";
        while (previous != name)
        {
            previous = name;
            name = SuffixRegex.Replace(name, "").Trim();
        }
        return name;
    }
}
