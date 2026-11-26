using System.Windows;
using System.Windows.Controls;
using PcMonitor.Core;

namespace PcMonitor.Views;

/// <summary>Выбор метрики и оформления плитки дашборда.</summary>
public partial class TileEditorWindow : Window
{
    private readonly List<MetricOption> _options;

    public TileEditorWindow(IEnumerable<SensorRow> rows, TileConfig? existing = null)
    {
        InitializeComponent();
        Title = LocalizationManager.T("Плитка дашборда");
        SearchBox.ToolTip = LocalizationManager.T("Поиск по названию метрики, устройства или датчика");
        LocalizationManager.Apply(this, LocalizationManager.Language);

        _options = BuildOptions(rows);
        MetricBox.ItemsSource = _options;

        if (existing is not null)
        {
            TitleBox.Text = existing.Title;
            WidthBox.Text = existing.Width.ToString();
            HeightBox.Text = existing.Height.ToString();
            WarningBox.Text = existing.Warning?.ToString("0.#") ?? string.Empty;
            CriticalBox.Text = existing.Critical?.ToString("0.#") ?? string.Empty;
            HistoryCheck.IsChecked = existing.ShowHistory;

            foreach (RadioButton radio in new[] { VisualValue, VisualGauge, VisualBar, VisualGraph })
            {
                if (radio.Tag is string tag && tag == existing.Visual.ToString())
                    radio.IsChecked = true;
            }

            MetricBox.SelectedItem = _options.FirstOrDefault(o => o.Key == existing.MetricKey);
        }

        if (MetricBox.SelectedItem is null && _options.Count > 0)
            MetricBox.SelectedItem = _options[0];
    }

    /// <summary>Итоговая конфигурация плитки.</summary>
    public TileConfig? Result { get; private set; }

    private static List<MetricOption> BuildOptions(IEnumerable<SensorRow> rows)
    {
        var options = new List<MetricOption>();

        foreach (MetricDefinition metric in MetricCatalog.Aggregates)
        {
            options.Add(new MetricOption(metric.Key,
                $"{LocalizationManager.T("Сводка")} · {LocalizationManager.T(metric.Title)}", metric.Unit));
        }

        foreach (SensorRow row in rows
                     .Where(r => r.Type != LibreHardwareMonitor.Hardware.SensorType.Timing)
                     .OrderBy(r => r.HardwareOrder)
                     .ThenBy(r => r.Hardware, StringComparer.CurrentCulture)
                     .ThenBy(r => r.TypeOrder)
                     .ThenBy(r => r.DisplayName, StringComparer.CurrentCulture))
        {
            string marker = row.IsThreshold
                ? $" ({LocalizationManager.T("порог")})"
                : !row.IsPlausible ? $" ({LocalizationManager.T("недостоверно")})" : string.Empty;
            options.Add(new MetricOption(
                MetricCatalog.SensorPrefix + row.Id,
                $"{LocalizationManager.T("Датчик")} · {row.Hardware}: {row.DisplayName}{marker}",
                row.Unit));
        }

        return options;
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        string query = SearchBox.Text.Trim();
        MetricBox.ItemsSource = query.Length == 0
            ? _options
            : _options.Where(o => o.Display.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (MetricBox.SelectedItem is not MetricOption option)
        {
            MessageBox.Show(this, LocalizationManager.T("Выберите метрику для плитки."), LocalizationManager.T("Плитка"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var config = new TileConfig
        {
            MetricKey = option.Key,
            Title = TitleBox.Text.Trim(),
            Visual = SelectedVisual(),
            Width = ParseInt(WidthBox.Text, 260, 140, 900),
            Height = ParseInt(HeightBox.Text, 150, 110, 700),
            Warning = ParseDouble(WarningBox.Text),
            Critical = ParseDouble(CriticalBox.Text),
            ShowHistory = HistoryCheck.IsChecked == true,
        };

        Result = config;
        DialogResult = true;
    }

    private TileVisual SelectedVisual()
    {
        foreach (RadioButton radio in new[] { VisualValue, VisualGauge, VisualBar, VisualGraph })
        {
            if (radio.IsChecked == true && radio.Tag is string tag && Enum.TryParse(tag, out TileVisual visual))
                return visual;
        }

        return TileVisual.Value;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text, out int value) ? Math.Clamp(value, min, max) : fallback;

    private static double? ParseDouble(string text) =>
        double.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : null;

    private sealed record MetricOption(string Key, string Display, string Unit)
    {
        public override string ToString() => Display;
    }
}
