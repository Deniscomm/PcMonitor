using PcMonitor.Core.Rgb;

namespace PcMonitor.Core;

/// <summary>
/// Командная строка для подсветки: определить, чем можно управлять, включить сервер
/// OpenRGB и задать цвет. Нужна и для проверки на реальном железе.
/// </summary>
internal static class RgbCli
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
                Подсветка — Мониторинг ПК.

                  PcMonitor.exe --rgb                  определить, чем можно управлять
                  PcMonitor.exe --rgb --start          запустить сервер OpenRGB
                  PcMonitor.exe --rgb --color FF0000   задать цвет всем устройствам (RRGGBB)
                  PcMonitor.exe --rgb --off            погасить подсветку
                  PcMonitor.exe --rgb --out file.txt   сохранить отчёт
                """);
            return 0;
        }

        Write("Управление подсветкой — Мониторинг ПК");
        Write(new string('=', 100));

        string? path = OpenRgbLauncher.Path;
        Write($"OpenRGB: {(path is null ? "не найден" : path)}");
        Write($"Сервер на порту {OpenRgbClient.DefaultPort}: " +
              (OpenRgbClient.IsServerRunning() ? "работает" : "не запущен"));

        if (args.Any(a => a is "--start"))
        {
            Write(OpenRgbLauncher.StartServer());
        }
        else if (!OpenRgbClient.IsServerRunning() && args.Any(a => a is "--color" or "--off"))
        {
            Write(OpenRgbLauncher.StartServer());
        }

        (IReadOnlyList<OpenRgbDevice> devices, string diagnostics) = OpenRgbClient.Enumerate();
        Write(diagnostics);
        Write(string.Empty);

        // Дамп сырых данных устройства: нужен, чтобы точно знать раскладку протокола.
        string? dump = Value(args, "--dump");
        if (dump is not null && int.TryParse(dump, out int dumpIndex))
        {
            Write(OpenRgbClient.DumpDevice(dumpIndex));
            return Save(log, args);
        }

        if (devices.Count == 0)
        {
            Write("Устройств, которыми можно управлять, не найдено.");
            Write("Так бывает, если сервер OpenRGB не запущен, устройства не поддерживаются " +
                  "или подсветкой управляет только утилита производителя.");
            return Save(log, args);
        }

        Write("Устройства и их возможности:");
        foreach (OpenRgbDevice device in devices)
        {
            Write($"  [{device.Index}] {device.Display}");
            Write($"      режимы: {(device.Modes.Count == 0 ? "не определены" : string.Join(", ", device.Modes))}" +
                  $"; прямое управление цветом: {(device.SupportsDirect ? "да" : "нет")}");
        }

        string? color = Value(args, "--color");
        bool off = args.Any(a => a is "--off");

        if (color is not null && color.Length == 6 &&
            byte.TryParse(color[..2], System.Globalization.NumberStyles.HexNumber, null, out byte red) &&
            byte.TryParse(color[2..4], System.Globalization.NumberStyles.HexNumber, null, out byte green) &&
            byte.TryParse(color[4..], System.Globalization.NumberStyles.HexNumber, null, out byte blue))
        {
            Write(string.Empty);
            foreach (OpenRgbDevice device in devices)
                Write("  " + OpenRgbClient.SetColorByCommand(device, red, green, blue));
        }
        else if (off)
        {
            Write(string.Empty);
            foreach (OpenRgbDevice device in devices)
                Write("  " + OpenRgbClient.SetColorByCommand(device, 0, 0, 0));
        }

        return Save(log, args);
    }

    private static int Save(System.Text.StringBuilder log, string[] args)
    {
        string? outPath = Value(args, "--out");
        if (outPath is null) return 0;

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
