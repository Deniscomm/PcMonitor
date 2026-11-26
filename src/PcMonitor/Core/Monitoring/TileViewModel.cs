using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;
using PcMonitor.Core.Diagnostics;

namespace PcMonitor.Core;

/// <summary>Плитка дашборда: значение метрики, оформление, история для графика.</summary>
public sealed class TileViewModel : INotifyPropertyChanged
{
    private static readonly HashSet<string> ResolveFailures = new(StringComparer.Ordinal);
    private readonly HistoryBuffer _history;
    private string _title = string.Empty;
    private string _valueText = "—";
    private string _subText = string.Empty;
    private string _unit = string.Empty;
    private double _value;
    private double _progress;
    private Brush _accent = Theme.Accent;
    private Brush _progressBrush = Theme.Accent;
    private SensorType? _metricType;
    private float[] _historyView = Array.Empty<float>();
    private bool _hasValue;
    private string? _lastMetricKey;
    private double? _lastResolvedValue;

    public TileViewModel(TileConfig config, int historyPoints)
    {
        Config = config;
        _history = new HistoryBuffer(Math.Clamp(historyPoints, 60, 20000));
        _title = string.IsNullOrWhiteSpace(config.Title) ? "Плитка" : config.Title;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TileConfig Config { get; }

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    public string ValueText
    {
        get => _valueText;
        private set => Set(ref _valueText, value);
    }

    public string SubText
    {
        get => _subText;
        private set => Set(ref _subText, value);
    }

    public string Unit
    {
        get => _unit;
        private set => Set(ref _unit, value);
    }

    public double Value
    {
        get => _value;
        private set => Set(ref _value, value);
    }

    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, Math.Clamp(value, 0, 1));
    }

    public Brush Accent
    {
        get => _accent;
        private set => Set(ref _accent, value);
    }

    public Brush ProgressBrush
    {
        get => _progressBrush;
        private set => Set(ref _progressBrush, value);
    }

    public float[] History
    {
        get => _historyView;
        private set => Set(ref _historyView, value);
    }

    /// <summary>Тип показания для форматирования значений истории.</summary>
    public SensorType? MetricType
    {
        get => _metricType;
        private set => Set(ref _metricType, value);
    }

    public bool HasValue
    {
        get => _hasValue;
        private set => Set(ref _hasValue, value);
    }

    public bool ShowGauge => Config.Visual == TileVisual.Gauge && HasValue;
    public bool ShowBar => Config.Visual == TileVisual.Bar && HasValue;
    public bool IsGraphOnly => Config.Visual == TileVisual.Graph;
    public bool ShowMissingValue => !HasValue && Config.Visual != TileVisual.Value;

    /// <summary>Показывать отдельный текстовый слой только у обычной Value-плитки.</summary>
    public bool ShowValue => Config.Visual == TileVisual.Value;

    /// <summary>Полоска истории под значением.</summary>
    public bool ShowSparkline => Config.ShowHistory && !IsGraphOnly;

    /// <summary>Обновление значений плитки по текущему снимку датчиков.</summary>
    public void Update(IReadOnlyList<SensorRow> rows, AppSettings settings)
    {
        MetricDefinition? metric = ResolveMetric(rows);

        if (metric is null)
        {
            LogResolveFailure(Config.MetricKey, rows, "metric-not-found");
            Title = string.IsNullOrWhiteSpace(Config.Title) ? "Метрика недоступна" : Config.Title;
            ValueText = "—";
            SubText = "нет данных";
            MetricType = null;
            HasValue = false;
            Progress = 0;
            Accent = Theme.TextFaint;
            ProgressBrush = Theme.TextFaint;
            NotifyVisualModes();
            _history.Add(float.NaN);
            History = WindowedHistory(settings);
            return;
        }

        double? value = metric.Resolve(rows);
        if (metric.Type == SensorType.Clock && metric.Group == SummaryBuilder.Cpu && value is <= 0)
            value = null;
        if (!string.Equals(_lastMetricKey, metric.Key, StringComparison.Ordinal))
        {
            _lastMetricKey = metric.Key;
            _lastResolvedValue = null;
        }

        // История уже подтверждает, что значение было получено. Не превращаем
        // плитку в «—» из-за одного пустого промежуточного снимка источника.
        value ??= _lastResolvedValue;
        Title = string.IsNullOrWhiteSpace(Config.Title) ? LocalizationManager.T(metric.Title) : Config.Title;
        MetricType = metric.Type;
        Unit = SensorCatalog.Unit(metric.Type);

        if (value is { } v && !double.IsNaN(v) && !double.IsInfinity(v))
        {
            Value = v;
            _lastResolvedValue = v;
            HasValue = true;
            ValueText = SensorCatalog.Format(metric.Type, v);
            _history.Add((float)v);
        }
        else
        {
            LogResolveFailure(metric.Key, rows, "no-value");
            HasValue = false;
            ValueText = "—";
            _history.Add(float.NaN);
        }

        double? scale = Config.Critical ?? Config.Warning ?? metric.Scale;
        Progress = scale is > 0 ? (value ?? 0) / scale.Value : 0;
        Accent = AccentFor(metric.Type, value, Config);
        ProgressBrush = Accent;
            SubText = LocalizationManager.T(metric.Group);
        NotifyVisualModes();
        History = WindowedHistory(settings);
    }

    public void RefreshLanguage(IReadOnlyList<SensorRow> rows, AppSettings settings) => Update(rows, settings);

    /// <summary>
    /// Возвращает только последние секунды истории: график показывает текущий период,
    /// а не накладывает новые значения на всю накопленную кривую.
    /// </summary>
    private float[] WindowedHistory(AppSettings settings)
    {
        float[] all = _history.ToArray();
        if (all.Length == 0) return all;
        if (settings.ChartWindowSeconds <= 0) return all;

        double intervalSeconds = Math.Max(0.2, settings.Interval.TotalSeconds);
        int windowPoints = (int)Math.Round(Math.Max(10, settings.ChartWindowSeconds) / intervalSeconds);
        windowPoints = Math.Clamp(windowPoints, 20, _history.Capacity);

        return all.Length <= windowPoints ? all : all[^windowPoints..];
    }

    /// <summary>Применяет выбранный период к уже накопленным точкам без их очистки.</summary>
    public void ApplyHistoryWindow(AppSettings settings) => History = WindowedHistory(settings);

    /// <summary>Текст метрики для диалога выбора.</summary>
    public static string DescribeMetric(string key) =>
        MetricCatalog.Find(key)?.Title ?? (MetricCatalog.IsSensorKey(key) ? key[MetricCatalog.SensorPrefix.Length..] : key);

    public void ResetHistory()
    {
        _history.Clear();
        History = Array.Empty<float>();
    }

    private MetricDefinition? ResolveMetric(IReadOnlyList<SensorRow> rows)
    {
        if (!MetricCatalog.IsSensorKey(Config.MetricKey)) return MetricCatalog.Find(Config.MetricKey);

        string id = MetricCatalog.SensorIdFromKey(Config.MetricKey);
        SensorRow? row = rows.FirstOrDefault(r => r.Id == id);
        return row is null ? null : MetricCatalog.ForSensor(row);
    }

    private void NotifyVisualModes()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowBar)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowGauge)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowMissingValue)));
    }

    private static void LogResolveFailure(string key, IReadOnlyList<SensorRow> rows, string reason)
    {
        lock (ResolveFailures)
        {
            if (!ResolveFailures.Add(key)) return;
        }

        StartupDiagnostics.Mark($"tile.metric.resolve key={key} result={reason} rows={rows.Count}");
    }

    private static Brush AccentFor(SensorType type, double? value, TileConfig config)
    {
        if (value is null) return Theme.TextFaint;

        double warning = config.Warning ?? 70;
        double critical = config.Critical ?? 85;

        if (type == SensorType.Temperature)
            return value >= critical ? Theme.Bad : value >= warning ? Theme.Warn : Theme.Good;

        if (type is SensorType.Load or SensorType.Level or SensorType.Control)
            return value >= 90 ? Theme.Bad : value >= 70 ? Theme.Warn : Theme.Accent;

        return Theme.Accent;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
