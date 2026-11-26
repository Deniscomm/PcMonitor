using PcMonitor.Core.Testing;
using PcMonitor.Core.Gpu;

namespace PcMonitor.Core;

/// <summary>
/// Режим командной строки: авто-тест всего компьютера (--autotest).
/// Выполняет полный набор проверок, печатает выводы и сохраняет HTML-отчёт.
/// </summary>
internal static class AutoTestCli
{
    private static readonly string TracePath =
        Path.Combine(Path.GetTempPath(), "pcmonitor-autotest.log");

    /// <summary>Пошаговый журнал прогона: помогает понять, на какой фазе тест остановился.</summary>
    private static void Trace(string message)
    {
        try
        {
            File.AppendAllText(TracePath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // Журнал не критичен.
        }
    }

    public static int Run(string[] args)
    {
        try
        {
            File.WriteAllText(TracePath, $"=== Авто-тест {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
        }
        catch
        {
            // Игнорируем проблемы с журналом.
        }

        Trace("старт");
        ConsoleHelper.Attach();
        Trace($"консоль подключена, вывод перенаправлен: {Console.IsOutputRedirected}");

        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            Console.WriteLine(
                """
                Авто-тест компьютера — Мониторинг ПК.

                  PcMonitor.exe --autotest                     полный тест (фазы по 30 секунд)
                  PcMonitor.exe --autotest --quick             быстрый тест (фазы по 15 секунд)
                  PcMonitor.exe --autotest --seconds 60        длительность каждой фазы
                  PcMonitor.exe --autotest --components cpu,ram,gpu,storage
                  PcMonitor.exe --autotest --memory 4096 --disk-mb 1024
                  PcMonitor.exe --autotest --out report.html

                Проверяются: процессор, память, видеокарта, накопители, мат. плата,
                вентиляторы, NPU, сеть. Итог — отчёт с оценкой и рекомендациями.
                """);
            return 0;
        }

        AppSettings settings = AppSettings.Load();
        AutoTestOptions options = BuildOptions(args, settings);
        Trace($"настройки загружены, папка данных {AppSettings.DataDirectory}, фаз по {options.PhaseDuration.TotalSeconds:0} с");

        using var service = new MonitorService { Interval = TimeSpan.FromSeconds(1) };
        var context = new MonitorContext(settings, service);

        // Мини-контроллер: переносим показания датчиков в контекст, как это делает окно.
        service.SnapshotReady += (readings, timestamp) =>
        {
            foreach (SensorReading reading in readings)
            {
                if (context.ById.TryGetValue(reading.Id, out SensorRow? row))
                {
                    row.Apply(reading, settings);
                    continue;
                }

                settings.SensorOverrides.TryGetValue(reading.Id, out SensorOverride? rule);
                row = new SensorRow(reading, settings, rule);
                context.ById[reading.Id] = row;
                context.Rows.Add(row);
            }

            context.LastUpdate = timestamp;
            context.RaiseUpdated();
        };

        service.Start();
        Trace("служба датчиков запущена");

        Console.WriteLine("Инициализация датчиков…");
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (context.Rows.Count == 0 && wait.Elapsed < TimeSpan.FromSeconds(20))
            Thread.Sleep(200);

        Console.WriteLine($"Датчиков: {context.Rows.Count}, права администратора: {(service.IsElevated ? "да" : "нет")}");
        Console.WriteLine($"Фазы по {options.PhaseDuration.TotalSeconds:0} с. Тест идёт…");
        Console.WriteLine();

        Trace($"датчиков {context.Rows.Count}, создаю авто-тест");
        var runner = new AutoTestRunner(context, options);
        runner.LogLine += message => { Trace("  " + message); Console.WriteLine("  " + message); };
        runner.PhaseCompleted += phase => Trace($"фаза завершена: {phase.Name} ({phase.Duration.TotalSeconds:0} с), ошибок {phase.Errors}");
        runner.GpuWorkload = (duration, token) => RunGpuWorkload(duration, token);

        var progress = new Progress<AutoTestProgress>(p =>
        {
            Trace($"[{p.PhaseIndex}/{p.PhaseCount}] {p.Phase}: {p.Status} ({p.Percent:0} %)");
            Console.WriteLine($"  [{p.PhaseIndex}/{p.PhaseCount}] {p.Phase}: {p.Status}");
        });

        AutoTestReport report;
        try
        {
            report = runner.RunAsync(progress, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine("Ошибка авто-теста: " + ex.Message);
            return 2;
        }
        finally
        {
            Trace("фазы завершены, останавливаю службу датчиков");
            service.Stop();
        }

        Trace($"отчёт готов: оценка {report.Score}, {report.Summary}");
        Console.WriteLine();
        Console.WriteLine(new string('=', 100));
        Console.WriteLine($"ОЦЕНКА: {report.Score}/100 — {report.Summary}");
        Console.WriteLine(new string('=', 100));
        Console.WriteLine();

        foreach (Verdict verdict in report.Verdicts)
        {
            Console.WriteLine($"[{verdict.LevelText.ToUpperInvariant()}] {verdict.Component}: {verdict.Title}");
            Console.WriteLine($"    {verdict.Detail}");
            Console.WriteLine($"    Совет: {verdict.Recommendation}");
            Console.WriteLine();
        }

        string? reportPath = GetValue(args, "--out");
        if (reportPath is not null)
        {
            try
            {
                string full = Path.GetFullPath(reportPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, BuildTextReport(report));
                Console.WriteLine("Отчёт сохранён: " + full);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Не удалось сохранить отчёт: " + ex.Message);
            }
        }

        return report.BadCount > 0 ? 1 : 0;
    }

    private static (long Errors, string Details) RunGpuWorkload(TimeSpan duration, CancellationToken token)
    {
        try
        {
            using var engine = new GpuEngine(0);
            double stability = Math.Max(4, duration.TotalSeconds * 0.35);
            double load = Math.Max(4, duration.TotalSeconds - stability);

            long errors = engine.RunArtifactCheck("julia", GpuPreset.High, checks: 6,
                TimeSpan.FromSeconds(stability), null, token);
            GpuTestResult result = engine.RunRasterTest("bloom", GpuPreset.High,
                TimeSpan.FromSeconds(load), null, token, syncEveryNFrames: 8);

            return (errors, $"{engine.Adapter.Name}: {result.AverageFps:0.0} FPS, артефактов {errors}");
        }
        catch (Exception ex)
        {
            return (0, "ошибка GPU-нагрузки: " + ex.Message);
        }
    }

    private static AutoTestOptions BuildOptions(string[] args, AppSettings settings)
    {
        double seconds = args.Contains("--quick") ? 15 : 30;
        string? secondsValue = GetValue(args, "--seconds");
        if (secondsValue is not null && double.TryParse(secondsValue, out double parsed))
            seconds = Math.Clamp(parsed, 5, 300);

        var components = new HashSet<ComponentKind>(Enum.GetValues<ComponentKind>());
        string? list = GetValue(args, "--components");
        if (!string.IsNullOrWhiteSpace(list))
        {
            components.Clear();
            foreach (string name in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Enum.TryParse(name, ignoreCase: true, out ComponentKind kind)) components.Add(kind);
                else if (name.StartsWith("storage", StringComparison.OrdinalIgnoreCase))
                {
                    components.Add(ComponentKind.StorageNvme);
                    components.Add(ComponentKind.StorageSata);
                    components.Add(ComponentKind.StorageHdd);
                }
            }
        }

        int memory = int.TryParse(GetValue(args, "--memory"), out int memoryValue)
            ? Math.Clamp(memoryValue, 64, 65536)
            : settings.StressMemoryMegabytes;

        int disk = int.TryParse(GetValue(args, "--disk-mb"), out int diskValue)
            ? Math.Clamp(diskValue, 32, 4096)
            : settings.StressDiskMegabytes;

        return new AutoTestOptions
        {
            Components = components,
            PhaseDuration = TimeSpan.FromSeconds(seconds),
            IdleDuration = TimeSpan.FromSeconds(Math.Max(5, seconds / 3)),
            MemoryMegabytes = memory,
            DiskMegabytes = disk,
            DiskPath = settings.DiskTestDirectory,
            SaveReport = true,
        };
    }

    private static string BuildTextReport(AutoTestReport report)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("АВТО-ТЕСТ КОМПЬЮТЕРА — Мониторинг ПК");
        builder.AppendLine(new string('=', 100));
        builder.AppendLine($"Дата: {report.Started:dd.MM.yyyy HH:mm:ss}   Длительность: {report.Duration:mm\\:ss}");
        builder.AppendLine($"Машина: {report.Machine}   Права администратора: {(report.Elevated ? "да" : "нет")}");
        builder.AppendLine($"Оценка: {report.Score}/100 — {report.Summary}");
        builder.AppendLine();

        builder.AppendLine("ВЫВОДЫ");
        builder.AppendLine(new string('-', 100));
        foreach (Verdict verdict in report.Verdicts)
        {
            builder.AppendLine($"[{verdict.LevelText.ToUpperInvariant()}] {verdict.Component}: {verdict.Title}");
            builder.AppendLine($"    {verdict.Detail}");
            builder.AppendLine($"    Совет: {verdict.Recommendation}");
        }

        builder.AppendLine();
        builder.AppendLine("СОСТАВ СИСТЕМЫ");
        builder.AppendLine(new string('-', 100));
        foreach (ComponentInfo component in report.Components)
            builder.AppendLine($"{component.Name,-42}{component.Details}");

        foreach (AutoTestPhase phase in report.Phases)
        {
            builder.AppendLine();
            builder.AppendLine($"ФАЗА: {phase.Name} — {phase.Duration.TotalSeconds:0} с, ошибок {phase.Errors}");
            if (!string.IsNullOrWhiteSpace(phase.Notes)) builder.AppendLine(phase.Notes);
            foreach (MetricSummary metric in phase.Metrics.Values.Where(m => m.HasData))
                builder.AppendLine($"  {metric.Name,-34}мин {metric.MinText,-16}сред {metric.AverageText,-16}макс {metric.MaxText}");
        }

        return builder.ToString();
    }

    private static string? GetValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }

        return null;
    }
}
