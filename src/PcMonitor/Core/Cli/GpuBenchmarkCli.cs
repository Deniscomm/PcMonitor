using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PcMonitor.Controls;
using PcMonitor.Core.Gpu;

namespace PcMonitor.Core;

/// <summary>
/// Режим командной строки: бенчмарк GPU (--gpu-bench). Открывает окно нагрузки
/// в духе GPU, измеряет частоту кадров, проверяет артефакты и пишет отчёт.
/// С ключом --d3d работает движок Direct3D 11 без вертикальной синхронизации.
/// </summary>
internal static class GpuBenchmarkCli
{
    public static int Run(string[] args)
    {
        ConsoleHelper.Attach();

        if (args.Contains("--d3d"))
            return RunDirect3D(args);

        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            Console.WriteLine(
                """
                Бенчмарк GPU — Мониторинг ПК.

                  PcMonitor.exe --gpu-bench                 30 секунд нагрузки, нагрузка 5/5
                  PcMonitor.exe --gpu-bench --seconds 60    длительность в секундах
                  PcMonitor.exe --gpu-bench --load 3        уровень нагрузки 1..5
                  PcMonitor.exe --gpu-bench --out report.txt

                Во время прогона измеряется частота кадров, температура, мощность и частоты GPU,
                а кадр сравнивается с эталонным для поиска артефактов.
                """);
            return 0;
        }

        int seconds = Math.Clamp(GetInt(args, "--seconds", 30), 5, 3600);
        int load = Math.Clamp(GetInt(args, "--load", 8), 1, 12);
        string outPath = GetValue(args, "--out")
                         ?? Path.Combine(AppSettings.ReportsDirectory, "gpu-benchmark.txt");

        Console.WriteLine($"Бенчмарк GPU: {seconds} с, уровень нагрузки {load}/5. Идёт прогон…");

        var settings = new AppSettings();
        using var service = new MonitorService { Interval = TimeSpan.FromSeconds(1) };

        double maxTemperature = 0, maxPower = 0, maxClock = 0;
        double temperatureSum = 0;
        int temperatureSamples = 0;

        service.SnapshotReady += (readings, _) =>
        {
            var rows = readings.Select(r => new SensorRow(r, settings)).ToList();
            double? temperature = MetricCatalog.Find(MetricCatalog.GpuTemperature)?.Resolve(rows);
            double? power = MetricCatalog.Find(MetricCatalog.GpuPower)?.Resolve(rows);
            double? clock = MetricCatalog.Find(MetricCatalog.GpuClock)?.Resolve(rows);

            if (temperature.HasValue)
            {
                maxTemperature = Math.Max(maxTemperature, temperature.Value);
                temperatureSum += temperature.Value;
                temperatureSamples++;
            }

            if (power.HasValue) maxPower = Math.Max(maxPower, power.Value);
            if (clock.HasValue) maxClock = Math.Max(maxClock, clock.Value);
        };

        service.Start();

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var surface = new GpuStressSurface { LoadLevel = load };
        var window = new Window
        {
            Title = "Бенчмарк GPU — Мониторинг ПК",
            Width = 1100,
            Height = 720,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Brushes.Black,
            Content = surface,
        };

        window.Show();

        var samples = new List<double>();
        byte[]? golden = null;
        long artifacts = 0;
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            if (surface.Fps > 0) samples.Add(surface.Fps);
            if (golden is null && samples.Count >= 6) golden = surface.CaptureDeterministicFrame();

            if (DateTime.UtcNow < deadline) return;

