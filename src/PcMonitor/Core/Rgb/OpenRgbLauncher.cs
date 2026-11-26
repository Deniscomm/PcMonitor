using System.Diagnostics;
using Microsoft.Win32;

namespace PcMonitor.Core.Rgb;

/// <summary>
/// Запуск OpenRGB в режиме сервера: после этого приложение может само управлять
/// подсветкой (цвета и режимы) через SDK, а не только проверять «на глаз».
/// </summary>
public static class OpenRgbLauncher
{
    private static string? _path;

    /// <summary>Путь к OpenRGB.exe или null, если программа не найдена.</summary>
    public static string? Path
    {
        get
        {
            if (_path is not null) return _path.Length == 0 ? null : _path;

            foreach (string candidate in Candidates())
            {
                if (File.Exists(candidate))
                {
                    _path = candidate;
                    return _path;
                }
            }

            _path = string.Empty;
            return null;
        }
    }

    private static IEnumerable<string> Candidates()
    {
        // Частые места установки и папка, откуда программу запускают вручную.
        yield return @"D:\Visual\OpwnRgb\OpenRGB.exe";
        yield return @"D:\Visual\OpenRgb\OpenRGB.exe";
        yield return @"C:\Program Files\OpenRGB\OpenRGB.exe";
        yield return @"C:\Program Files (x86)\OpenRGB\OpenRGB.exe";

        foreach (string drive in new[] { "C:", "D:", "E:" })
        {
            yield return $@"{drive}\OpenRGB\OpenRGB.exe";
            yield return $@"{drive}\OpenRGB Windows 64-bit\OpenRGB.exe";
        }

        string? fromRegistry = FindInRegistry();
        if (fromRegistry is not null) yield return fromRegistry;
    }

    private static string? FindInRegistry()
    {
        try
        {
            foreach (string root in new[]
                     {
                         @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                         @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                     })
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(root);
                if (key is null) continue;

                foreach (string sub in key.GetSubKeyNames())
                {
                    using RegistryKey? app = key.OpenSubKey(sub);
                    string name = app?.GetValue("DisplayName") as string ?? string.Empty;
                    if (!name.Contains("OpenRGB", StringComparison.OrdinalIgnoreCase)) continue;

                    string location = app?.GetValue("InstallLocation") as string ?? string.Empty;
                    if (location.Length == 0 || !Directory.Exists(location)) continue;

                    string? exe = Directory.EnumerateFiles(location, "OpenRGB.exe", SearchOption.AllDirectories)
                        .FirstOrDefault();
                    if (exe is not null) return exe;
                }
            }
        }
        catch
        {
            // Реестр недоступен — считаем, что через него найти не удалось.
        }

        return null;
    }

    /// <summary>
    /// Запускает OpenRGB как сервер и ждёт готовности порта.
    /// Возвращает описание результата.
    /// </summary>
    public static string StartServer(int port = OpenRgbClient.DefaultPort, int timeoutSeconds = 25)
    {
        if (OpenRgbClient.IsServerRunning(port))
            return $"Сервер OpenRGB уже работает на порту {port}";

        string? path = Path;
        if (path is null)
            return "OpenRGB не найден. Установите OpenRGB, чтобы приложение могло управлять подсветкой напрямую.";

        try
        {
            // --server поднимает SDK-сервер, --startminimized не мешает работе окном.
            var info = new ProcessStartInfo(path, $"--server --startminimized --server-port {port}")
            {
                UseShellExecute = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(path) ?? string.Empty,
            };

            Process.Start(info);

            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
            {
                if (OpenRgbClient.IsServerRunning(port))
                    return $"Сервер OpenRGB запущен: {path} (порт {port})";

                Thread.Sleep(500);
            }

            return "OpenRGB запущен, но сервер не ответил. Проверьте, что программа не требует подтверждения прав.";
        }
        catch (Exception ex)
        {
            return "Не удалось запустить OpenRGB: " + ex.Message;
        }
    }
}
