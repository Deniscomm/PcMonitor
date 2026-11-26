using System.Collections.ObjectModel;
using System.Globalization;
using System.Diagnostics;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;
using PcMonitor.Core.Stress;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PcMonitor.Core;
using PcMonitor.Core.Gpu;

namespace PcMonitor.Views;

/// <summary>Вкладка GPU-нагрузки: стресс-тест с поиском артефактов и бенчмарк частоты кадров.</summary>
public partial class GpuTestView : UserControl
{
    private readonly ObservableCollection<GpuTestRun> _runs = new();
    private readonly List<float> _temperatureHistory = new();
    private readonly List<float> _fpsHistory = new();

    private MonitorContext? _context;
    private CancellationTokenSource? _cts;
    private bool _surfaceRunning;
    private string? _stopReason;
    private StressThermalGuard? _thermalGuard;
    private DispatcherTimer? _safetyTimer;

    public GpuTestView()
    {
        InitializeComponent();
        RunsGrid.ItemsSource = _runs;
        // Surface автоматически подписывается на Rendering в своём Loaded; сразу гасим
        // эту подписку, если пользователь ещё не запустил тест.
        Surface.Loaded += (_, _) => { if (!_surfaceRunning) Surface.Stop(); };
        Unloaded += (_, _) => CancelOperation("Остановлено при закрытии вкладки");

        Surface.FpsUpdated += fps =>
        {
            FpsText.Text = $"FPS: {fps:0.0}";
            if (_cts is not null)
            {
                _fpsHistory.Add((float)fps);
                Trim(_fpsHistory);
                FpsChart.Values = _fpsHistory.ToArray();
            }
        };

        foreach (GpuTestRun run in GpuTestStore.Load())
            _runs.Add(run);

        UpdateRunSummary();
    }

    public event Action<string>? StatusChanged;

    public void ApplyLanguage(string language)
    {
        LocalizationManager.Apply(this, language);
        InitDirect3D();
        RunsGrid.Items.Refresh();
        UpdateRunSummary();
    }

    public void Attach(MonitorContext context)
    {
        _context = context;
        context.Updated += OnSensorsUpdated;
        UpdateRunSummary();
        InitDirect3D();
    }

    /// <summary>Заполняет список сцен и определяет адаптер для Direct3D-бенчмарка.</summary>
    private void InitDirect3D()
    {
        try
        {
            var scenes = new List<string> { LocalizationManager.T("Все сцены (итоговый балл)") };
            scenes.AddRange(GpuEngine.Scenes.Select(s => LocalizationManager.T(s.Title)));
            D3DSceneBox.ItemsSource = scenes;
            D3DSceneBox.SelectedIndex = 0;

            IReadOnlyList<GpuAdapterInfo> adapters = GpuEngine.EnumerateAdapters();
            AdapterText.Text = adapters.Count > 0
                ? $"{adapters[0].Name} · {LocalizationManager.T("рендер без вертикальной синхронизации, монитор ни при чём")}"
                : LocalizationManager.T("адаптер не определён");
        }
        catch (Exception ex)
        {
            AdapterText.Text = "Direct3D недоступен: " + ex.Message;
        }
    }

    private GpuPreset SelectedPreset() => D3DPresetBox.SelectedIndex switch
    {
        0 => GpuPreset.Low,
        2 => GpuPreset.High,
        3 => GpuPreset.Extreme,
        _ => GpuPreset.Medium,
    };

