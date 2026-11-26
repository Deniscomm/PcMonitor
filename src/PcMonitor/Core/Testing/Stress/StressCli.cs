using System.IO;
using System.Text;

namespace PcMonitor.Core.Stress;

/// <summary>Режим командной строки: стресс-тесты из каталога (--stress).</summary>
internal static class StressCli
{
    public static int Run(string[] args)
    {
        ConsoleHelper.Attach();

        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            Console.WriteLine(
                """
                Стресс-тесты — Мониторинг ПК.

                  PcMonitor.exe --stress                          первые тесты из каждой категории
                  PcMonitor.exe --stress --category Memory        только тесты памяти
                  PcMonitor.exe --stress --count 5                сколько тестов прогнать
                  PcMonitor.exe --stress --id mem.own.single      конкретный тест по идентификатору
                  PcMonitor.exe --stress --list                   список всех тестов
                  PcMonitor.exe --stress --memory 2048 --disk-mb 1024
                  PcMonitor.exe --stress --out report.txt

                Каждый тест выполняется полностью (30 этапов) с подсчётом ошибок.
                """);
            return 0;
        }

        if (args.Contains("--list"))
        {
            Console.WriteLine($"Всего тестов: {StressTestCatalog.All.Count}");
            foreach (StressCategory category in Enum.GetValues<StressCategory>())
            {
                List<IStressTest> tests = StressTestCatalog.ByCategory(category).ToList();
                if (tests.Count == 0) continue;

                Console.WriteLine();
                Console.WriteLine($"== {category} ({tests.Count}) ==");
                foreach (IStressTest test in tests)
                    Console.WriteLine($"  {test.Id,-34} {test.Name}  ·  этапов {test.StageCount}, " +
                                      $"по {test.StageDuration.TotalSeconds:0.#} с");
            }

            return 0;
        }

        var selected = new List<IStressTest>();
        string? id = GetValue(args, "--id");
        string? categoryName = GetValue(args, "--category");
        int count = Math.Clamp(GetInt(args, "--count", 0), 0, 200);

        if (id is not null)
        {
            IStressTest? test = StressTestCatalog.Find(id);
            if (test is null)
            {
                Console.WriteLine($"Тест «{id}» не найден. Список: PcMonitor.exe --stress --list");
                return 2;
            }

            selected.Add(test);
        }
        else if (categoryName is not null && Enum.TryParse(categoryName, ignoreCase: true, out StressCategory category))
        {
            selected.AddRange(StressTestCatalog.ByCategory(category));
        }
        else
        {
            // По умолчанию — по одному первому тесту из каждой категории.
            foreach (StressCategory value in Enum.GetValues<StressCategory>())
            {
                IStressTest? first = StressTestCatalog.ByCategory(value).FirstOrDefault();
                if (first is not null) selected.Add(first);
            }
        }

        if (count > 0 && selected.Count > count) selected = selected.Take(count).ToList();
        if (selected.Count == 0)
        {
            Console.WriteLine("Не выбрано ни одного теста.");
            return 2;
        }

        AppSettings settings = AppSettings.Load();
        var options = new StressOptions
        {
            MemoryMegabytes = Math.Clamp(GetInt(args, "--memory", settings.StressMemoryMegabytes), 64, 65536),
            DiskBytes = (long)Math.Clamp(GetInt(args, "--disk-mb", settings.StressDiskMegabytes), 16, 65536) * 1024 * 1024,
            DiskPath = GetValue(args, "--disk-path") ?? settings.StressDiskPath,
            Seed = Environment.TickCount,
        };

        string outPath = GetValue(args, "--out")
                         ?? Path.Combine(AppSettings.ReportsDirectory, "stress-report.txt");

        Console.WriteLine($"Прогон {selected.Count} тестов, память {options.MemoryMegabytes} МБ, " +
                          $"файл диска {options.DiskBytes / 1024 / 1024} МБ");
        Console.WriteLine();

        var runner = new StressTestRunner();
        var lines = new List<string>();
        long totalErrors = 0;
        var started = DateTime.Now;

        foreach (IStressTest test in selected)
        {
            Console.WriteLine($"— {test.Name} ({test.Category}), этапов {test.StageCount}…");
            var progress = new Progress<StressProgress>(p =>
                Console.WriteLine($"    этап {p.Stage}/{p.StageCount} ({p.Percent:0} %), ошибок {p.Errors}, " +
                                  $"скорость {p.Throughput:0.0}"));

            StressTestResult result = runner.RunAsync(test, options, progress, CancellationToken.None)
                .GetAwaiter().GetResult();

            totalErrors += result.TotalErrors;
            string verdict = result.Passed ? "успешно" : $"ОШИБКИ ({result.TotalErrors})";
            Console.WriteLine($"    итог: {verdict}, средняя скорость {result.AverageThroughput:0.0}, " +
                              $"время {result.Duration:mm\\:ss}");
            lines.Add($"{test.Id};{test.Name};{test.Category};{test.StageCount};{result.TotalErrors};" +
                      $"{result.AverageThroughput:0.###};{result.Duration.TotalSeconds:0.###};{verdict};{result.Summary}");
        }

        var report = new StringBuilder();
        report.AppendLine("Отчёт по стресс-тестам — Мониторинг ПК");
        report.AppendLine(new string('=', 90));
        report.AppendLine($"Начало: {started:yyyy-MM-dd HH:mm:ss}   Окончание: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"Тестов: {selected.Count}   Всего ошибок: {totalErrors}");
        report.AppendLine($"Память тестов: {options.MemoryMegabytes} МБ, файл диска: {options.DiskBytes / 1024 / 1024} МБ");
        report.AppendLine();
        report.AppendLine("id;тест;категория;этапов;ошибок;средняя скорость;секунд;итог;примечание");
        foreach (string line in lines) report.AppendLine(line);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllText(outPath, report.ToString(), new UTF8Encoding(false));
            Console.WriteLine();
            Console.WriteLine("Отчёт сохранён: " + outPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Не удалось сохранить отчёт: " + ex.Message);
            return 3;
        }

        return totalErrors == 0 ? 0 : 1;
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
