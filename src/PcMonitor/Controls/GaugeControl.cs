using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PcMonitor.Core;

namespace PcMonitor.Controls;

/// <summary>Круговая шкала для плиток дашборда.</summary>
public sealed class GaugeControl : FrameworkElement
{

    /// <summary>Заполнение шкалы: 0 — пусто, 1 — максимум.</summary>
    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(GaugeControl),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Крупный текст в центре (обычно значение с единицей).</summary>
    public static readonly DependencyProperty ValueTextProperty = DependencyProperty.Register(
        nameof(ValueText), typeof(string), typeof(GaugeControl),
        new FrameworkPropertyMetadata("—", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(GaugeControl),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ScaleTextProperty = DependencyProperty.Register(
        nameof(ScaleText), typeof(string), typeof(GaugeControl),
        new FrameworkPropertyMetadata("0", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ScaleMaxTextProperty = DependencyProperty.Register(
        nameof(ScaleMaxText), typeof(string), typeof(GaugeControl),
        new FrameworkPropertyMetadata("100", FrameworkPropertyMetadataOptions.AffectsRender));

    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public string ValueText
    {
        get => (string)GetValue(ValueTextProperty);
        set => SetValue(ValueTextProperty, value);
    }

    public Brush Accent
    {
        get => (Brush)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public string ScaleText
    {
        get => (string)GetValue(ScaleTextProperty);
        set => SetValue(ScaleTextProperty, value);
    }

    public string ScaleMaxText
    {
        get => (string)GetValue(ScaleMaxTextProperty);
        set => SetValue(ScaleMaxTextProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 30) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2 + size * 0.03);
        double radius = size / 2 - 12;
        const double startAngle = 150;
        const double sweep = 240;

        var trackPen = new Pen(Theme.PanelAlt, Math.Max(6, size * 0.075)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var valuePen = new Pen(Accent, Math.Max(6, size * 0.075)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        dc.DrawGeometry(null, trackPen, Arc(center, radius, startAngle, startAngle + sweep));

        double fraction = Math.Clamp(Fraction, 0, 1);
        if (fraction > 0.001)
        {
            dc.DrawGeometry(null, valuePen, Arc(center, radius, startAngle, startAngle + sweep * fraction));
        }

        // Риски по шкале.
        var tickPen = new Pen(Theme.PanelAlt, 2);
        for (int i = 0; i <= 8; i++)
        {
            double angle = (startAngle + sweep * i / 8.0) * Math.PI / 180;
            var outer = new Point(center.X + Math.Cos(angle) * (radius + 8), center.Y + Math.Sin(angle) * (radius + 8));
            var inner = new Point(center.X + Math.Cos(angle) * (radius + 3), center.Y + Math.Sin(angle) * (radius + 3));
            dc.DrawLine(tickPen, inner, outer);
        }

        DrawText(dc, ValueText, center, size * 0.16, Theme.Text, bold: true, centered: true);
        DrawText(dc, ScaleText, new Point(center.X - radius * 0.72, center.Y + radius * 0.72), size * 0.075, Theme.TextFaint, false, true);
        DrawText(dc, ScaleMaxText, new Point(center.X + radius * 0.72, center.Y + radius * 0.72), size * 0.075, Theme.TextFaint, false, true);
    }

    private static StreamGeometry Arc(Point center, double radius, double startAngle, double endAngle)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            Point start = PointOnCircle(center, radius, startAngle);
            Point end = PointOnCircle(center, radius, endAngle);
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, endAngle - startAngle > 180,
                SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        double angle = angleDegrees * Math.PI / 180;
        return new Point(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
    }

    private void DrawText(DrawingContext dc, string text, Point origin, double size, Brush brush, bool bold, bool centered)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
                bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            Math.Max(8, size),
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        Point point = centered
            ? new Point(origin.X - formatted.Width / 2, origin.Y - formatted.Height / 2)
            : origin;
        dc.DrawText(formatted, point);
    }

}
