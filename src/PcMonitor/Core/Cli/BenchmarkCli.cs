using System.IO;
using System.Text;

namespace PcMonitor.Core;

/// <summary>Режим командной строки: бенчмарки процессора, памяти и диска (--bench).</summary>
internal static class BenchmarkCli
{
    public static int Run(string[] args)
    {
        ConsoleHelper.Attach();

        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            Console.WriteLine(
                """
                Бенчмарки — Мониторинг ПК.

                  PcMonitor.exe --bench                    все тесты (~1 минута)
                  PcMonitor.exe --bench --cpu              только процессор
                  PcMonitor.exe --bench --memory           только память
                  PcMonitor.exe --bench --disk             только диск
                  PcMonitor.exe --bench --disk-mb 2048     размер файла теста диска
                  PcMonitor.exe --bench --out report.txt   сохранить отчёт
                """);
            return 0;
        }

        bool cpu = args.Contains("--cpu") || !args.Any(a => a is "--cpu" or "--memory" or "--disk");
        bool memory = args.Contains("--memory") || !args.Any(a => a is "--cpu" or "--memory" or "--disk");
        bool disk = args.Contains("--disk") || !args.Any(a => a is "--cpu" or "--memory" or "--disk");
        int diskMb = Math.Clamp(GetInt(args, "--disk-mb", 512), 32, 8192);
        int memoryMb = Math.Clamp(GetInt(args, "--memory-mb", 512), 64, 4096);
        string outPath = GetValue(args, "--out")
                         ?? Path.Combine(AppSettings.ReportsDirectory, "benchmark-report.txt");

        var results = new List<BenchmarkResult>();
        using var cts = new CancellationTokenSource();
        string tempFile = BenchmarkSuite.CreateTempFile(
            string.IsNullOrWhiteSpace(AppSettings.Load().StressDiskPath) ? null : AppSettings.Load().StressDiskPath, "bench");

        try
        {
            if (cpu)
            {
                Console.WriteLine("Процессор, один поток…");
                results.Add(BenchmarkSuite.CpuSingle(TimeSpan.FromSeconds(5), cts.Token));
                Console.WriteLine("Процессор, все потоки…");
                results.Add(BenchmarkSuite.CpuMulti(TimeSpan.FromSeconds(6), cts.Token));
            }

            if (memory)
            {
                Console.WriteLine($"Память, пропускная способность ({memoryMb} МБ)…");
                results.Add(BenchmarkSuite.MemoryBandwidth(memoryMb, TimeSpan.FromSeconds(5), cts.Token));
                Console.WriteLine("Память, задержка доступа…");
                results.Add(BenchmarkSuite.MemoryLatency(Math.Min(memoryMb, 256), cts.Token));
            }

            if (disk)
            {
                Console.WriteLine($"Диск, последовательная запись {diskMb} МБ…");
                results.Add(BenchmarkSuite.DiskSequentialWrite(tempFile, diskMb, cts.Token));
                Console.WriteLine("Диск, последовательное чтение…");
                results.Add(BenchmarkSuite.DiskSequentialRead(tempFile, diskMb, cts.Token));
                Console.WriteLine("Диск, случайное чтение 4К…");
                results.Add(BenchmarkSuite.DiskRandom(tempFile, write: false, operations: 4000, cts.Token));
                Console.WriteLine("Диск, случайная запись 4К…");
                results.Add(BenchmarkSuite.DiskRandom(tempFile, write: true, operations: 1500, cts.Token));
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
            catch
            {
                // Файл мог остаться — не критично.
            }
        }

        var builder = new StringBuilder();
        builder.AppendLine("Бенчмарки — Мониторинг ПК");
        builder.AppendLine(new string('=', 78));
        builder.AppendLine($"Дата: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Процессор: {Environment.ProcessorCount} логических ядер");
        builder.AppendLine();
        builder.AppendLine($"{"Тест",-42}{"Значение",14}{"Единица",10}{"Балл",8}");
        builder.AppendLine(new string('-', 78));

        foreach (BenchmarkResult result in results)
            builder.AppendLine($"{result.Name,-42}{result.ValueText,14}{result.Unit,10}{result.Score,8}");

        builder.AppendLine();
        foreach (BenchmarkResult result in results)
            builder.AppendLine($"  {result.Name}: {result.Details}");

        string text = builder.ToString();
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

    private static int GetInt(string[] args, string name, int fallback)
    {
        string? raw = GetValue(args, name);
        return raw is not null && int.TryParse(raw, out int value) ? value : fallback;
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
