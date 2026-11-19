namespace PodsController.Devices.Huawei;

/// <summary>
/// 华为 / HONOR 型号识别与能力表（对应 huawei/huawei_models.py +
/// huawei/huawei_features.py + huawei/huawei_protocol.py 的语义映射）。
///
/// Python 版是实例化上游 OpenFreebuds 驱动、遍历 handlers 推导能力；
/// C# 版把同一份结论落成表（每行注明对应的 per_model 驱动），
/// 避免为了一个能力位把整套 asyncio 驱动搬进来。
/// </summary>
public static class HuaweiCatalog
{
    /// <summary>规范型号名（= 上游 DEVICE_TO_DRIVER_MAP 的键）。</summary>
    public static readonly string[] Models =
    {
        "HONOR Earbuds 2", "HONOR Earbuds 2 SE", "HONOR Earbuds 2 Lite",
        "HUAWEI FreeBuds 3", "FreeBuds 3",
        "HUAWEI FreeBuds 4i", "HUAWEI FreeBuds 5i", "HUAWEI FreeBuds 6i",
        "HUAWEI FreeBuds Pro", "HUAWEI FreeBuds Pro 2", "HUAWEI FreeBuds Pro 3",
        "HUAWEI FreeBuds Pro 4", "HUAWEI FreeBuds Pro 5",
        "HUAWEI FreeClip", "HUAWEI FreeClip 2",
        "HUAWEI FreeBuds SE", "HUAWEI FreeBuds SE 2", "HUAWEI FreeBuds SE 4 ANC",
        "HUAWEI FreeBuds Studio",
        "HUAWEI FreeLace Pro", "HUAWEI FreeLace Pro 2",
    };

    private static readonly string[] FamilyKeywords =
    {
        "freebuds", "freeclip", "freelace", "earbuds",
    };

    /// <summary>每个型号具备的能力位（与 per_model 驱动里的 handlers 一一对应）。</summary>
    private static readonly Dictionary<string, string[]> Capabilities = new()
    {
        // buds_4i: Info / StateInEar / Anc / Battery
        ["HONOR Earbuds 2"] = new[] { "noise", "wear" },
        ["HONOR Earbuds 2 SE"] = new[] { "noise", "wear" },
        ["HONOR Earbuds 2 Lite"] = new[] { "noise", "wear" },
        // buds_3: Info / StateInEar / Battery（无 ANC）
        ["HUAWEI FreeBuds 3"] = new[] { "wear" },
        ["FreeBuds 3"] = new[] { "wear" },
        ["HUAWEI FreeBuds 4i"] = new[] { "noise", "wear" },
        // buds_5i / buds_6i: 全量 handler
        ["HUAWEI FreeBuds 5i"] = new[] { "noise", "wear", "eq", "dual", "game" },
        ["HUAWEI FreeBuds 6i"] = new[] { "noise", "wear", "eq", "dual", "game" },
        // buds_pro: 无 EQ / 低延迟
        ["HUAWEI FreeBuds Pro"] = new[] { "noise", "wear", "dual" },
        ["HUAWEI FreeBuds Pro 2"] = new[] { "noise", "wear", "eq", "dual" },
        ["HUAWEI FreeBuds Pro 3"] = new[] { "noise", "wear", "eq", "dual", "game" },
        ["HUAWEI FreeBuds Pro 4"] = new[] { "noise", "wear", "eq", "dual", "game" },
        ["HUAWEI FreeBuds Pro 5"] = new[] { "noise", "wear", "eq", "dual", "game" },
        ["HUAWEI FreeClip"] = new[] { "noise", "wear", "eq", "dual", "game" },
        // free_clip_2 是开放式耳夹，无 ANC handler
        ["HUAWEI FreeClip 2"] = new[] { "wear", "eq", "dual", "game" },
        ["HUAWEI FreeBuds SE"] = Array.Empty<string>(),
        ["HUAWEI FreeBuds SE 2"] = new[] { "eq", "game" },
        ["HUAWEI FreeBuds SE 4 ANC"] = new[] { "noise", "eq", "game" },
        ["HUAWEI FreeBuds Studio"] = new[] { "noise", "eq" },
        ["HUAWEI FreeLace Pro"] = new[] { "noise" },
        ["HUAWEI FreeLace Pro 2"] = new[] { "noise", "eq", "dual", "game" },
    };