            timer.Stop();
            if (golden is not null) artifacts = CompareFrames(golden, surface.CaptureDeterministicFrame());
            window.Close();
            app.Shutdown();
        };

        timer.Start();
        app.Run();
        service.Stop();

        double average = samples.Count > 0 ? samples.Average() : 0;
        double minimum = samples.Count > 0 ? samples.Min() : 0;
        double maximum = samples.Count > 0 ? samples.Max() : 0;
        int score = (int)Math.Round(average * 100);
        string verdict = artifacts == 0 ? "сбоев не обнаружено" : $"обнаружены артефакты: {artifacts} пикселей";

        var report = new StringBuilder();
        report.AppendLine("Бенчмарк GPU — Мониторинг ПК");
        report.AppendLine(new string('=', 70));
        report.AppendLine($"Дата: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"Длительность: {seconds} с, уровень нагрузки {load}/12");
        report.AppendLine($"Частота кадров: средняя {average:0.0} FPS, минимум {minimum:0.0}, максимум {maximum:0.0}");
        report.AppendLine($"Балл: {score} (средняя частота кадров × 100; чем больше нагрузка, тем ниже FPS)");
        report.AppendLine($"Температура GPU: максимум {maxTemperature:0.0} °C, средняя " +
                          $"{(temperatureSamples > 0 ? temperatureSum / temperatureSamples : 0):0.0} °C");
        report.AppendLine($"Мощность GPU: максимум {maxPower:0.0} Вт");
        report.AppendLine($"Частота GPU: максимум {maxClock:0} МГц");
        report.AppendLine($"Проверка кадров: {verdict}");
        report.AppendLine();
        report.AppendLine("Первый график (10 секунд) и результаты прогонов сохраняются в историю приложения.");

        string text = report.ToString();
        Console.WriteLine(text);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllText(outPath, text, new UTF8Encoding(false));
            Console.WriteLine("Отчёт сохранён: " + outPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Не удалось сохранить отчёт: " + ex.Message);
            return 3;
        }

        return 0;
    }

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

    private static int GetInt(string[] args, string name, int fallback)
    {
        string? raw = GetValue(args, name);
        return raw is not null && int.TryParse(raw, out int value) ? value : fallback;
    }

    /// <summary>
    /// Бенчмарк на Direct3D 11: рендер только в offscreen-текстуры, без swap chain,
    /// поэтому результат не зависит от частоты обновления монитора.
    /// </summary>
    private static int RunDirect3D(string[] args)
    {
        GpuPreset preset = ParsePreset(GetValue(args, "--preset"));
        int seconds = Math.Clamp(GetInt(args, "--seconds", 8), 3, 120);
        string outPath = GetValue(args, "--out")
                         ?? Path.Combine(AppSettings.ReportsDirectory, "gpu-d3d-benchmark.txt");

        var builder = new StringBuilder();
        var results = new List<GpuTestResult>();

        try
        {
            using var engine = new GpuEngine(0);
            Console.WriteLine($"Адаптер: {engine.Adapter.Name} ({engine.Adapter.MemoryText})");
            Console.WriteLine($"Устройство: {(engine.UsedSoftwareRenderer ? "ПРОГРАММНОЕ (WARP)" : "аппаратное")} — {engine.DeviceDiagnostics}");

            var progress = new Progress<GpuTestProgress>(p =>
                Console.WriteLine($"    {p.Stage}: {p.Percent:0} %, {p.Value:0.0} {p.ValueUnit} — {p.Status}"));

            // Сначала проверка стабильности на самой тяжёлой сцене.
            Console.WriteLine("Проверка стабильности (поиск артефактов)…");
            long errors = engine.RunArtifactCheck("raymarch", preset, checks: 6,
                TimeSpan.FromSeconds(20), null, CancellationToken.None);

            Console.WriteLine($"Бенчмарк: пресет {preset}, по {seconds} с на сцену…");
            GpuTestResult summary = engine.RunBenchmark(preset, TimeSpan.FromSeconds(seconds), progress, CancellationToken.None);

            foreach (string scene in GpuEngine.Scenes.Select(s => s.Name))
            {
                GpuTestResult result = engine.RunRasterTest(scene, preset, TimeSpan.FromSeconds(seconds), null, CancellationToken.None);
                results.Add(result);
            }

            builder.AppendLine("GPU-бенчмарк Direct3D 11 (без вертикальной синхронизации) — Мониторинг ПК");
            builder.AppendLine(new string('=', 90));
            builder.AppendLine($"Дата: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"Адаптер: {engine.Adapter.Name}, {engine.Adapter.MemoryText}");
            builder.AppendLine($"Устройство: {(engine.UsedSoftwareRenderer ? "программное (WARP)" : "аппаратное")}");
            builder.AppendLine($"Пресет: {preset}, длительность сцены {seconds} с");
            builder.AppendLine($"Проверка стабильности: расхождений кадров {errors}");
            builder.AppendLine();
            builder.AppendLine($"{"Сцена",-34}{"Разрешение",14}{"Средний FPS",14}{"Минимум",12}{"Максимум",12}{"Балл",10}");
            builder.AppendLine(new string('-', 90));

            foreach (GpuTestResult result in results)
            {
                builder.AppendLine($"{result.TestName,-34}{result.ResolutionText,14}{result.AverageFps,14:0.0}" +
                                   $"{result.MinimumFps,12:0.0}{result.MaximumFps,12:0.0}{result.Score,10}");
            }

            builder.AppendLine();
            builder.AppendLine($"Итоговый балл (среднее по сценам): {summary.Score}");
            builder.AppendLine($"Средний FPS: {summary.AverageFps:0.0}, минимум {summary.MinimumFps:0.0}, максимум {summary.MaximumFps:0.0}");
            builder.AppendLine($"Кадров всего: {summary.Frames}, время прогона {summary.Duration:mm\\:ss}");
            builder.AppendLine();
            builder.AppendLine("Примечание: сцены рисуются в offscreen-текстуры, поэтому 60 FPS — не предел,");
            builder.AppendLine("и результат не зависит от герцовки монитора.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Ошибка Direct3D: " + ex.Message);
            builder.AppendLine("Ошибка Direct3D: " + ex.Message);
            return 3;
        }

        string text = builder.ToString();
        Console.WriteLine();
        Console.WriteLine(text);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllText(outPath, text, new UTF8Encoding(false));
            Console.WriteLine("Отчёт сохранён: " + outPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Не удалось сохранить отчёт: " + ex.Message);
        }

        return 0;
    }

    private static GpuPreset ParsePreset(string? value) => value?.ToLowerInvariant() switch
    {
        "low" or "низкий" => GpuPreset.Low,
        "high" or "высокий" => GpuPreset.High,
        "extreme" or "максимум" => GpuPreset.Extreme,
        _ => GpuPreset.Medium,
    };

    private static string? GetValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }

        return null;
    }
}
