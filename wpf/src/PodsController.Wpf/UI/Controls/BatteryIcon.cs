using System.Windows;
using System.Windows.Media;

namespace PodsController.UI.Controls;

/// <summary>
/// 电量电池图标：对应 Python ui/widgets.py 的 battery_pixmap()。
/// 外描边 + 右侧端子 + 15 档量化填充；&lt;20% 用警示色。
/// 34×12（展开）/ 28×10（折叠），描边粗细 6% 宽度。
/// </summary>
public sealed class BatteryIcon : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(
        nameof(Percent), typeof(int), typeof(BatteryIcon),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeBrushProperty = DependencyProperty.Register(
        nameof(StrokeBrush), typeof(Brush), typeof(BatteryIcon),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(BatteryIcon),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LowBrushProperty = DependencyProperty.Register(
        nameof(LowBrush), typeof(Brush), typeof(BatteryIcon),
        new FrameworkPropertyMetadata(Brushes.IndianRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Percent
    {
        get => (int)GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public Brush StrokeBrush
    {
        get => (Brush)GetValue(StrokeBrushProperty);
        set => SetValue(StrokeBrushProperty, value);
    }

    public Brush FillBrush
    {
        get => (Brush)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush LowBrush
    {
        get => (Brush)GetValue(LowBrushProperty);
        set => SetValue(LowBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 2 || h <= 2) return;

        var stroke = StrokeBrush ?? Brushes.Gray;
        var fill = Percent >= 0 && Percent < 20 ? (LowBrush ?? Brushes.IndianRed) : (FillBrush ?? Brushes.DeepSkyBlue);

        var penW = Math.Max(1.0, w * 0.06);
        var margin = 1.0;
        var nubW = w * 0.10;
        var gap = w * 0.04;
        var bodyW = Math.Max(4.0, w - margin * 2 - nubW - gap);
        var bodyH = h - margin * 2;
        var radius = Math.Min(bodyH / 2, h * 0.20);

        var pen = new Pen(stroke, penW);
        var body = new Rect(margin + penW / 2, margin + penW / 2,
                            Math.Max(1, bodyW - penW), Math.Max(1, bodyH - penW));
        dc.DrawRoundedRectangle(null, pen, body, radius, radius);

        // 右侧端子
        var nubH = h * 0.32;
        var nub = new Rect(margin + bodyW + gap, (h - nubH) / 2, Math.Max(1, nubW - penW), Math.Max(1, nubH));
        dc.DrawRoundedRectangle(stroke, null, nub, 1, 1);

        if (Percent < 0) return;

        // 15 档量化，从左往右填充
        var steps = Math.Clamp((int)Math.Floor(Percent / 100.0 * 15), 0, 15);
        if (steps <= 0) return;

        var innerLeft = margin + penW;
        var innerTop = margin + penW;
        var innerW = Math.Max(0, bodyW - penW * 1.6);
        var innerH = Math.Max(0, bodyH - penW * 1.6);
        var fillW = innerW * steps / 15.0;
        var fr = Math.Max(0, Math.Min(innerH / 2, radius - penW / 2));
        dc.DrawRoundedRectangle(fill, null,
            new Rect(innerLeft, innerTop, Math.Max(0, fillW), innerH), fr, fr);
    }
}
