using Microsoft.Win32;

namespace PcMonitor.Core;

/// <summary>
/// Автозапуск приложения при входе в Windows: запись в раздел Run текущего пользователя.
/// Права администратора для этого не нужны, а путь берётся у самого приложения —
/// поэтому автозапуск продолжает работать даже после переноса папки.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PcMonitor";

    /// <summary>Путь к запущенному файлу приложения.</summary>
    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "PcMonitor.exe");

    /// <summary>Включён ли автозапуск.</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Что записано в автозапуске сейчас.</summary>
    public static string CurrentCommand
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>Включает или выключает автозапуск. Возвращает описание результата.</summary>
    public static string Set(bool enabled, bool startInTray = true, bool elevated = false)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, true)
                                    ?? throw new InvalidOperationException("раздел автозапуска недоступен");

            if (!enabled)
            {
                key.DeleteValue(ValueName, false);
                return "Автозапуск выключен";
            }

            string path = ExecutablePath;
            if (!File.Exists(path)) return "Не удалось определить путь к приложению";

            // Ключ --tray означает «запуститься сразу в трее, без окна».
            string command = $"\"{path}\"" + (startInTray ? " --tray" : string.Empty);
            key.SetValue(ValueName, command, RegistryValueKind.String);

            string suffix = elevated
                ? " Приложение запросит права администратора при входе — появится запрос UAC."
                : string.Empty;

            return $"Автозапуск включён: {command}.{suffix}";
        }
        catch (Exception ex)
        {
            return "Не удалось изменить автозапуск: " + ex.Message;
        }
    }
}
