using System.Collections.ObjectModel;
using System.Globalization;
using System.Diagnostics;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PcMonitor.Core;
using PcMonitor.Core.Stress;

namespace PcMonitor.Views;

/// <summary>Строка результата стресс-теста для таблицы.</summary>
public sealed class StressResultRow
{
    public StressResultRow(StressTestResult result, StressCategory category)
    {
        TestName = result.TestName;
        CategoryText = CategoryName(category);
        StageCount = result.Stages.Count;
        TotalErrors = result.TotalErrors;
        ThroughputText = result.Stages.Count > 0 ? $"{result.AverageThroughput:0.0}" : "—";
        Duration = result.Duration;
        VerdictText = result.Cancelled ? "остановлено" : result.Passed ? "успешно" : "ОШИБКИ";
        Summary = result.Summary;
        StartedText = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");
    }

    public string StartedText { get; }
    public string TestName { get; }
    public string CategoryText { get; }
    public int StageCount { get; }
    public long TotalErrors { get; }
    public string ThroughputText { get; }
    public TimeSpan Duration { get; }
    public string DurationText => StressRunPolicy.FormatElapsed(Duration);
    public string VerdictText { get; }
    public string Summary { get; }

    public static string CategoryName(StressCategory category) => category switch
    {
        StressCategory.Cpu => LocalizationManager.T("Процессор"),
        StressCategory.Memory => LocalizationManager.T("Память"),
        StressCategory.Cache => LocalizationManager.T("Кэш"),
        StressCategory.Disk => LocalizationManager.T("Диск"),
        StressCategory.Gpu => LocalizationManager.T("Видеокарта"),
        _ => category.ToString(),
    };
}

internal sealed class LocalizedStressTest : IStressTest
{
    private readonly IStressTest _inner;
    public LocalizedStressTest(IStressTest inner) => _inner = inner;
    public string Id => _inner.Id;
    public string Name => LocalizationManager.StressText($"stress.{_inner.Id}.name", _inner.Name);
    public string Description => LocalizationManager.StressText($"stress.{_inner.Id}.description", _inner.Description);
    public StressCategory Category => _inner.Category;
    public int StageCount => _inner.StageCount;
    public TimeSpan StageDuration => _inner.StageDuration;
    public string ThroughputUnit => _inner.ThroughputUnit;
    public StressStageResult RunStage(int stage, StressOptions options, CancellationToken token, IProgress<StressProgress>? progress) =>
        _inner.RunStage(stage, options, token, progress);
}

/// <summary>Вкладка «Тесты и бенчмарки»: стресс-тесты, GPU-нагрузка и бенчмарки.</summary>
public partial class StressView : UserControl
{
    private readonly ObservableCollection<StressResultRow> _results = new();
    private readonly List<float> _temperatureHistory = new();
    private readonly List<float> _throughputHistory = new();

    private MonitorContext? _context;
    private CancellationTokenSource? _cts;
    private IStressTest? _selected;
    private bool _ready;
    private string? _stopReason;
    private StressThermalGuard? _thermalGuard;

