using PcMonitor.Core;

namespace PcMonitor.Core;

/// <summary>
/// Командная строка для вентиляторов: показать управляемые, задать обороты, вернуть авторежим.
/// </summary>
internal static class FansCli
{
    public static int Run(string[] args)
    {
        ConsoleHelper.Attach();

        var log = new System.Text.StringBuilder();
        void Write(string line)
        {
            Console.WriteLine(line);
            log.AppendLine(line);
        }

        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            Console.WriteLine(
                """
                Вентиляторы — Мониторинг ПК.

                  PcMonitor.exe --fans                 показать управляемые вентиляторы
                  PcMonitor.exe --fans --set 50        задать обороты всем, проценты
                  PcMonitor.exe --fans --auto          вернуть автоматический режим всем
                  PcMonitor.exe --fans --out file.txt  сохранить отчёт
                """);
            return 0;
        }

        AppSettings settings = AppSettings.Load();
        using var service = new MonitorService { Interval = TimeSpan.FromSeconds(1) };

        // Последние показания датчиков: по ним видно реальные обороты вентиляторов.
        IReadOnlyList<SensorReading> latest = Array.Empty<SensorReading>();
        service.SnapshotReady += (readings, _) => latest = readings;
        service.Start();

        string FanSpeeds()
        {
            List<string> speeds = latest
                .Where(r => r.SensorType == LibreHardwareMonitor.Hardware.SensorType.Fan && r.Value.HasValue)
                .Select(r => $"{r.SensorName} {r.Value:0}")
                .ToList();

            return speeds.Count == 0 ? "датчики оборотов не отдали значения" : string.Join(", ", speeds);
        }

        // Контроллер платы отвечает не мгновенно: даём ему до минуты.
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (service.FanControls().Count == 0 && wait.Elapsed < TimeSpan.FromSeconds(60))
            Thread.Sleep(500);

        IReadOnlyList<FanControlInfo> fans = service.FanControls();
        Write($"Управляемых вентиляторов: {fans.Count}");
        Write($"Права администратора: {(service.IsElevated ? "да" : "нет")}");
        Write("Обороты до команды: " + FanSpeeds());
        Write(new string('=', 100));

        foreach (FanControlInfo fan in fans)
        {
            Write($"{fan.Display,-58} режим {fan.ModeText,-14} задано {fan.Value:0} %");
        }

        if (fans.Count == 0)
        {
            Write("Контроллер не отдал управляемых каналов. Проверьте права администратора и драйвер PawnIO.");
        }

        string? set = Value(args, "--set");
        if (set is not null && float.TryParse(set, out float percent))
        {
            Write(string.Empty);
            Write($"Задаём {percent:0} % всем каналам…");
            foreach (FanControlInfo fan in fans) Write("  " + service.SetFanPercent(fan.Id, percent));

            // Ждём минуту и смотрим, изменились ли обороты (пользователь просил проверять минуту).
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var before = new Dictionary<string, float>();
            foreach (FanControlInfo fan in service.FanControls()) before[fan.Id] = fan.Value;

            while (watch.Elapsed < TimeSpan.FromSeconds(60))
            {
                Thread.Sleep(3000);
                var now = service.FanControls();
                Write($"  через {watch.Elapsed.TotalSeconds:0} с: обороты — {FanSpeeds()}");
            }

            Write(string.Empty);
            Write("Итог по каналам:");
            foreach (FanControlInfo fan in service.FanControls())
            {
                bool changed = before.TryGetValue(fan.Id, out float old) && Math.Abs(old - fan.Value) > 0.5;
                Write($"  {fan.Display}: {fan.Value:0} % ({fan.ModeText})" +
                      (changed ? $" — значение изменилось с {before[fan.Id]:0} %" : " — значение не изменилось"));
            }

            Write(string.Empty);
            Write("Обороты после команды: " + FanSpeeds());
        }

        if (args.Any(a => a is "--auto"))
        {
            Write(string.Empty);
            Write(service.RestoreAllFans());
        }

        string? outPath = Value(args, "--out");
        if (outPath is not null)
        {
            try
            {
                string full = Path.GetFullPath(outPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, log.ToString(), new System.Text.UTF8Encoding(false));
                Console.WriteLine("Отчёт сохранён: " + full);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Не удалось сохранить отчёт: " + ex.Message);
            }
        }

        service.Stop();
        return 0;
    }

    private static string? Value(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }

        return null;
    }
}
