using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PcMonitor.Core;

namespace PcMonitor.Views;

/// <summary>Вкладка бенчмарков: процессор, память, диск — с баллами и историей.</summary>
public partial class BenchmarkView : UserControl
{
    private readonly ObservableCollection<BenchmarkResult> _results = new();
    private MonitorContext? _context;
    private CancellationTokenSource? _cts;

    public BenchmarkView()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _results;

        foreach (BenchmarkResult result in BenchmarkSuite.LoadHistory().Take(60))
            _results.Add(result);
    }

    public event Action<string>? StatusChanged;

    public void ApplyLanguage(string language)
    {
        LocalizationManager.Apply(this, language);
        ResultsGrid.Items.Refresh();
    }

    public void Attach(MonitorContext context)
    {
        _context = context;
    }

    private async void RunAll_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(cpu: true, memory: true, disk: true);

    private async void RunCpu_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(cpu: true, memory: false, disk: false);

    private async void RunMemory_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(cpu: false, memory: true, disk: false);

    private async void RunDisk_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(cpu: false, memory: false, disk: true);

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CurrentText.Text = "остановка…";
    }

    private async Task RunAsync(bool cpu, bool memory, bool disk)
    {
        if (_cts is not null) return;

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        AppSettings settings = _context?.Settings ?? new AppSettings();
        int memoryMb = Math.Clamp(settings.StressMemoryMegabytes, 64, 4096);
        int diskMb = Math.Clamp(settings.StressDiskMegabytes, 32, 4096);
        string? diskFolder = string.IsNullOrWhiteSpace(settings.StressDiskPath) ? null : settings.StressDiskPath;
        string tempFile = BenchmarkSuite.CreateTempFile(diskFolder, "bench");

        var plan = new List<(string Title, Func<BenchmarkResult> Action)>();
        if (cpu)
        {
            plan.Add((LocalizationManager.T("Процессор, один поток"), () => BenchmarkSuite.CpuSingle(TimeSpan.FromSeconds(5), token)));
            plan.Add((LocalizationManager.T("Процессор, все потоки"), () => BenchmarkSuite.CpuMulti(TimeSpan.FromSeconds(6), token)));
        }

        if (memory)
        {
            plan.Add((LocalizationManager.T($"Память, пропускная способность ({memoryMb} МБ)"), () => BenchmarkSuite.MemoryBandwidth(memoryMb, TimeSpan.FromSeconds(5), token)));
            plan.Add((LocalizationManager.T($"Память, задержка ({memoryMb} МБ)"), () => BenchmarkSuite.MemoryLatency(Math.Min(memoryMb, 256), token)));
        }

        if (disk)
        {
            plan.Add((LocalizationManager.T($"Диск, запись {diskMb} МБ"), () => BenchmarkSuite.DiskSequentialWrite(tempFile, diskMb, token)));
            plan.Add((LocalizationManager.T($"Диск, чтение {diskMb} МБ"), () => BenchmarkSuite.DiskSequentialRead(tempFile, diskMb, token)));
            plan.Add((LocalizationManager.T("Диск, случайное чтение 4К"), () => BenchmarkSuite.DiskRandom(tempFile, write: false, operations: 4000, token)));
            plan.Add((LocalizationManager.T("Диск, случайная запись 4К"), () => BenchmarkSuite.DiskRandom(tempFile, write: true, operations: 2000, token)));
        }

        try
        {
            for (int i = 0; i < plan.Count; i++)
            {
                if (token.IsCancellationRequested) break;

                (string title, Func<BenchmarkResult> action) = plan[i];
                Progress.Value = (double)i / plan.Count;
                CurrentText.Text = $"Выполняется: {title}…";
                StatusChanged?.Invoke($"Бенчмарк: {title}");

                BenchmarkResult result = await Task.Run(action, token);
                _results.Insert(0, result);
                BenchmarkSuite.SaveHistory(_results);
                StatusChanged?.Invoke($"{result.Name}: {result.ValueText} {result.Unit} (балл {result.Score})");
            }

            Progress.Value = 1;
            CurrentText.Text = token.IsCancellationRequested ? "остановлено" : $"Готово: выполнено тестов {plan.Count}";
        }
        catch (OperationCanceledException)
        {
            CurrentText.Text = "Бенчмарк остановлен пользователем";
        }
        catch (Exception ex)
        {
            CurrentText.Text = "Ошибка бенчмарка: " + ex.Message;
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
            catch
            {
                // Временный файл мог остаться — не критично.
            }

            _cts.Dispose();
            _cts = null;
            Progress.Value = 0;
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _results.Clear();
        BenchmarkSuite.SaveHistory(_results);
        StatusChanged?.Invoke("История бенчмарков очищена");
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Отчёт по бенчмаркам",
            FileName = $"benchmarks-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            Filter = "CSV (*.csv)|*.csv|Текст (*.txt)|*.txt",
            InitialDirectory = AppSettings.ReportsDirectory,
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("Дата;Тест;Значение;Единица;Балл;Длительность;Примечание");
            foreach (BenchmarkResult result in _results)
            {
                builder.AppendLine($"{result.StartedText};{result.Name};{result.ValueText};{result.Unit};" +
                                   $"{result.Score};{result.Duration:mm\\:ss};{result.Details}");
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