    public StressView()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _results;
        LoadResults();
        Unloaded += (_, _) => CancelRun("Остановлено при закрытии вкладки");
    }

    public event Action<string>? StatusChanged;

    public void ApplyLanguage(string language)
    {
        CategoryBox.Items.Refresh();
        TestList.Items.Refresh();
        ResultsGrid.Items.Refresh();
        LocalizationManager.Apply(this, language);
        GpuTest.ApplyLanguage(language);
        Benchmarks.ApplyLanguage(language);
    }

    public void Attach(MonitorContext context)
    {
        _context = context;
        GpuTest.Attach(context);
        Benchmarks.Attach(context);
        context.Updated += OnSensorsUpdated;

        ThreadsBox.Text = "0";
        MemoryBox.Text = context.Settings.StressMemoryMegabytes.ToString(CultureInfo.CurrentCulture);
        DiskBox.Text = context.Settings.StressDiskMegabytes.ToString(CultureInfo.CurrentCulture);

        InnerTabs.SelectedIndex = Math.Clamp(context.Settings.LastStressTab, 0, InnerTabs.Items.Count - 1);
        _ready = true;
        Reload();
    }

    private void OnSensorsUpdated()
    {
        if (_context is null) return;
        CheckThermals();

        double? temperature = MetricCatalog.Find(MetricCatalog.CpuTemperature)?.Resolve(_context.MeasurementRows());
        if (temperature.HasValue)
        {
            TempText.Text = LocalizationManager.T($"температура процессора: {temperature.Value:0.0} °C");
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

    // ============================ Каталог ============================

    private void Reload()
    {
        var categories = new List<CategoryOption> { new("Все категории", null) };
        foreach (StressCategory category in Enum.GetValues<StressCategory>())
        {
            int count = StressTestCatalog.ByCategory(category).Count;
            if (count > 0) categories.Add(new CategoryOption($"{StressResultRow.CategoryName(category)} ({count})", category));
        }

        CategoryBox.ItemsSource = categories;
        CategoryBox.SelectedIndex = 0;

        int total = StressTestCatalog.All.Count;
        TestCountText.Text = LocalizationManager.T($"всего {total} тестов");
        ShowTests(null);
    }

    private void Category_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ShowTests((CategoryBox.SelectedItem as CategoryOption)?.Category);
    }

    private void ShowTests(StressCategory? category)
    {
        List<IStressTest> tests = (category is null
            ? StressTestCatalog.All
            : StressTestCatalog.ByCategory(category.Value))
            .Select(static test => (IStressTest)new LocalizedStressTest(test))
            .ToList();

        TestList.ItemsSource = tests;
        TestList.DisplayMemberPath = "Name";
        if (tests.Count > 0) TestList.SelectedIndex = 0;
    }

    private void TestList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = TestList.SelectedItem as IStressTest;
        if (_selected is null) return;

        TitleText.Text = $"{_selected.Name}  ·  {StressResultRow.CategoryName(_selected.Category)}";
        if (InfiniteBox.IsChecked == true)
        {
            DescriptionText.Text = _selected.Description + "\n\n" + LocalizationManager.T("Непрерывная нагрузка без дедлайна, до «Стоп» или защитной остановки.");
            return;
        }
        DescriptionText.Text = $"{_selected.Description}\n\n{LocalizationManager.T("Этапов: ")}{_selected.StageCount}, " +
                               $"{LocalizationManager.T("длительность этапа: ")}{_selected.StageDuration.TotalSeconds:0.#} с. " +
                               $"{LocalizationManager.T("Ожидаемое время прогона: ")}{_selected.StageCount * _selected.StageDuration.TotalSeconds / 60:0.#} мин.";
    }

    // ============================ Запуск ============================

    private async void RunTest_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            Log("Сначала выберите тест в каталоге.");
            return;
        }

        await RunAsync(new[] { _selected });
    }

    private async void RunCategory_Click(object sender, RoutedEventArgs e)
    {
        StressCategory? category = (CategoryBox.SelectedItem as CategoryOption)?.Category;
        List<IStressTest> tests = category is null
            ? StressTestCatalog.All.ToList()
            : StressTestCatalog.ByCategory(category.Value).ToList();

        if (tests.Count == 0)
        {
            Log("В этой категории нет тестов.");
            return;
        }

        await RunAsync(tests);
    }

    private async void RunAll_Click(object sender, RoutedEventArgs e) => await RunAsync(StressTestCatalog.All.ToList());

    private void Duration_Changed(object sender, RoutedEventArgs e)
    {
        if (RunCategoryButton is null || RunAllButton is null) return;
        RunCategoryButton.IsEnabled = RunAllButton.IsEnabled = InfiniteBox.IsChecked != true;
        if (_selected is not null) TestList_SelectionChanged(sender, null!);
    }

    public void StopFromHotKey() => CancelRun("Остановлено горячей клавишей");

    private void Stop_Click(object sender, RoutedEventArgs e) => CancelRun("Остановлено пользователем");

    private void CancelRun(string reason)
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        _stopReason = reason;
        _cts.Cancel();
        ProgressText.Text = reason + "; завершение рабочих потоков…";
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

    private void CheckThermals()
    {
        if (_cts is null || _cts.IsCancellationRequested || _thermalGuard is null) return;
        var readings = ReadThermals();
        if (_thermalGuard.Observe(readings.Cpu, readings.Gpu, readings.Fresh) is { } reason) CancelRun(reason);
    }

    private async Task RunAsync(IReadOnlyList<IStressTest> tests)
    {
        if (_cts is not null || _context is null) return;
        bool infinite = InfiniteBox.IsChecked == true;
        if (infinite && tests.Count != 1)
        {
            Log("В бессрочном режиме выберите один тест и нажмите «Запустить тест».");
            return;
        }
        if (tests.Any(t => t.Category == StressCategory.Disk))
        {
            if (DiskConsentBox.IsChecked != true)
            {
                Log("Дисковые тесты, включая тесты чтения, записывают временный файл. Сначала разрешите запись.");
                return;
            }
            if (MessageBox.Show(Window.GetWindow(this),
                infinite ? "Бессрочная нагрузка непрерывно читает/перезаписывает ограниченный временный файл до «Стоп». Запись увеличивает износ SSD. Продолжить?"
                         : "Дисковые тесты записывают временные файлы и увеличивают износ SSD. Продолжить?",
                "Дисковая нагрузка", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
        }
        var initial = ReadThermals();
        if (initial.Cpu >= StressThermalGuard.CpuLimit || initial.Gpu >= StressThermalGuard.GpuLimit)
        {
            Log("Запуск запрещён: температура уже достигла предела 95 °C CPU / 85 °C GPU.");
            return;
        }
        if (infinite && tests[0].Category != StressCategory.Disk && !initial.Cpu.HasValue)
        {
            Log("Бессрочный CPU/RAM/кэш-тест требует свежего датчика температуры CPU. Термозащита недоступна.");
            return;
        }
        if (!initial.Cpu.HasValue) Log("Предупреждение: температура CPU недоступна; термозащита CPU не работает.");

        using var cts = new CancellationTokenSource();
        _cts = cts;
        CancellationToken token = cts.Token;
        _stopReason = null;
        _thermalGuard = new StressThermalGuard();
        _thermalGuard.Observe(initial.Cpu, initial.Gpu, initial.Fresh);
        AppSettings settings = _context.Settings;
        int threads = ParseInt(ThreadsBox.Text, 0, 0, 256);
        int memoryMb = ParseInt(MemoryBox.Text, settings.StressMemoryMegabytes, 64, 65536);
        int diskMb = ParseInt(DiskBox.Text, settings.StressDiskMegabytes, 16, 65536);
        var options = new StressOptions
        {
            Threads = threads == 0 ? null : threads,
            RunUntilStopped = infinite,
            MemoryMegabytes = memoryMb,
            DiskPath = settings.StressDiskPath,
            DiskBytes = (long)diskMb * 1024 * 1024,
            Seed = Environment.TickCount,
        };
        InfiniteBox.IsEnabled = DiskConsentBox.IsEnabled = false;
        ThreadsBox.IsEnabled = MemoryBox.IsEnabled = DiskBox.IsEnabled = false;
        Progress.IsIndeterminate = infinite;
        _temperatureHistory.Clear();
        _throughputHistory.Clear();
        LogBox.Clear();
        Log($"Старт: тестов {tests.Count}, {(infinite ? "до остановки" : "по времени")}, потоки {(options.Threads?.ToString() ?? "все")}, память {memoryMb} МБ, файл {diskMb} МБ");
        var runner = new StressTestRunner();
        var runWatch = Stopwatch.StartNew();
        long totalErrors = 0;
        int completed = 0;
        var safetyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        safetyTimer.Tick += (_, _) => CheckThermals();
        safetyTimer.Start();
        try
        {
            for (int index = 0; index < tests.Count && !token.IsCancellationRequested; index++)
            {
                IStressTest test = tests[index];
                TitleText.Text = $"{test.Name}  ·  {StressResultRow.CategoryName(test.Category)}";
                DescriptionText.Text = test.Description;
                Log($"— {test.Name}: {(infinite ? "непрерывный этап без дедлайна" : $"этапов {test.StageCount}")}");
                double offset = (double)index / tests.Count;
                var progress = new Progress<StressProgress>(p =>
                {
                    if (!ReferenceEquals(_cts, cts) || token.IsCancellationRequested) return;
                    if (!infinite) Progress.Value = Math.Clamp(offset + p.Percent / 100.0 / tests.Count, 0, 1);
                    ProgressText.Text = $"{test.Name}: {(infinite ? "до остановки" : $"этап {p.Stage}/{p.StageCount}")} · прошло {StressRunPolicy.FormatElapsed(runWatch.Elapsed)} · ошибок {p.Errors} · {p.Status}";
                    if (p.Throughput > 0)
                    {
                        _throughputHistory.Add((float)p.Throughput);
                        Trim(_throughputHistory);
                        ThroughputChart.Values = _throughputHistory.ToArray();
                    }
                    ErrorsText.Text = $"ошибки: {p.Errors}";
                });
                StressTestResult result = await runner.RunAsync(test, options, progress, token);
                totalErrors += result.TotalErrors;
                completed++;
                _results.Insert(0, new StressResultRow(result, test.Category));
                SaveResults();
                Log($"   итог: {(result.Cancelled ? "остановлено" : result.Passed ? "успешно" : "ОШИБКИ")}, ошибок {result.TotalErrors}, средняя скорость {result.AverageThroughput:0.0}, время {StressRunPolicy.FormatElapsed(result.Duration)}");
                Log("   " + result.Summary);
                StatusChanged?.Invoke($"{test.Name}: {(result.Cancelled ? _stopReason ?? "остановлено" : result.Passed ? "успешно" : $"ошибок {result.TotalErrors}")}");
            }
            Progress.Value = token.IsCancellationRequested ? Progress.Value : 1;
            ProgressText.Text = $"{(token.IsCancellationRequested ? _stopReason ?? "Остановлено" : $"Готово: тестов {completed}, ошибок {totalErrors}")} · прошло {StressRunPolicy.FormatElapsed(runWatch.Elapsed)}";
            Log(ProgressText.Text);
        }
        catch (OperationCanceledException) { ProgressText.Text = _stopReason ?? "Прогон остановлен"; Log(ProgressText.Text); }
        catch (Exception ex) { ProgressText.Text = "Ошибка: " + ex.Message; Log(ProgressText.Text); }
        finally
        {
            runWatch.Stop();
            safetyTimer.Stop();
            Progress.IsIndeterminate = false;
            InfiniteBox.IsEnabled = DiskConsentBox.IsEnabled = true;
            ThreadsBox.IsEnabled = MemoryBox.IsEnabled = DiskBox.IsEnabled = true;
            _thermalGuard = null;
            _cts = null;
        }
    }

    // ============================ Журнал и результаты ============================

    private void Log(string message)
    {
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        LogScroll.ScrollToEnd();
    }

    private static string ResultsPath => Path.Combine(AppSettings.HistoryDirectory, "stress-results.json");

    private void LoadResults()
    {
        try
        {
            if (!File.Exists(ResultsPath)) return;
            List<StressResultRow>? saved = JsonSerializer.Deserialize<List<StressResultRow>>(
                File.ReadAllText(ResultsPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            foreach (StressResultRow row in (saved ?? new List<StressResultRow>()).Take(40))
                _results.Add(row);
        }
        catch
        {
            // История результатов не критична.
        }
    }

    private void SaveResults()
    {
        try
        {
            File.WriteAllText(ResultsPath, JsonSerializer.Serialize(_results.Take(60).ToList(),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Игнорируем ошибки сохранения истории.
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Отчёт по стресс-тестам",
            FileName = $"stress-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            Filter = "Текстовый отчёт (*.txt)|*.txt",
            InitialDirectory = AppSettings.ReportsDirectory,
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("Отчёт по стресс-тестам — Мониторинг ПК");
            builder.AppendLine($"Сформирован: {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
            builder.AppendLine();
            foreach (StressResultRow row in _results)
            {
                builder.AppendLine($"{row.StartedText} | {row.TestName} | {row.CategoryText} | " +
                                   $"этапов {row.StageCount} | ошибок {row.TotalErrors} | {row.VerdictText}");
                builder.AppendLine($"   средняя скорость {row.ThroughputText}, время {row.Duration:mm\\:ss}");
            }

            builder.AppendLine();
            builder.AppendLine("Журнал последнего прогона:");
            builder.AppendLine(LogBox.Text);

            File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(true));
            StatusChanged?.Invoke($"Отчёт сохранён: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "Не удалось сохранить отчёт:\n" + ex.Message,
                "Мониторинг ПК", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void InnerTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _context is null) return;
        _context.Settings.LastStressTab = InnerTabs.SelectedIndex;
        _context.Settings.Save();
    }

    private static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int value)
            ? Math.Clamp(value, min, max)
            : fallback;

    private sealed record CategoryOption(string Title, StressCategory? Category)
    {
        public override string ToString() => Title;
    }
}
