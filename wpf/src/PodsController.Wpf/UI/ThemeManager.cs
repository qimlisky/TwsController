using System.Windows;

namespace PodsController.UI;

/// <summary>
/// 深浅色主题切换（对应 Python 版「深色模式」+ tray.refresh_tray_icon）。
///
/// 两套配色放在两本资源字典里，控件一律用 DynamicResource 引用，
/// 所以切换时只替换 Application.Resources.MergedDictionaries[0] 即可即时生效，
/// 不需要重建窗口。
/// </summary>
public static class ThemeManager
{
    private const string LightUri = "pack://application:,,,/PodsController.Wpf;component/UI/Themes/Light.xaml";
    private const string DarkUri = "pack://application:,,,/PodsController.Wpf;component/UI/Themes/Dark.xaml";

    public static bool IsDark { get; private set; }

    public static void Apply(bool dark)
    {
        var app = Application.Current;
        if (app is null) return;

        try
        {
            var dictionary = new ResourceDictionary
            {
                Source = new Uri(dark ? DarkUri : LightUri)
            };

            var merged = app.Resources.MergedDictionaries;
            if (merged.Count == 0)
            {
                merged.Add(dictionary);
            }
            else
            {
                merged[0] = dictionary;
            }

            IsDark = dark;
        }
        catch (Exception ex)
        {
            Core.AppLog.Runtime($"主题切换失败：{ex.Message}");
        }
    }
}
