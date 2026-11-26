using Microsoft.Win32;

namespace PcMonitor.Core;

/// <summary>Установленная программа управления подсветкой.</summary>
public sealed record RgbSoftware(string Name, string ExecutablePath, string Note)
{
    public bool CanLaunch => ExecutablePath.Length > 0 && File.Exists(ExecutablePath);
}

/// <summary>
/// Поиск установленных программ управления подсветкой: через их можно переключать
/// режимы и цвета, а мастер проверки подсветки затем подтверждает результат.
/// </summary>
public static class RgbSoftwareDetector
{
    private static readonly (string Name, string Note, string[] Markers)[] Known =
    {
        ("OpenRGB", "Универсальное управление подсветкой (OpenRGB). Умеет менять цвета всех устройств.", new[] { "openrgb" }),
        ("MSI Center / Mystic Light", "Подсветка плат и устройств MSI.", new[] { "msi center", "mystic light", "msi mystic" }),
        ("ASUS Armoury Crate / Aura", "Подсветка ASUS Aura.", new[] { "armoury crate", "aura creator", "asus aura" }),
        ("Gigabyte RGB Fusion / Control Center", "Подсветка Gigabyte.", new[] { "rgb fusion", "gigabyte control center" }),
        ("ASRock Polychrome", "Подсветка ASRock.", new[] { "polychrome" }),
        ("Corsair iCUE", "Подсветка Corsair.", new[] { "icue" }),
        ("NZXT CAM", "Подсветка NZXT.", new[] { "nzxt cam" }),
        ("Razer Synapse", "Подсветка Razer.", new[] { "razer synapse" }),
        ("Logitech G HUB", "Подсветка Logitech.", new[] { "g hub", "lghub" }),
        ("FanControl", "Управление вентиляторами; часть устройств умеет и подсветку через плагины.", new[] { "fancontrol" }),
    };

    /// <summary>Ищет исполняемый файл установленной программы по части названия.</summary>
    public static string? FindExecutable(string nameMarker)
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
                    if (app is null) continue;

                    string name = app.GetValue("DisplayName") as string ?? string.Empty;
                    string location = app.GetValue("InstallLocation") as string ?? string.Empty;
                    if (location.Length == 0 || !Directory.Exists(location)) continue;
                    if (!name.Contains(nameMarker, StringComparison.OrdinalIgnoreCase) &&
                        !sub.Contains(nameMarker, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string marker = nameMarker.Replace(" ", string.Empty);
                    foreach (string file in Directory.EnumerateFiles(location, "*.exe", SearchOption.AllDirectories).Take(400))
                    {
                        if (Path.GetFileNameWithoutExtension(file).Contains(marker, StringComparison.OrdinalIgnoreCase))
                            return file;
                    }

                    string? first = Directory.EnumerateFiles(location, "*.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (first is not null) return first;
                }
            }
        }
        catch
        {
            // Реестр или папка недоступны — считаем, что программа не найдена.
        }

        return null;
    }

    /// <summary>Возвращает список найденных программ подсветки.</summary>
    public static IReadOnlyList<RgbSoftware> Detect()
    {
        var found = new List<RgbSoftware>();
        var installed = new List<(string Name, string Path)>();

        foreach (string root in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                     @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                 })
        {
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(root);
                if (key is null) continue;

                foreach (string sub in key.GetSubKeyNames())
                {
                    using RegistryKey? app = key.OpenSubKey(sub);
                    if (app is null) continue;

                    string name = app.GetValue("DisplayName") as string ?? string.Empty;
                    string location = app.GetValue("InstallLocation") as string ?? string.Empty;
                    if (name.Length > 0 && location.Length > 0) installed.Add((name, location));
                }
            }
            catch
            {
                // Реестр может быть недоступен — просто пропускаем ветку.
            }
        }

        foreach ((string name, string note, string[] markers) in Known)
        {
            foreach ((string installedName, string location) in installed)
            {
                if (!markers.Any(m => installedName.Contains(m, StringComparison.OrdinalIgnoreCase)))
                    continue;

                string executable = FindExecutable(location, markers);
                found.Add(new RgbSoftware(installedName, executable, note));
                break;
            }
        }

        return found;
    }

    private static string FindExecutable(string location, string[] markers)
    {
        try
        {
            if (!Directory.Exists(location)) return string.Empty;

            foreach (string file in Directory.EnumerateFiles(location, "*.exe", SearchOption.AllDirectories).Take(400))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (markers.Any(m => name.Contains(m.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase)) ||
                    markers.Any(m => name.Contains(m.Split(' ')[0], StringComparison.OrdinalIgnoreCase)))
                {
                    return file;
                }
            }
        }
        catch
        {
            // Нет доступа к папке — вернём пустой путь.
        }

        return string.Empty;
    }
}