    private async void RunD3DBenchmark_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null || _context is null || !CanStart(false)) return;

        GpuPreset preset = SelectedPreset();
        int sceneIndex = D3DSceneBox.SelectedIndex;
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        BeginSafety();

        var progress = new Progress<GpuTestProgress>(p =>
        {
            D3DProgress.Value = Math.Clamp(p.Percent / 100.0, 0, 1);
            D3DStatusText.Text = $"{p.Stage}: {p.Value:0.0} {p.ValueUnit} · {p.Status}";
        });

        D3DStatusText.Text = "Подготовка Direct3D…";

        try
        {
            (GpuTestResult result, string adapter) = await Task.Run(() =>
            {
                using var engine = new GpuEngine(0);
                GpuTestResult run = sceneIndex <= 0
                    ? engine.RunBenchmark(preset, TimeSpan.FromSeconds(10), progress, token)
                    : engine.RunRasterTest(GpuEngine.Scenes[sceneIndex - 1].Name, preset,
                        TimeSpan.FromSeconds(15), progress, token);

                return (run, engine.Adapter.Name);
            }, token);

            var entry = new GpuTestRun
            {
                Mode = $"Direct3D · {preset}",
                Adapter = adapter,
                LoadLevel = (int)preset + 1,
                DurationSeconds = result.Duration.TotalSeconds,
                FpsAverage = result.AverageFps,
                FpsMinimum = result.MinimumFps,
                FpsMaximum = result.MaximumFps,
                Score = result.Score,
                Artifacts = result.Errors,
                Passed = result.Errors == 0,
                Summary = result.Details,
            };

            _runs.Insert(0, entry);
            GpuTestStore.Save(_runs);
            UpdateRunSummary();

            D3DProgress.Value = 1;
            D3DStatusText.Text = $"{result.TestName}: {result.AverageFps:0.0} FPS в среднем, " +
                                 $"минимум {result.MinimumFps:0.0}, максимум {result.MaximumFps:0.0}, " +
                                 $"балл {result.Score} ({result.ResolutionText})";
            StatusChanged?.Invoke($"Direct3D-бенчмарк: балл {result.Score}");
        }
        catch (OperationCanceledException)
        {
            D3DStatusText.Text = "Прогон отменён.";
        }
        catch (Exception ex)
        {
            D3DStatusText.Text = "Ошибка Direct3D: " + ex.Message;
        }
        finally
        {
            EndSafety();
            _cts.Dispose();
            _cts = null;
        }
    }

    private async void RunD3DStability_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is not null || _context is null || !CanStart(false)) return;

        GpuPreset preset = SelectedPreset();
        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;
        BeginSafety();
        D3DStatusText.Text = "Проверка стабильности: ищу расхождения кадров…";

        try
        {
            (long errors, string adapter) = await Task.Run(() =>
            {
                using var engine = new GpuEngine(0);
                long found = engine.RunArtifactCheck("raymarch", preset, checks: 12,
                    TimeSpan.FromSeconds(30), null, token);
                return (found, engine.Adapter.Name);
            }, token);

            var entry = new GpuTestRun
            {
                Mode = $"Direct3D стабильность · {preset}",
                Adapter = adapter,
                LoadLevel = (int)preset + 1,
                Artifacts = errors,
                Passed = errors == 0,
                Score = 0,
                Summary = errors == 0 ? "Расхождений кадров не найдено" : $"Расхождений: {errors}",
            };

            _runs.Insert(0, entry);
            GpuTestStore.Save(_runs);
            UpdateRunSummary();

            D3DStatusText.Text = errors == 0
                ? "Стабильность в норме: эталонный кадр повторяется без расхождений."
                : $"Обнаружены расхождения кадров: {errors}. Это признак нестабильности видеокарты.";
            StatusChanged?.Invoke(errors == 0 ? "Проверка стабильности GPU пройдена" : $"Сбои GPU: {errors}");
        }
        catch (OperationCanceledException)
        {
            D3DStatusText.Text = "Проверка отменена.";
        }
        catch (Exception ex)
        {
            D3DStatusText.Text = "Ошибка Direct3D: " + ex.Message;
        }
        finally
        {
            EndSafety();
            _cts.Dispose();
            _cts = null;
        }
    }

    private void OnSensorsUpdated()
    {
        if (_context is null) return;
        CheckThermals();

        List<SensorRow> rows = _context.MeasurementRows();
        double? temperature = MetricCatalog.Find(MetricCatalog.GpuTemperature)?.Resolve(rows);
        double? power = MetricCatalog.Find(MetricCatalog.GpuPower)?.Resolve(rows);
        double? clock = MetricCatalog.Find(MetricCatalog.GpuClock)?.Resolve(rows);

        GpuTempText.Text = temperature.HasValue ? $"GPU: {temperature.Value:0.0} °C" : "GPU: —";
        GpuExtraText.Text = $"{(power.HasValue ? $"{power.Value:0.0} Вт" : "—")} · " +
                            $"{(clock.HasValue ? $"{clock.Value:0} МГц" : "—")} · " +
                            $"{(temperature.HasValue ? $"{temperature.Value:0.0} °C" : "—")}";

        if (_cts is null) return;

        if (temperature.HasValue)
        {
            _temperatureHistory.Add((float)temperature.Value);
            Trim(_temperatureHistory);
            TempChart.Values = _temperatureHistory.ToArray();
        }
    }

    private static void Trim(List<float> list)
    {
        const int max = 600;
        if (list.Count > max) list.RemoveRange(0, list.Count - max);
    }

    private void Load_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Surface is not null) Surface.LoadLevel = (int)e.NewValue;
    }

    private double StageSeconds()
    {
        if (StageBox.SelectedItem is ComboBoxItem { Tag: string tag } && double.TryParse(tag, out double value))
            return value;

        return 10;
    }

    private async void StartStress_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("Стресс-тест", stages: 30, stageSeconds: StageSeconds(), untilStopped: InfiniteBox.IsChecked == true);

    private async void StartBenchmark_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("Бенчмарк", stages: 3, stageSeconds: 20);

    private void Stop_Click(object sender, RoutedEventArgs e) => CancelOperation("Остановлено пользователем");

    private void CancelOperation(string reason)
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        _stopReason = reason;
        Surface.Stop();
        _surfaceRunning = false;
        _cts.Cancel();
        ProgressText.Text = D3DStatusText.Text = reason + "; завершение…";
    }

    private (double? Cpu, double? Gpu, bool Fresh) ReadThermals()
    {
        if (_context is null) return (null, null, false);
        double age = (DateTime.Now - _context.LastUpdate).TotalSeconds;
        bool fresh = _context.LastUpdate != default && age >= -2 && age <= 15;
        double? cpu = null, gpu = null;
        if (fresh)
            foreach (SensorRow row in _context.Rows)
            {
                if (row.Type != SensorType.Temperature || row.IsThreshold || row.DisplayValue is not { } value ||
                    !float.IsFinite(value) || value <= 0 || value > 200) continue;
                if (row.HardwareType == HardwareType.Cpu) cpu = Math.Max(cpu ?? value, value);
                else if (row.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                    gpu = Math.Max(gpu ?? value, value);
            }
        return (cpu, gpu, fresh);
    }

    private bool CanStart(bool untilStopped)
    {
        var readings = ReadThermals();
        if (readings.Cpu >= StressThermalGuard.CpuLimit || readings.Gpu >= StressThermalGuard.GpuLimit)
        {
            ProgressText.Text = "Запуск запрещён: достигнут температурный предел CPU 95 °C / GPU 85 °C.";
            return false;
        }
        if (untilStopped && !readings.Gpu.HasValue)
        {
            ProgressText.Text = "Бессрочный GPU-тест требует свежего датчика температуры GPU. Термозащита недоступна.";
            return false;
        }
        if (!readings.Gpu.HasValue && MessageBox.Show(Window.GetWindow(this),
            "Свежий датчик температуры GPU недоступен. Термозащита GPU не работает. Запустить ограниченный тест?",
            "GPU-нагрузка", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return false;
        return true;
    }

    private void BeginSafety()
    {
        _stopReason = null;
        _thermalGuard = new StressThermalGuard();
        var readings = ReadThermals();
        _thermalGuard.Observe(readings.Cpu, readings.Gpu, readings.Fresh);
        _safetyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _safetyTimer.Tick += SafetyTimer_Tick;
        _safetyTimer.Start();
    }

    private void SafetyTimer_Tick(object? sender, EventArgs e) => CheckThermals();
    private void CheckThermals()
    {
        if (_cts is null || _cts.IsCancellationRequested || _thermalGuard is null) return;
        var readings = ReadThermals();
        if (_thermalGuard.Observe(readings.Cpu, readings.Gpu, readings.Fresh) is { } reason) CancelOperation(reason);
    }

    private void EndSafety()
    {
        if (_safetyTimer is not null)
        {
            _safetyTimer.Stop();
            _safetyTimer.Tick -= SafetyTimer_Tick;
            _safetyTimer = null;
        }
        _thermalGuard = null;
    }

    private async Task RunAsync(string mode, int stages, double stageSeconds, bool untilStopped = false)
    {
        if (_cts is not null || _context is null || !CanStart(untilStopped)) return;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        CancellationToken token = cts.Token;
        var runWatch = Stopwatch.StartNew();
        var run = new GpuTestRun
        {
            Mode = untilStopped ? mode + " · до остановки" : mode,
            Adapter = _context.Rows.FirstOrDefault(r => r.HardwareGroup == SummaryBuilder.Gpu)?.Hardware ?? "GPU",
            LoadLevel = (int)LoadSlider.Value,
            RunUntilStopped = untilStopped,
        };
        double fpsSum = 0, fpsMin = double.MaxValue, fpsMax = 0;
        long fpsSamples = 0, temperatureSamples = 0;
        double temperatureSum = 0, temperatureMax = 0, powerMax = 0, clockMax = 0;
        long artifacts = 0;
        string? failure = null;
        double totalSeconds = stages * stageSeconds;
        BeginSafety();
        InfiniteBox.IsEnabled = LoadSlider.IsEnabled = StageBox.IsEnabled = false;
        Progress.IsIndeterminate = untilStopped;
        _temperatureHistory.Clear();
        _fpsHistory.Clear();
        LogText.Text = untilStopped ? mode + ": непрерывная нагрузка до «Стоп»" : $"{mode}: {stages} этапов по {stageSeconds:0} с";
        try
        {
            Surface.LoadLevel = run.LoadLevel;
            byte[] golden = Surface.CaptureDeterministicFrame();
            _surfaceRunning = true;
            Surface.Start();
            for (long stage = 0; (untilStopped || stage < stages) && !token.IsCancellationRequested; stage++)
            {
                var stageWatch = Stopwatch.StartNew();
                double stageFpsSum = 0, stageFpsMin = double.MaxValue, stageTempMax = 0, stagePowerMax = 0;
                long stageSamples = 0;
                while (StressRunPolicy.ShouldContinue(false, stageWatch.Elapsed, TimeSpan.FromSeconds(stageSeconds), token))
                {
                    await Task.Delay(500, token);
                    double fps = Surface.Fps;
                    if (fps > 0)
                    {
                        stageFpsSum += fps;
                        stageSamples++;
                        stageFpsMin = Math.Min(stageFpsMin, fps);
                        fpsSum += fps;
                        fpsSamples++;
                        fpsMin = Math.Min(fpsMin, fps);
                        fpsMax = Math.Max(fpsMax, fps);
                    }
                    List<SensorRow> rows = _context.MeasurementRows();
                    double? temperature = MetricCatalog.Find(MetricCatalog.GpuTemperature)?.Resolve(rows);
                    double? power = MetricCatalog.Find(MetricCatalog.GpuPower)?.Resolve(rows);
                    double? clock = MetricCatalog.Find(MetricCatalog.GpuClock)?.Resolve(rows);
                    if (temperature.HasValue)
                    {
                        stageTempMax = Math.Max(stageTempMax, temperature.Value);
                        temperatureMax = Math.Max(temperatureMax, temperature.Value);
                        temperatureSum += temperature.Value;
                        temperatureSamples++;
                    }
                    if (power.HasValue)
                    {
                        stagePowerMax = Math.Max(stagePowerMax, power.Value);
                        powerMax = Math.Max(powerMax, power.Value);
                    }
                    if (clock.HasValue) clockMax = Math.Max(clockMax, clock.Value);
                    if (!untilStopped) Progress.Value = Math.Clamp(runWatch.Elapsed.TotalSeconds / totalSeconds, 0, 1);
                    string elapsed = StressRunPolicy.FormatElapsed(runWatch.Elapsed);
                    ProgressText.Text = $"{(untilStopped ? "До остановки" : $"Этап {stage + 1}/{stages}")} · прошло {elapsed} · FPS {(stageSamples > 0 ? stageFpsSum / stageSamples : 0):0.0} · макс. {stageTempMax:0.0} °C · артефактов {artifacts}";
                    OverlayText.Text = $"{run.Mode} · {elapsed}";
                }
                token.ThrowIfCancellationRequested();
                long artifactPixels = CompareFrames(golden, Surface.CaptureDeterministicFrame());
                artifacts += artifactPixels;
                double stageAverageFps = stageSamples > 0 ? stageFpsSum / stageSamples : 0;
                run.Stages.Add(new GpuStageResult((int)Math.Min(stage + 1, int.MaxValue), stageAverageFps,
                    stageFpsMin == double.MaxValue ? 0 : stageFpsMin, stageTempMax, stagePowerMax, artifactPixels, stageWatch.Elapsed));
                // Ограничиваем только подробную историю, не длительность или суммарные счётчики.
                if (run.Stages.Count > 600) run.Stages.RemoveAt(0);
                StageTempText.Text = $"температура: макс {stageTempMax:0.0} °C · артефактов {artifacts}";
                StageFpsText.Text = $"частота кадров: этап {stageAverageFps:0.0} FPS · средняя {(fpsSamples > 0 ? fpsSum / fpsSamples : 0):0.0} FPS";
                LogText.Text = $"Проверка {stage + 1}: {stageAverageFps:0.0} FPS, макс {stageTempMax:0.0} °C, расхождений {artifactPixels}";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex.Message; }
        finally
        {
            // Сначала полностью снимаем нагрузку и обработчики, затем сохраняем отчёт.
            Surface.Stop();
            _surfaceRunning = false;
            runWatch.Stop();
            EndSafety();
            _cts = null;
            InfiniteBox.IsEnabled = LoadSlider.IsEnabled = StageBox.IsEnabled = true;
            Progress.IsIndeterminate = false;
            run.DurationSeconds = runWatch.Elapsed.TotalSeconds;
            run.Cancelled = token.IsCancellationRequested;
            run.ThermalStopped = _stopReason?.StartsWith("Термостоп") == true || _stopReason?.StartsWith("Защитная") == true;
            run.FpsAverage = fpsSamples > 0 ? fpsSum / fpsSamples : 0;
            run.FpsMinimum = fpsMin == double.MaxValue ? 0 : fpsMin;
            run.FpsMaximum = fpsMax;
            run.TemperatureMaximum = temperatureMax;
            run.TemperatureAverage = temperatureSamples > 0 ? temperatureSum / temperatureSamples : 0;
            run.PowerMaximum = powerMax;
            run.ClockMaximum = clockMax;
            run.Artifacts = artifacts;
            run.Score = (int)Math.Round(run.FpsAverage * 100);
            run.Passed = !run.Cancelled && failure is null && artifacts == 0;
            run.Summary = failure is not null ? "Ошибка: " + failure : run.Cancelled ? _stopReason ?? "Остановлено" :
                artifacts == 0 ? $"Сбоев не обнаружено, максимум {temperatureMax:0.0} °C" : $"Расхождения кадров: {artifacts} пикселей";
            _runs.Insert(0, run);
            GpuTestStore.Save(_runs);
            Progress.Value = 0;
            ProgressText.Text = $"{run.Summary} · прошло {run.DurationText}";
            OverlayText.Text = "тест не запущен";
            LogText.Text = $"{run.Mode}: {run.ResultText} · {run.DurationText} · FPS {run.FpsAverage:0.0} · макс. {run.TemperatureMaximum:0.0} °C · артефактов {run.Artifacts}";
            StatusChanged?.Invoke($"{run.Mode}: {run.Summary}");
        }
    }

    /// <summary>Сравнение кадра с эталоном: сколько пикселей отличается заметно.</summary>
    private static long CompareFrames(byte[] reference, byte[] current, int tolerance = 12)
    {
        if (reference.Length != current.Length || reference.Length == 0) return 0;

        long different = 0;
        for (int i = 0; i < reference.Length; i += 4)
        {
            if (Math.Abs(reference[i] - current[i]) > tolerance ||
                Math.Abs(reference[i + 1] - current[i + 1]) > tolerance ||
                Math.Abs(reference[i + 2] - current[i + 2]) > tolerance)
            {
                different++;
            }
        }

        return different;
    }

    private void UpdateRunSummary()
    {
        if (_runs.Count == 0)
        {
            LogText.Text = "История пуста. Запустите стресс-тест или бенчмарк.";
            return;
        }

        double best = _runs.Where(r => r.Score > 0).Select(r => (double)r.Score).DefaultIfEmpty(0).Max();
        double maxTemp = _runs.Select(r => r.TemperatureMaximum).DefaultIfEmpty(0).Max();
        LogText.Text = $"Прогонов: {_runs.Count} · лучшая оценка {best:0} · максимальная температура за всё время {maxTemp:0.0} °C";
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _runs.Clear();
        GpuTestStore.Save(_runs);
        UpdateRunSummary();
    }

    private void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Отчёт по GPU-тестам",
            FileName = $"gpu-tests-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Filter = "Текстовый отчёт (*.txt)|*.txt|CSV (*.csv)|*.csv",
            InitialDirectory = AppSettings.ReportsDirectory,
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("Отчёт по GPU-тестам — Мониторинг ПК");
            builder.AppendLine($"Сформирован: {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
            builder.AppendLine();

            foreach (GpuTestRun run in _runs)
            {
                builder.AppendLine($"{run.StartedText} | {run.Mode} | {run.Adapter} | нагрузка {run.LoadLevel}/5");
                builder.AppendLine($"  длительность {run.DurationText}, средний FPS {run.FpsAverage:0.0}, " +
                                   $"минимум {run.FpsMinimum:0.0}, максимум {run.FpsMaximum:0.0}");
                builder.AppendLine($"  температура: макс {run.TemperatureMaximum:0.0} °C, средняя {run.TemperatureAverage:0.0} °C, " +
                                   $"мощность до {run.PowerMaximum:0.0} Вт, частота до {run.ClockMaximum:0} МГц");
                builder.AppendLine($"  артефактов: {run.Artifacts}, оценка {run.Score}, итог: {run.ResultText}");
                builder.AppendLine();
            }

            File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(true));
            StatusChanged?.Invoke($"Отчёт сохранён: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "Не удалось сохранить отчёт:\n" + ex.Message,
                "Мониторинг ПК", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