    private static readonly Dictionary<string, string> NormalizedExact = BuildExact();
    private static readonly Dictionary<string, string> NormalizedBare = BuildBare();

    /// <summary>型号 → (UI 序号 → 耳机侧 preset id)。</summary>
    private static readonly Dictionary<string, Dictionary<int, int>> PresetIds = new()
    {
        ["HUAWEI FreeBuds Pro 5"] = new() { [0] = 5, [1] = 1, [2] = 2, [3] = 9 },
        ["HUAWEI FreeClip 2"] = new() { [0] = 1, [1] = 2, [2] = 3, [3] = 9 },
    };

    private static readonly Dictionary<int, int> DefaultPresetIds = new()
    {
        [0] = 1, [1] = 2, [2] = 3, [3] = 9
    };

    public static readonly string[] AudioEffectNames = { "默认", "超重低音", "清亮高音", "清澈人声" };

    // ------------------------------------------------------------------ //
    public static string? FindModel(string? name)
    {
        var n = Normalize(name);
        if (n.Length == 0) return null;

        if (NormalizedExact.TryGetValue(n, out var exact)) return exact;

        var bare = StripVendor(n);
        if (NormalizedBare.TryGetValue(bare, out var hit)) return hit;
        return null;
    }

    /// <summary>是否「像」华为耳机（仅用于显示，不用于自动接管）。</summary>
    public static bool IsFamilyName(string? name)
    {
        var n = Normalize(name);
        if (n.Length == 0) return false;
        if (NormalizedExact.ContainsKey(n) || NormalizedBare.ContainsKey(StripVendor(n))) return true;
        return FamilyKeywords.Any(k => n.Contains(k, StringComparison.Ordinal));
    }

    public static string DisplayName(string? name)
    {
        if (FindModel(name) is { } model) return model;
        if (IsFamilyName(name)) return "华为 / HONOR 耳机（家族兼容）";
        return string.IsNullOrWhiteSpace(name) ? "未知设备" : name;
    }

    public static Core.FeatureSet Features(string? model)
    {
        if (model is null || !Capabilities.TryGetValue(model, out var caps))
        {
            return Core.FeatureSet.Empty;
        }
        // 华为侧永远没有「查找 / 空间音频」（上游无 handler）
        var supported = caps.Where(c => c != Core.Feature.Find && c != Core.Feature.Spatial).ToArray();
        var experimental = supported.Contains(Core.Feature.Game) ? new[] { Core.Feature.Game } : Array.Empty<string>();
        return new Core.FeatureSet(supported, null, experimental);
    }

    public static IReadOnlyList<Core.EqOption> EqOptions(string? model)
    {
        if (model is null || Features(model).Has(Core.Feature.Eq) == false)
        {
            return Array.Empty<Core.EqOption>();
        }
        var table = PresetIds.TryGetValue(model, out var t) ? t : DefaultPresetIds;
        return table.Keys.OrderBy(k => k)
            .Select(code => new Core.EqOption(code, AudioEffectNames[code]))
            .ToArray();
    }

    public static int? PresetIdFromUiCode(string? model, int uiCode)
    {
        var table = PresetIds.TryGetValue(model ?? "", out var t) ? t : DefaultPresetIds;
        return table.TryGetValue(uiCode, out var id) ? id : null;
    }

    public static int? UiCodeFromPresetId(string? model, int presetId)
    {
        var table = PresetIds.TryGetValue(model ?? "", out var t) ? t : DefaultPresetIds;
        foreach (var (code, id) in table)
        {
            if (id == presetId) return code;
        }
        return null;
    }

    // ------------------------------------------------------------------ //
    private static Dictionary<string, string> BuildExact()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var model in Models)
        {
            map.TryAdd(Normalize(model), model);
        }
        return map;
    }

    private static Dictionary<string, string> BuildBare()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var model in Models)
        {
            map.TryAdd(StripVendor(Normalize(model)), model);
        }
        return map;
    }

    private static string Normalize(string? value) =>
        new string((value ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string StripVendor(string normalized)
    {
        foreach (var prefix in new[] { "huawei", "honor" })
        {
            if (normalized.StartsWith(prefix)) return normalized[prefix.Length..];
        }
        return normalized;
    }
}
