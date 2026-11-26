using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PcMonitor.Core;
using PcMonitor.Core.Gpu;
using PcMonitor.Core.Testing;
using PcMonitor.Views.Dialogs;

namespace PcMonitor.Views;

/// <summary>Комплектующая в списке «что проверяем».</summary>
public sealed class ComponentOption : INotifyPropertyChanged
{
    private bool _isChecked = true;
    private readonly string _title;
    private readonly string _hint;

    public ComponentOption(ComponentKind kind, string title, string hint, bool isChecked = true)
    {
        Kind = kind;
        _title = title;
        _hint = hint;
        _isChecked = isChecked;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ComponentKind Kind { get; }
    public string Title => LocalizationManager.T(_title);
    public string Hint => LocalizationManager.T(_hint);

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }
}

/// <summary>Накопитель в списке «какие диски проверять».</summary>
public sealed class DiskOption : INotifyPropertyChanged
{
    private bool _isChecked = true;
    private string _kind;
    private readonly string _title;
    private readonly string _hint;

    public static IReadOnlyList<StorageKindChoice> KindChoices { get; } = new[]
    {
        new StorageKindChoice("Auto", "Авто"),
        new StorageKindChoice("Nvme", "NVMe SSD"),
        new StorageKindChoice("Sata", "SATA SSD"),
        new StorageKindChoice("Hdd", "HDD"),
        new StorageKindChoice("UsbFlash", "USB-флешка"),
        new StorageKindChoice("Other", "Другое"),
    };

    public DiskOption(int index, string identity, string title, string hint, string kind)
    {
        Index = index;
        Identity = identity;
        _title = title;
        _hint = hint;
        _kind = kind;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Index { get; }
    public string Identity { get; }
    public string Title => LocalizationManager.T(_title);
    public string Hint => LocalizationManager.T(_hint);
    public IReadOnlyList<StorageKindChoice> Choices => KindChoices;

    public string Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            _kind = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Kind)));
        }
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }
}

public sealed record StorageKindChoice(string Value, string Label);

/// <summary>Пункт списка выводов с цветом плашки.</summary>
public sealed class VerdictRow
{
    public VerdictRow(Verdict verdict)
    {
        Verdict = verdict;
        BadgeBrush = verdict.Level switch
        {
            VerdictLevel.Good => Theme.Good,
            VerdictLevel.Attention => Theme.Warn,
            VerdictLevel.Bad => Theme.Bad,
            _ => Theme.Accent,
        };
    }

    public Verdict Verdict { get; }
    public Brush BadgeBrush { get; }
    public string LevelText => LocalizationManager.T(Verdict.LevelText);
    public string Component => LocalizationManager.T(Verdict.Component);
    public string Title => LocalizationManager.T(Verdict.Title);
    public string Detail => LocalizationManager.T(Verdict.Detail);
    public string Recommendation => LocalizationManager.T(Verdict.Recommendation);
}

/// <summary>Фаза теста в виде, удобном для списка.</summary>
public sealed class PhaseRow
{
    public PhaseRow(AutoTestPhase phase)
    {
        Title = LocalizationManager.T($"{phase.Name} · {phase.Duration.TotalSeconds:0} с" +
                (phase.Errors > 0 ? $" · ошибок {phase.Errors}" : string.Empty));
        Subtitle = LocalizationManager.T(string.IsNullOrWhiteSpace(phase.Notes) ? phase.Description : phase.Notes);
        Metrics = phase.Metrics.Values
            .Where(m => m.HasData)
            .Select(m => new MetricRow(LocalizationManager.T(m.Name), LocalizationManager.T($"мин {m.MinText} · сред {m.AverageText} · макс {m.MaxText}")))
            .ToList();
    }

    public string Title { get; }
    public string Subtitle { get; }
    public List<MetricRow> Metrics { get; }
}

/// <summary>Строка метрики фазы.</summary>
public sealed record MetricRow(string Name, string Value);

/// <summary>Вкладка «Авто-тест»: полная проверка компьютера с выводами и отчётом.</summary>
public partial class AutoTestView : UserControl
{
    private readonly ObservableCollection<VerdictRow> _verdicts = new();
    private readonly ObservableCollection<PhaseRow> _phases = new();
    private readonly ObservableCollection<ComponentOption> _components = new();
    private readonly ObservableCollection<DiskOption> _disks = new();

    private MonitorContext? _context;
    private CancellationTokenSource? _cts;
    private string? _lastReportPath;
    private readonly List<(DateTime Time, string Message)> _logMessages = new();

