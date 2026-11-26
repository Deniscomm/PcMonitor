using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using PcMonitor.Controls;
using PcMonitor.Core;

namespace PcMonitor.Views;

/// <summary>
/// Экранный оверлей в духе внешних приложений: окно поверх всех, сквозное для кликов,
/// показывает выбранные метрики с мини-графиками. Переключается клавишами Ctrl+Alt+O.
/// </summary>
public partial class OsdWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    private readonly Dictionary<string, (TextBlock Value, HistoryBuffer History, Sparkline? Chart, MetricDefinition Metric)> _rows = new();
    private MonitorContext? _context;

    public OsdWindow()
    {
        InitializeComponent();
        ApplyLanguage(LocalizationManager.Language);
        SourceInitialized += (_, _) => MakeClickThrough();
    }

    public void Attach(MonitorContext context)
    {
        _context = context;
        BuildRows();
        context.Updated += Refresh;
        ApplyLayout();
        Refresh();
    }

    /// <summary>Пересобрать список метрик (после изменения настроек).</summary>
    public void Rebuild()
    {
        BuildRows();
        ApplyLayout();
        Refresh();
    }

    /// <summary>Обновить подписи OSD после смены языка интерфейса.</summary>
    public void ApplyLanguage(string language)
    {
        HeaderTitle.Text = language switch
        {
            "en-US" or "en-GB" => "PC MONITOR",
            "zh-CN" => "电脑监控",
            _ => "МОНИТОРИНГ ПК",
        };
        BuildRows();
        ApplyLayout();
        Refresh();
    }

    private void MakeClickThrough()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExTransparent | WsExLayered | WsExNoActivate | WsExToolWindow);
    }

    private void BuildRows()
    {
        RowsHost.Children.Clear();
        _rows.Clear();

        if (_context is null) return;

        double fontSize = Math.Clamp(_context.Settings.OsdFontSize, 10, 28);
        foreach (string key in _context.Settings.OsdMetrics)
        {
            MetricDefinition? metric = MetricCatalog.Find(key);
            if (metric is null) continue;

            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), MinWidth = 220 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var title = new TextBlock
            {
                Text = LocalizationManager.T(metric.Title),
                Foreground = Theme.TextDim,
                FontSize = Math.Max(10, fontSize - 3),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            };
            Grid.SetColumn(title, 0);
            grid.Children.Add(title);

            var value = new TextBlock
            {
                Text = "—",
                Foreground = Theme.Text,
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);

            RowsHost.Children.Add(grid);
            _rows[key] = (value, new HistoryBuffer(240), null, metric);
        }

        HeaderRow.Visibility = _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Положение и прозрачность оверлея по настройкам.</summary>
    public void ApplyLayout()
    {
        if (_context is null) return;

        AppSettings settings = _context.Settings;
        Root.Opacity = Math.Clamp(settings.OsdOpacity, 0.2, 1.0);

        var area = SystemParameters.WorkArea;
        Left = settings.OsdCorner switch
        {
            "RightTop" or "RightBottom" => area.Right - ActualWidth - settings.OsdOffsetX,
            _ => area.Left + settings.OsdOffsetX,
        };
        Top = settings.OsdCorner switch
        {
            "LeftBottom" or "RightBottom" => area.Bottom - ActualHeight - settings.OsdOffsetY,
            _ => area.Top + settings.OsdOffsetY,
        };

        if (double.IsNaN(Left) || double.IsNaN(Top))
        {
            Left = area.Left + settings.OsdOffsetX;
            Top = area.Top + settings.OsdOffsetY;
        }
    }

    private void Refresh()
    {
        if (_context is null) return;

        List<SensorRow> rows = _context.MeasurementRows();
        foreach (var entry in _rows)
        {
            double? value = entry.Value.Metric.Resolve(rows);
            if (value is { } v && !double.IsNaN(v))
            {
                entry.Value.Value.Text = SensorCatalog.Format(entry.Value.Metric.Type, v);
                entry.Value.Value.Foreground = BrushFor(entry.Value.Metric.Type, v);
                entry.Value.History.Add((float)v);
            }
            else
            {
                entry.Value.Value.Text = "—";
                entry.Value.Value.Foreground = Theme.TextFaint;
                entry.Value.History.Add(float.NaN);
            }

            if (entry.Value.Chart is { } chart)
            {
                chart.LineBrush = entry.Value.Value.Foreground;
                chart.Values = entry.Value.History.ToArray();
            }
        }

        HeaderRight.Text = DateTime.Now.ToString("HH:mm:ss");
        ApplyLayout();
    }

    private static Brush BrushFor(LibreHardwareMonitor.Hardware.SensorType type, double value) => type switch
    {
        LibreHardwareMonitor.Hardware.SensorType.Temperature =>
            value >= 85 ? Theme.Bad : value >= 75 ? Theme.Warn : Theme.Good,
        LibreHardwareMonitor.Hardware.SensorType.Load =>
            value >= 95 ? Theme.Bad : value >= 80 ? Theme.Warn : Theme.Text,
        _ => Theme.Text,
    };
}
