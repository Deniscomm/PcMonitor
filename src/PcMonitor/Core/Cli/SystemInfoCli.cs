using System.Text;

namespace PcMonitor.Core;

/// <summary>
/// Режим командной строки: сведения о железе (--sysinfo) — то же, что показывает
/// вкладка «Система». Удобно для проверки и для сохранения отчёта о конфигурации.
/// </summary>
internal static class SystemInfoCli
{
    public static int Run(string[] args)
    {
        ConsoleHelper.Attach();

        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            Console.WriteLine(
                """
                Сведения о железе — Мониторинг ПК.

                  PcMonitor.exe --sysinfo                 все разделы в консоль
                  PcMonitor.exe --sysinfo --out report.txt   сохранить отчёт
                  PcMonitor.exe --sysinfo --section memory   только один раздел
                """);
            return 0;
        }

        // Датчики нужны для температур накопителей и таймингов SPD.
        AppSettings settings = AppSettings.Load();
        using var service = new MonitorService { Interval = TimeSpan.FromSeconds(1) };
        var context = new MonitorContext(settings, service);

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
        };

        service.Start();

        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (context.Rows.Count == 0 && wait.Elapsed < TimeSpan.FromSeconds(20))
            Thread.Sleep(200);

        IReadOnlyList<InfoSection> sections = SystemInfoService.Collect(context.Rows.ToList());
        service.Stop();

        string? filter = GetValue(args, "--section");
        var builder = new StringBuilder();
        builder.AppendLine("Сведения о компьютере — Мониторинг ПК");
        builder.AppendLine(new string('=', 100));
        builder.AppendLine($"Дата: {DateTime.Now:yyyy-MM-dd HH:mm:ss}   Датчиков: {context.Rows.Count}   " +
                           $"Права администратора: {(service.IsElevated ? "да" : "нет")}");
        builder.AppendLine();

        int shown = 0;
        foreach (InfoSection section in sections)
        {
            if (filter is not null && !section.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            shown++;

            builder.AppendLine($"### {section.Title}" + (section.Subtitle.Length > 0 ? $" — {section.Subtitle}" : string.Empty));
            builder.AppendLine(new string('-', 100));

            foreach (InfoItem item in section.Items)
                builder.AppendLine($"{item.Name,-38}{item.Value}");

            if (section.IsTable)
            {
                builder.AppendLine(string.Join(" | ", section.Columns));
                foreach (string[] row in section.Rows)
                    builder.AppendLine(string.Join(" | ", row));
            }

            builder.AppendLine();
        }

        string text = builder.ToString();
        Console.WriteLine(text);
        Console.WriteLine($"Разделов выведено: {shown}");

        string? outPath = GetValue(args, "--out");
        if (outPath is not null)
        {
            try
            {
                string full = Path.GetFullPath(outPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text, new UTF8Encoding(false));
                Console.WriteLine("Отчёт сохранён: " + full);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Не удалось сохранить отчёт: " + ex.Message);
                return 3;
            }
        }

        return 0;
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