    public AutoTestView()
    {
        InitializeComponent();
        VerdictList.ItemsSource = _verdicts;
        PhaseList.ItemsSource = _phases;

        _components.Add(new ComponentOption(ComponentKind.Cpu, "Процессор", "Температура, частоты, рендер-бенчмарк, троттлинг"));
        _components.Add(new ComponentOption(ComponentKind.Ram, "Оперативная память", "Тест памяти с проверкой данных, число модулей"));
        _components.Add(new ComponentOption(ComponentKind.Gpu, "Видеокарта", "Нагрузка, артефакты, температуры, hot spot"));
        _components.Add(new ComponentOption(ComponentKind.StorageNvme, "SSD NVMe", "Скорости, температура, износ, ошибки"));
        _components.Add(new ComponentOption(ComponentKind.StorageSata, "SSD / накопитель SATA", "Скорости и состояние"));
        _components.Add(new ComponentOption(ComponentKind.StorageHdd, "Жёсткий диск (HDD)", "Скорости, температура, состояние"));
        _components.Add(new ComponentOption(ComponentKind.StorageUsb, "USB-флешки", "Тест записи на выбранные USB-накопители", isChecked: false));
        _components.Add(new ComponentOption(ComponentKind.StorageOther, "Прочие накопители", "Устройства с нераспознанным типом"));
        _components.Add(new ComponentOption(ComponentKind.Motherboard, "Мат. плата и BIOS", "Напряжения, температуры, версия BIOS"));
        _components.Add(new ComponentOption(ComponentKind.Fans, "Вентиляторы", "Реакция оборотов на нагрузку"));
        _components.Add(new ComponentOption(ComponentKind.Npu, "NPU (нейромодуль)", "Наличие и простая нагрузка"));
        _components.Add(new ComponentOption(ComponentKind.Network, "Сеть", "Активность адаптеров"));
        ComponentList.ItemsSource = _components;
    }

    public event Action<string>? StatusChanged;

    public void ApplyLanguage(string language)
    {
        ComponentList.Items.Refresh();
        DiskList.Items.Refresh();
        VerdictList.Items.Refresh();
        PhaseList.Items.Refresh();
        LogBox.Clear();
        foreach (var entry in _logMessages)
            LogBox.AppendText($"{entry.Time:HH:mm:ss}  {LocalizationManager.T(entry.Message)}{Environment.NewLine}");
        LogScroll.ScrollToEnd();
        LocalizationManager.Apply(this, language);
    }

    public void StopFromHotKey() => _cts?.Cancel();

    public void Attach(MonitorContext context)
    {
        _context = context;
        MemoryBox.Text = context.Settings.StressMemoryMegabytes.ToString(CultureInfo.CurrentCulture);
        DiskBox.Text = context.Settings.StressDiskMegabytes.ToString(CultureInfo.CurrentCulture);
        PhaseSecondsBox.Text = context.Settings.AutoTestPhaseSeconds.ToString(CultureInfo.CurrentCulture);
        IdleSecondsBox.Text = context.Settings.AutoTestIdleSeconds.ToString(CultureInfo.CurrentCulture);
        MemorySecondsBox.Text = context.Settings.AutoTestMemoryPatternSeconds.ToString(CultureInfo.CurrentCulture);
        ReloadDisks();
        AppendLog("Авто-тест готов к запуску. Параметры можно менять в любой момент.");
    }

    /// <summary>Список физических накопителей: любой можно исключить из проверки.</summary>
    private void ReloadDisks()
    {
        try
        {
            _disks.Clear();
            foreach (StorageDisk disk in StorageInventory.Collect())
            {
                string hint = $"{disk.Model} · {disk.BusType} · {disk.SizeGb:0.#} ГБ" +
                              (disk.Volumes.Count > 0
                                  ? $" · тома: {string.Join(", ", disk.Volumes.Select(v => v.Letter))}"
                                  : string.Empty);

                _context!.Settings.StorageTypeOverrides.TryGetValue(disk.StableId, out string? kind);
                _disks.Add(new DiskOption(disk.Index, disk.StableId,
                    $"{disk.Model} ({disk.SizeGb:0.#} ГБ, {disk.BusType})", hint,
                    Enum.TryParse(kind, true, out StorageDeviceKind parsed) ? parsed.ToString() : StorageDeviceKind.Auto.ToString()));
            }

            DiskList.ItemsSource = _disks;
            if (_disks.Count == 0) AppendLog("Накопители не найдены — проверка дисков будет пропущена.");
        }
        catch (Exception ex)
        {
            AppendLog("Не удалось получить список накопителей: " + ex.Message);
        }
    }

