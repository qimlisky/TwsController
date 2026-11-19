using PodsController.Core;

namespace PodsController.Devices.Redmi;

/// <summary>
/// Redmi / 小米耳机型号识别与能力矩阵。
///
/// 协议参考 FallenLeeee / MiBudsController（MIT License）：
/// https://github.com/FallenLeeee/MiBudsController
/// 目前以 Redmi Buds 8 Pro 为已验证目标；同协议型号按家族兼容处理。
/// </summary>
public static class RedmiCatalog
{
    public static readonly string[] Models =
    {
        "Redmi Buds 8 Pro",
    };

    private static readonly string[] FamilyKeywords =
    {
        "redmi buds", "redmi 耳机", "xiaomi buds", "xiaomi 耳机", "小米耳机", "小米蓝牙耳机",
    };

    public static string? FindModel(string? name)
    {
        var normalized = Normalize(name);
        if (normalized.Length == 0) return null;

        foreach (var model in Models)
        {
            if (Normalize(model) == normalized) return model;
        }

        var bare = StripVendor(normalized);
        foreach (var model in Models)
        {
            if (StripVendor(Normalize(model)) == bare) return model;
        }

        return null;
    }

    public static bool IsFamilyName(string? name)
    {
        var normalized = Normalize(name);
        return normalized.Length > 0 && FamilyKeywords.Any(k => normalized.Contains(Normalize(k)));
    }

    public static string DisplayName(string? name)
    {
        return FindModel(name) ?? (string.IsNullOrWhiteSpace(name) ? "Redmi 耳机" : name);
    }

    /// <summary>
    /// 已验证能力：降噪、空间音频、低延迟（UI 的“低延迟”复用 Game 位）。
    /// 查找耳机、EQ、双连暂未从参考项目确认，不显示。
    /// </summary>
    public static FeatureSet Features()
    {
        return new FeatureSet(
            new[] { Feature.Noise, Feature.Spatial, Feature.Game },
            experimental: new[] { Feature.Spatial, Feature.Game });
    }

    private static string Normalize(string? value) =>
        new string((value ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string StripVendor(string normalized)
    {
        foreach (var prefix in new[] { "redmi", "xiaomi", "mi" })
        {
            if (normalized.StartsWith(prefix)) return normalized[prefix.Length..];
        }

        return normalized;
    }
}
