using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;
using PcMonitor.Core;

namespace PcMonitor.Controls;

/// <summary>Простой график-«искра» для истории значений датчика.</summary>
public sealed class Sparkline : FrameworkElement
{
    private static readonly Typeface Face = new("Segoe UI");
    private Popup? _hoverPopup;
    private Border? _hoverBorder;
    private TextBlock? _hoverText;
    private int _hoverIndex = -1;

    public Sparkline()
    {
        Unloaded += (_, _) => HideHover();
        IsVisibleChanged += (_, _) => { if (!IsVisible) HideHover(); };
    }

    /// <summary>
    /// Точки графика (float[]). Тип object нужен, чтобы XAML-компилятор
    /// позволял привязку внутри DataTemplate — с типом-массивом он падает (MC4102).
    /// </summary>
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(object), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnHoverPropertyChanged));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
        nameof(Format), typeof(string), typeof(Sparkline),
        new FrameworkPropertyMetadata("0.0", FrameworkPropertyMetadataOptions.AffectsRender, OnHoverPropertyChanged));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(Sparkline),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender, OnHoverPropertyChanged));

    /// <summary>Тип датчика для форматирования показания в подсказке.</summary>
    public static readonly DependencyProperty MetricTypeProperty = DependencyProperty.Register(
        nameof(MetricType), typeof(SensorType?), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, OnHoverPropertyChanged));

    /// <summary>Перевод значения графика в исходные единицы датчика для подсказки.</summary>
    public static readonly DependencyProperty TooltipValueScaleProperty = DependencyProperty.Register(
        nameof(TooltipValueScale), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(1d, OnHoverPropertyChanged));

    public static readonly DependencyProperty FixedRangeProperty = DependencyProperty.Register(
        nameof(FixedRange), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Компактный режим: только линия и заливка, без сетки и подписей (для плиток).</summary>
    public static readonly DependencyProperty CompactProperty = DependencyProperty.Register(
        nameof(Compact), typeof(bool), typeof(Sparkline),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool Compact
    {
        get => (bool)GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    public object? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    /// <summary>Формат чисел на оси (например "0.0").</summary>
    public string Format
    {
        get => (string)GetValue(FormatProperty);
        set => SetValue(FormatProperty, value);
    }

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public SensorType? MetricType
    {
        get => (SensorType?)GetValue(MetricTypeProperty);
        set => SetValue(MetricTypeProperty, value);
    }

    public double TooltipValueScale
    {
        get => (double)GetValue(TooltipValueScaleProperty);
        set => SetValue(TooltipValueScaleProperty, value);
    }

    /// <summary>Если больше нуля — ось фиксируется от 0 до этого значения.</summary>
    public double FixedRange
    {
        get => (double)GetValue(FixedRangeProperty);
        set => SetValue(FixedRangeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 20 || height < 20) return;

        var full = new Rect(0, 0, width, height);
        Pen borderPen = new(Theme.Border, 1);
        Pen gridPen = new(Theme.Border, 0.8);
        Brush labelBrush = Theme.TextFaint;
        dc.DrawRoundedRectangle(Theme.PanelAlt, borderPen, full, 8, 8);

        Rect plot = PlotBounds(width, height);
        float[] values = Values as float[] ?? Array.Empty<float>();

        double min = double.MaxValue, max = double.MinValue;
        int finite = 0;
        foreach (float v in values)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) continue;
            finite++;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        if (finite == 0)
        {
            DrawText(dc, LocalizationManager.T("нет данных"),
                new Point(plot.Left + (Compact ? 8 : 0), plot.Top + plot.Height / 2 - (Compact ? 7 : 8)),
                    Compact ? 10.5 : 12, labelBrush);
            return;
        }

        if (FixedRange > 0)
        {
            min = 0;
            max = FixedRange;
        }
        else
        {
            double span = max - min;
            if (span < 1e-6)
            {
                double pad = Math.Max(Math.Abs(max) * 0.05, 1);
                min -= pad;
                max += pad;
            }
            else
            {
                min -= span * 0.08;
                max += span * 0.08;
            }
        }

        // Сетка и подписи по вертикали (в компактном режиме не нужны).
        if (!Compact)
        {
            for (int i = 0; i <= 4; i++)
            {
                double y = plot.Top + plot.Height * i / 4.0;
                dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                double value = max - (max - min) * i / 4.0;
                DrawText(dc, value.ToString(Format, CultureInfo.CurrentCulture) + (Unit.Length > 0 ? " " + Unit : ""),
                    new Point(6, y - 8), 10.5, labelBrush);
            }
        }

        // Линия и заливка по непрерывным участкам.
        int count = values.Length;
        double step = count > 1 ? plot.Width / (count - 1) : plot.Width;
        double scale = plot.Height / Math.Max(1e-9, max - min);

        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var lineCtx = line.Open())
        using (var fillCtx = fill.Open())
        {
            bool lineOpen = false, fillOpen = false;
            for (int i = 0; i < count; i++)
            {
                float raw = values[i];
                if (float.IsNaN(raw) || float.IsInfinity(raw))
                {
                    lineOpen = false;
                    fillOpen = false;
                    continue;
                }

                double x = plot.Left + i * step;
                double y = plot.Bottom - (raw - min) * scale;

                if (!lineOpen)
                {
                    lineCtx.BeginFigure(new Point(x, y), false, false);
                    lineOpen = true;
                }
                else
                {
                    lineCtx.LineTo(new Point(x, y), true, false);
                }

                if (!fillOpen)
                {
                    fillCtx.BeginFigure(new Point(x, plot.Bottom), true, true);
                    fillCtx.LineTo(new Point(x, y), true, false);
                    fillOpen = true;
                }
                else
                {
                    fillCtx.LineTo(new Point(x, y), true, false);
                }

                if (i + 1 >= count || float.IsNaN(values[i + 1]))
                {
                    if (fillOpen)
                    {
                        fillCtx.LineTo(new Point(x, plot.Bottom), true, false);
                        fillOpen = false;
                    }
                    lineOpen = false;
                }
            }
        }

        line.Freeze();
        fill.Freeze();

        dc.PushClip(new RectangleGeometry(plot));
        Color accent = Theme.Accent is SolidColorBrush accentBrush ? accentBrush.Color : Colors.DodgerBlue;
        var fillBrush = new LinearGradientBrush(
            WithAlpha(accent, 0x66),
            WithAlpha(accent, 0x08),
            new Point(0, 0), new Point(0, 1));
        fillBrush.Freeze();
        dc.DrawGeometry(fillBrush, null, fill);
        var linePen = new Pen(LineBrush, 1.8) { LineJoin = PenLineJoin.Round };
        linePen.Freeze();
        dc.DrawGeometry(null, linePen, line);

        if (_hoverIndex >= 0 && _hoverIndex < count)
        {
            double hoverX = plot.Left + _hoverIndex * step;
            dc.DrawLine(new Pen(Theme.TextDim, 1), new Point(hoverX, plot.Top), new Point(hoverX, plot.Bottom));
            float selected = values[_hoverIndex];
            if (float.IsFinite(selected))
            {
                double hoverY = plot.Bottom - (selected - min) * scale;
                dc.DrawEllipse(LineBrush, new Pen(Theme.Panel, 1), new Point(hoverX, hoverY), 3.5, 3.5);
            }
        }
        dc.Pop();

        // Текущее значение и последняя точка.
        float last = float.NaN;
        int lastIndex = -1;
        for (int i = count - 1; i >= 0; i--)
        {
            if (float.IsFinite(values[i])) { last = values[i]; lastIndex = i; break; }
        }

        if (!Compact && lastIndex >= 0)
        {
            double y = plot.Bottom - (last - min) * scale;
            double x = plot.Left + lastIndex * step;
            dc.DrawEllipse(LineBrush, null, new Point(Math.Min(x, plot.Right), y), 3, 3);
            DrawText(dc, last.ToString(Format, CultureInfo.CurrentCulture) + (Unit.Length > 0 ? " " + Unit : ""),
                new Point(plot.Left + 4, 6), 13, LineBrush, bold: true);
            DrawText(dc, $"{finite} точек", new Point(plot.Right - 60, 6), 10, labelBrush);
        }
        else if (Compact && lastIndex >= 0)
        {
            double y = plot.Bottom - (last - min) * scale;
            double x = plot.Left + lastIndex * step;
            dc.DrawEllipse(LineBrush, null, new Point(Math.Min(x, plot.Right - 2), y), 2.5, 2.5);
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        UpdateHover(e.GetPosition(this));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdateHover(e.GetPosition(this));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        HideHover();
    }

    private static void OnHoverPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (Sparkline)d;
        if (chart.IsMouseOver)
            chart.UpdateHover(Mouse.GetPosition(chart));
    }

    private Rect PlotBounds(double width, double height)
    {
        double left = Compact ? 4 : 62;
        double right = Compact ? 4 : 14;
        double top = Compact ? 4 : 26;
        double bottom = Compact ? 4 : 20;
        return new Rect(left, top, Math.Max(1, width - left - right), Math.Max(1, height - top - bottom));
    }

    private void UpdateHover(Point position)
    {
        float[] values = Values as float[] ?? Array.Empty<float>();
        if (values.Length == 0 || ActualWidth < 20 || ActualHeight < 20)
        {
            HideHover();
            return;
        }

        Rect plot = PlotBounds(ActualWidth, ActualHeight);
        double relativeX = Math.Clamp((position.X - plot.Left) / plot.Width, 0, 1);
        int index = (int)Math.Round(relativeX * (values.Length - 1), MidpointRounding.AwayFromZero);
        _hoverIndex = index;
        float value = values[index];
        string text = !float.IsFinite(value) ? LocalizationManager.T("нет данных")
            : MetricType is { } type ? SensorCatalog.Format(type, value * TooltipValueScale)
            : LocalizationManager.FormatNumber(value, format: Format) + (string.IsNullOrEmpty(Unit) ? "" : " " + Unit);

        EnsureHoverPopup();
        _hoverText!.Text = text;
        _hoverText.Foreground = Theme.Text;
        _hoverBorder!.Background = Theme.Panel;
        _hoverBorder.BorderBrush = Theme.Border;
        _hoverPopup!.HorizontalOffset = Math.Clamp(position.X + 8, 0, ActualWidth);
        _hoverPopup.VerticalOffset = position.Y - 32;
        _hoverPopup.IsOpen = true;
        InvalidateVisual();
    }

    private void EnsureHoverPopup()
    {
        if (_hoverPopup is not null) return;
        _hoverText = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 12 };
        _hoverBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 4, 7, 4),
            IsHitTestVisible = false,
            Child = _hoverText,
        };
        _hoverPopup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.Relative,
            AllowsTransparency = true,
            StaysOpen = true,
            Child = _hoverBorder,
        };
    }

    private void HideHover()
    {
        if (_hoverPopup is not null) _hoverPopup.IsOpen = false;
        if (_hoverIndex < 0) return;
        _hoverIndex = -1;
        InvalidateVisual();
    }

    private void DrawText(DrawingContext dc, string text, Point origin, double size, Brush brush, bool bold = false)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            bold ? new Typeface(Face.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal) : Face,
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        dc.DrawText(formatted, origin);
    }

    private static Pen CreatePen(string hex, double thickness)
    {
        var pen = new Pen(CreateBrush(hex), thickness);
        pen.Freeze();
        return pen;
    }

    private static Brush CreateBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}