    private HashSet<int> SelectedDiskIndexes() =>
        _disks.Where(d => d.IsChecked).Select(d => d.Index).ToHashSet();

    private void DiskKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_context is null || sender is not ComboBox { DataContext: DiskOption disk }) return;
        if (!Enum.TryParse(disk.Kind, true, out StorageDeviceKind kind) || kind == StorageDeviceKind.Auto)
            _context.Settings.StorageTypeOverrides.Remove(disk.Identity);
        else
            _context.Settings.StorageTypeOverrides[disk.Identity] = kind.ToString();
        _context.Settings.Save();
    }

    // ============================ Запуск ============================

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null || _cts is not null) return;

        _cts = new CancellationTokenSource();
        _verdicts.Clear();
        _phases.Clear();
        LogBox.Clear();
        ScoreText.Text = "…";
        SummaryText.Text = "Тест выполняется — идёт нагрузка на комплектующие.";
        CountsText.Text = string.Empty;
        _lastReportPath = null;

        var options = new AutoTestOptions
        {
            Components = _components.Where(c => c.IsChecked).Select(c => c.Kind).ToHashSet(),
            PhaseDuration = TimeSpan.FromSeconds(SelectedDuration()),
            IdleDuration = TimeSpan.FromSeconds(SelectedIdleDuration()),
            IncludeCombinedLoad = CombinedCheck.IsChecked == true,
            MemoryMegabytes = ParseInt(MemoryBox.Text, _context.Settings.StressMemoryMegabytes, 64, 65536),
            DiskMegabytes = ParseInt(DiskBox.Text, _context.Settings.StressDiskMegabytes, 32, 4096),
            MemorySecondsPerPattern = ParseInt(MemorySecondsBox.Text, _context.Settings.AutoTestMemoryPatternSeconds, 3, 600),
            StorageDiskIndexes = SelectedDiskIndexes(),
            RestrictStorageToSelectedDisks = true,
            StorageTypeOverrides = new Dictionary<string, string>(_context.Settings.StorageTypeOverrides, StringComparer.OrdinalIgnoreCase),
            DiskPath = _context.Settings.DiskTestDirectory,
            SaveReport = true,
        };

        _context.Settings.AutoTestPhaseSeconds = SelectedDuration();
        _context.Settings.AutoTestIdleSeconds = SelectedIdleDuration();
        _context.Settings.AutoTestMemoryPatternSeconds = options.MemorySecondsPerPattern;
        _context.Settings.StressMemoryMegabytes = options.MemoryMegabytes;
        _context.Settings.StressDiskMegabytes = options.DiskMegabytes;
        _context.Settings.Save();

        var runner = new AutoTestRunner(_context, options);
        runner.LogLine += OnRunnerLog;
        runner.PhaseCompleted += OnPhaseCompleted;
        runner.GpuWorkload = RunGpuWorkload;

        var progress = new Progress<AutoTestProgress>(p =>
        {
            Progress.Value = Math.Clamp(p.Percent, 0, 1);
            PhaseText.Text = $"Фаза {p.PhaseIndex} из {p.PhaseCount}: {p.Phase} — {p.Status}";
        });

        try
        {
            AutoTestReport report = await runner.RunAsync(progress, _cts.Token);

            foreach (Verdict verdict in report.Verdicts) _verdicts.Add(new VerdictRow(verdict));
            ScoreText.Text = $"{report.Score}/100";
            SummaryText.Text = report.Summary;
            CountsText.Text = $"Хорошо: {report.GoodCount} · обратить внимание: {report.AttentionCount} · " +
                              $"проблемы: {report.BadCount} · время теста {report.Duration:mm\\:ss}";
            PhaseText.Text = "Тест завершён.";
            Progress.Value = 1;

            _lastReportPath = FindLatestReport();
            StatusChanged?.Invoke($"Авто-тест завершён: {report.Summary}");
        }
        catch (OperationCanceledException)
        {
            PhaseText.Text = "Тест остановлен пользователем.";
        }
        catch (Exception ex)
        {
            PhaseText.Text = "Ошибка авто-теста: " + ex.Message;
            AppendLog("Ошибка: " + ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        PhaseText.Text = "остановка…";
    }

    public void RequestStop()
    {
        if (_cts is null) return;
        _cts.Cancel();
        PhaseText.Text = "остановка…";
    }

    // ============================ GPU-нагрузка ============================

    /// <summary>
    /// Нагрузка на видеокарту через Direct3D 11: рендер идёт в offscreen-текстуры,
    /// поэтому результат не зависит от частоты обновления монитора. Сначала проверяется
    /// стабильность (поиск артефактов), затем измеряется частота кадров.
    /// </summary>
    private (long Errors, string Details) RunGpuWorkload(TimeSpan duration, CancellationToken token)
    {
        if (_context is null) return (0, "GPU-нагрузка недоступна");

        try
        {
            using var engine = new GpuEngine(0);
            double stabilitySeconds = Math.Max(4, duration.TotalSeconds * 0.35);
            double loadSeconds = Math.Max(4, duration.TotalSeconds - stabilitySeconds);

            long errors = engine.RunArtifactCheck("julia", GpuPreset.High, checks: 6,
                TimeSpan.FromSeconds(stabilitySeconds), null, token);

            // Тяжёлая многопроходная сцена и синхронизация раз в 8 кадров:
            // очередь команд заполнена, видеокарта загружена почти на 100 %.
            GpuTestResult result = engine.RunRasterTest("bloom", GpuPreset.High,
                TimeSpan.FromSeconds(loadSeconds), null, token, syncEveryNFrames: 8);

            string renderer = engine.UsedSoftwareRenderer ? "программный рендер (WARP)" : "аппаратный";
            return (errors, $"{engine.Adapter.Name} ({renderer}): {result.AverageFps:0.0} FPS, " +
                            $"минимум {result.MinimumFps:0.0}, артефактов {errors}");
        }
        catch (Exception ex)
        {
            return (0, "ошибка GPU-нагрузки: " + ex.Message);
        }
    }

    // ============================ Служебное ============================

    private void OnRunnerLog(string message) =>
        Dispatcher.BeginInvoke(new Action(() => AppendLog(message)));

    private void OnPhaseCompleted(AutoTestPhase phase) =>
        Dispatcher.BeginInvoke(new Action(() => _phases.Insert(0, new PhaseRow(phase))));

    private void AppendLog(string message)
    {
        DateTime now = DateTime.Now;
        _logMessages.Add((now, message));
        LogBox.AppendText($"{now:HH:mm:ss}  {LocalizationManager.T(message)}{Environment.NewLine}");
        LogScroll.ScrollToEnd();
    }

    private int SelectedDuration()
    {
        if (DurationBox.SelectedItem is ComboBoxItem { Tag: string tag } && tag == "0")
            return ParseInt(PhaseSecondsBox.Text, _context?.Settings.AutoTestPhaseSeconds ?? 30, 5, 3600);
        return DurationBox.SelectedItem is ComboBoxItem { Tag: string value } && int.TryParse(value, out int seconds)
            ? Math.Clamp(seconds, 5, 3600)
            : 30;
    }

    private int SelectedIdleDuration() =>
        ParseInt(IdleSecondsBox.Text, _context?.Settings.AutoTestIdleSeconds ?? 10, 5, 3600);

    private static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text.Trim(), out int value) ? Math.Clamp(value, min, max) : fallback;

    private static string? FindLatestReport()
    {
        try
        {
            DirectoryInfo directory = new(AppSettings.ReportsDirectory);
            FileInfo? latest = directory.GetFiles("autotest-*.html")
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault();
            return latest?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        string? path = _lastReportPath ?? FindLatestReport();
        if (path is null)
        {
            MessageBox.Show(Window.GetWindow(this), "Отчётов пока нет — запустите авто-тест.",
                "Авто-тест", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "Не удалось открыть отчёт:\n" + ex.Message,
                "Авто-тест", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        List<AutoTestHistoryEntry> history = HtmlReportWriter.LoadHistory();
        if (history.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "История пуста.", "Авто-тест",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string text = string.Join(Environment.NewLine, history.Select(h =>
            $"{h.Started:dd.MM.yyyy HH:mm}  оценка {h.Score}/100  " +
            $"хорошо {h.Good}, внимание {h.Attention}, проблемы {h.Bad}  ({h.DurationSeconds / 60:0.#} мин)"));

        MessageBox.Show(Window.GetWindow(this), text, "История авто-тестов",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Rgb_Click(object sender, RoutedEventArgs e)
    {
        var wizard = new RgbWizardWindow { Owner = Window.GetWindow(this) };
        wizard.ShowDialog();
        AppendLog("Мастер проверки подсветки закрыт.");
    }
}
