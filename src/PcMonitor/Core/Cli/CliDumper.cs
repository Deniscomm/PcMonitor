using System.IO;
using System.Text;
using System.Text.Json;

namespace PcMonitor.Core;

/// <summary>Режим командной строки: разовый отчёт по всем датчикам (--dump / --json).</summary>
internal static class CliDumper
{
    public static int Run(string[] args)
    {
        ConsoleHelper.Attach();

        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            PrintHelp();
            return 0;
        }

        int seconds = GetInt(args, "--seconds", 3);
        bool json = args.Contains("--json");
        string? explicitOut = GetValue(args, "--out");
        string outPath = explicitOut ?? Path.Combine(
            AppSettings.ReportsDirectory,
            json ? "last-dump.json" : "last-dump.txt");

        Console.WriteLine("Опрос датчиков…");
        IReadOnlyList<SensorReading> readings = Array.Empty<SensorReading>();
        string? error = null;

        using var service = new MonitorService { Interval = TimeSpan.FromMilliseconds(600) };
        service.Failed += message => error = message;
        service.SnapshotReady += (snapshot, _) => readings = snapshot;
        service.Start();

        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(seconds, 1, 600));
        while (DateTime.UtcNow < deadline && error is null)
            Thread.Sleep(100);

        service.Stop();

        if (error is not null)
        {
            Console.WriteLine("Ошибка: " + error);
            return 2;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            string content = json ? BuildJson(readings, service.IsElevated) : BuildText(readings, service.IsElevated);
            File.WriteAllText(outPath, content, new UTF8Encoding(false));
            Console.WriteLine(content);
            Console.WriteLine();
            Console.WriteLine("Отчёт сохранён: " + outPath);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Не удалось сохранить отчёт: " + ex.Message);
            return 3;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
            """
            Мониторинг ПК — режим командной строки.

            Использование:
              PcMonitor.exe                 запустить окно мониторинга
              PcMonitor.exe --dump          отчёт по всем датчикам в консоль и в файл
              PcMonitor.exe --dump --json   то же в формате JSON
              PcMonitor.exe --dump --seconds 5   сколько секунд собирать минимум/максимум
              PcMonitor.exe --dump --out C:\path\report.txt

            Датчики температуры и напряжений читаются через kernel-драйвер,
            поэтому приложение запрашивает права администратора.
            """);
    }

    private static string BuildText(IReadOnlyList<SensorReading> readings, bool elevated)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Мониторинг ПК — отчёт по датчикам");
        sb.AppendLine(new string('=', 110));
        sb.AppendLine($"Время: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Права администратора: {(elevated ? "да" : "НЕТ — часть датчиков недоступна")}");

        int withValue = readings.Count(r => r.Value.HasValue);
        var hardware = readings.Select(r => r.Hardware).Distinct().Count();
        sb.AppendLine($"Устройств: {hardware}   Датчиков: {readings.Count}   С данными: {withValue}");
        sb.AppendLine();
        sb.AppendLine($"{"Тип",-16}{"Датчик",-42}{"Значение",16}{"Мин",10}{"Макс",10}");
        sb.AppendLine(new string('-', 110));

        foreach (var group in readings.GroupBy(r => r.Hardware))
        {
            sb.AppendLine($"[{group.First().HardwareGroup}] {group.Key}");
            foreach (SensorReading sensor in group
                         .OrderBy(r => SensorCatalog.TypeOrder(r.SensorType))
                         .ThenBy(r => r.SensorName, StringComparer.CurrentCulture))
            {
                string value = sensor.Value.HasValue ? SensorCatalog.Format(sensor.SensorType, sensor.Value.Value) : "—";
                string min = sensor.Min != 0 || sensor.Max != 0 ? SensorCatalog.Format(sensor.SensorType, sensor.Min) : "—";
                string max = sensor.Min != 0 || sensor.Max != 0 ? SensorCatalog.Format(sensor.SensorType, sensor.Max) : "—";
                sb.AppendLine($"  {SensorCatalog.TypeName(sensor.SensorType),-14}{Truncate(sensor.SensorName, 40),-42}{value,16}{min,10}{max,10}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Итого по типам:");
        foreach (var group in readings.GroupBy(r => r.SensorType).OrderBy(g => SensorCatalog.TypeOrder(g.Key)))
            sb.AppendLine($"  {SensorCatalog.TypeName(group.Key),-18}{group.Count(),5}");

        return sb.ToString();
    }

    private static string BuildJson(IReadOnlyList<SensorReading> readings, bool elevated)
    {
        var payload = new
        {
            timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            elevated,
            hardware = readings.Select(r => r.Hardware).Distinct().Count(),
            sensorCount = readings.Count,
            sensors = readings.Select(r => new
            {
                hardware = r.Hardware,
                group = r.HardwareGroup,
                type = r.SensorType.ToString(),
                typeName = SensorCatalog.TypeName(r.SensorType),
                sensor = r.SensorName,
                value = r.Value,
                unit = r.Unit,
                min = r.Min,
                max = r.Max,
                id = r.Id,
            }),
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    private static int GetInt(string[] args, string name, int fallback)
    {
        string? raw = GetValue(args, name);
        return raw is not null && int.TryParse(raw, out int value) ? value : fallback;
    }

    private static string? GetValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
