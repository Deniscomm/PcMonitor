using System.Diagnostics;
using System.Windows;
using PcMonitor.Core;
using PcMonitor.Core.Testing;

namespace PcMonitor.Views.Dialogs;

/// <summary>
/// Окно графической нагрузки для авто-теста: показывает сцену, счётчик кадров
/// и найденные артефакты, пока идёт фаза теста видеокарты.
/// </summary>
public partial class GpuLoadWindow : Window
{
    private readonly MonitorContext _context;
    private byte[]? _goldenFrame;
    private long _artifactPixels;
    private double _minFps = double.MaxValue;
    private double _maxFps;
    private double _fpsSum;
    private int _fpsSamples;
    private DateTime _deadline;

    public GpuLoadWindow(MonitorContext context)
    {
        InitializeComponent();
        LocalizationManager.Apply(this, LocalizationManager.Language);
        _context = context;

        Surface.LoadLevel = 8;
        Surface.FpsUpdated += OnFpsUpdated;
        _context.Updated += OnSensorsUpdated;
        Closed += (_, _) =>
        {
            Surface.FpsUpdated -= OnFpsUpdated;
            _context.Updated -= OnSensorsUpdated;
        };
    }

    /// <summary>Итоговое описание для отчёта.</summary>
    public string Details { get; private set; } = string.Empty;

    /// <summary>Найденные расхождения кадров.</summary>
    public long ArtifactErrors => _artifactPixels;

    /// <summary>Запускает нагрузку на указанное время (блокирует поток до окончания).</summary>
    public void RunFor(TimeSpan duration, CancellationToken token)
    {
        _deadline = DateTime.UtcNow.Add(duration);
        _goldenFrame = null;
        _artifactPixels = 0;
        _minFps = double.MaxValue;
        _maxFps = 0;
        _fpsSum = 0;
        _fpsSamples = 0;

        var stopwatch = Stopwatch.StartNew();
        while (DateTime.UtcNow < _deadline && !token.IsCancellationRequested)
        {
            Thread.Sleep(200);

            if (_goldenFrame is null && stopwatch.Elapsed.TotalSeconds > 3)
                _goldenFrame = Surface.CaptureDeterministicFrame(128);

            int remaining = (int)Math.Max(0, (_deadline - DateTime.UtcNow).TotalSeconds);
            Dispatcher.BeginInvoke(() => RemainingText.Text = $"осталось {remaining} с");

            if (stopwatch.Elapsed.TotalSeconds > 5 && (int)stopwatch.Elapsed.TotalSeconds % 5 == 0)
                CheckArtifacts();
        }

        CheckArtifacts();

        double average = _fpsSamples > 0 ? _fpsSum / _fpsSamples : 0;
        Details = $"средний FPS {average:0.0} (мин {(_minFps == double.MaxValue ? 0 : _minFps):0.0}, " +
                  $"макс {_maxFps:0.0}), артефактов {_artifactPixels}";
    }

    private void CheckArtifacts()
    {
        if (_goldenFrame is null) return;

        byte[] current = Surface.CaptureDeterministicFrame(128);
        if (current.Length != _goldenFrame.Length) return;

        long different = 0;
        for (int i = 0; i < _goldenFrame.Length; i += 4)
        {
            if (Math.Abs(_goldenFrame[i] - current[i]) > 12 ||
                Math.Abs(_goldenFrame[i + 1] - current[i + 1]) > 12 ||
                Math.Abs(_goldenFrame[i + 2] - current[i + 2]) > 12)
            {
                different++;
            }
        }

        _artifactPixels = Math.Max(_artifactPixels, different);
    }

    private void OnFpsUpdated(double fps)
    {
        if (fps <= 0) return;

        _fpsSum += fps;
        _fpsSamples++;
        _minFps = Math.Min(_minFps, fps);
        _maxFps = Math.Max(_maxFps, fps);
        StatsText.Text = $"FPS {fps:0.0} · артефактов {_artifactPixels}";
    }

    private void OnSensorsUpdated()
    {
        double? temperature = MetricCatalog.Find(MetricCatalog.GpuTemperature)?.Resolve(_context.MeasurementRows());
        double? power = MetricCatalog.Find(MetricCatalog.GpuPower)?.Resolve(_context.MeasurementRows());
        double? clock = MetricCatalog.Find(MetricCatalog.GpuClock)?.Resolve(_context.MeasurementRows());

        PhaseText.Text = temperature.HasValue ? $"Видеокарта: {temperature.Value:0.0} °C" : "Нагрузка на видеокарту";
        StatsText.Text = $"{(power.HasValue ? $"{power.Value:0.0} Вт" : "—")} · " +
                         $"{(clock.HasValue ? $"{clock.Value:0} МГц" : "—")} · артефактов {_artifactPixels}";
    }

    private void Abort_Click(object sender, RoutedEventArgs e) => _deadline = DateTime.UtcNow;
}
