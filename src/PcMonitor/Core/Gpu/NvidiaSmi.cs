using System.Diagnostics;
using System.Globalization;

namespace PcMonitor.Core.Gpu;

/// <summary>Текущие параметры видеокарты, полученные от драйвера через nvidia-smi.</summary>
public sealed record NvidiaGpuStatus(
    bool Available,
    string Name,
    double PowerLimitWatts,
    double PowerDefaultWatts,
    double PowerMinWatts,
    double PowerMaxWatts,
    int CoreClockMhz,
    int MemoryClockMhz,
    int TemperatureC,
    int FanPercent,
    string Diagnostics)
{
    public string PowerRangeText => Available ? $"{PowerMinWatts:0} … {PowerMaxWatts:0} Вт" : "—";
}

/// <summary>
/// Официальное управление питанием и частотами видеокарты NVIDIA через nvidia-smi:
/// лимит мощности и блокировка частот. Работает без недокументированных вызовов,
/// поэтому результат сразу виден и проверяется повторным чтением.
/// </summary>
public static class NvidiaSmi
{
    private static string? _path;

    /// <summary>Путь к nvidia-smi (ищется рядом с драйвером и в PATH).</summary>
    public static string? Path
    {
        get
        {
            if (_path is not null) return _path.Length == 0 ? null : _path;

            foreach (string candidate in new[]
                     {
                         System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe"),
                         System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                             @"NVIDIA Corporation\NVSMI\nvidia-smi.exe"),
                     })
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

    /// <summary>Читает состояние: лимиты мощности, частоты, температуру и вентилятор.</summary>
    public static NvidiaGpuStatus Read()
    {
        string? smi = Path;
        if (smi is null)
            return new NvidiaGpuStatus(false, "NVIDIA GPU", 0, 0, 0, 0, 0, 0, 0, 0, "nvidia-smi не найдена");

        string? output = Run(smi,
            "--query-gpu=name,power.limit,power.default_limit,power.min_limit,power.max_limit," +
            "clocks.gr,clocks.mem,temperature.gpu,fan.speed --format=csv,noheader,nounits");

        if (output is null)
            return new NvidiaGpuStatus(false, "NVIDIA GPU", 0, 0, 0, 0, 0, 0, 0, 0, "не удалось опросить драйвер");

        string[] parts = output.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 9)
            return new NvidiaGpuStatus(false, "NVIDIA GPU", 0, 0, 0, 0, 0, 0, 0, 0,
                "неожиданный ответ драйвера: " + output);

        return new NvidiaGpuStatus(
            true,
            parts[0],
            Number(parts[1]),
            Number(parts[2]),
            Number(parts[3]),
            Number(parts[4]),
            (int)Number(parts[5]),
            (int)Number(parts[6]),
            (int)Number(parts[7]),
            (int)Number(parts[8]),
            "драйвер отвечает");
    }

    /// <summary>Устанавливает лимит мощности в ваттах (в пределах, сообщённых драйвером).</summary>
    public static string SetPowerLimit(double watts)
    {
        NvidiaGpuStatus status = Read();
        if (!status.Available) return status.Diagnostics;

        if (watts < status.PowerMinWatts || watts > status.PowerMaxWatts)
        {
            return $"Драйвер допускает лимит от {status.PowerMinWatts:0} до {status.PowerMaxWatts:0} Вт";
        }

        string? smi = Path;
        if (smi is null) return "nvidia-smi не найдена";

        string? output = Run(smi, $"-pl {watts.ToString("0", CultureInfo.InvariantCulture)}", captureError: true);
        NvidiaGpuStatus after = Read();

        if (Math.Abs(after.PowerLimitWatts - watts) > 0.6)
        {
            return $"Драйвер не принял лимит {watts:0} Вт (сейчас {after.PowerLimitWatts:0} Вт)" +
                   (string.IsNullOrWhiteSpace(output) ? string.Empty : $": {output.Trim()}");
        }

        return $"Лимит мощности: {status.PowerLimitWatts:0} Вт → {after.PowerLimitWatts:0} Вт";
    }

    /// <summary>Блокирует частоту ядра в указанном диапазоне (МГц).</summary>
    public static string LockCoreClocks(int minimumMhz, int maximumMhz)
    {
        NvidiaGpuStatus status = Read();
        if (!status.Available) return status.Diagnostics;

        string? smi = Path;
        if (smi is null) return "nvidia-smi не найдена";

        string? output = Run(smi, $"-lgc {minimumMhz},{maximumMhz}", captureError: true);
        NvidiaGpuStatus after = Read();
        return $"Частота ядра: диапазон {minimumMhz}…{maximumMhz} МГц задан " +
               $"(драйвер сообщает {after.CoreClockMhz} МГц)" +
               (string.IsNullOrWhiteSpace(output) ? string.Empty : $" · {output.Trim()}");
    }

    /// <summary>Снимает блокировку частот ядра и памяти, возвращая авторежим.</summary>
    public static string ResetClocks()
    {
        string? smi = Path;
        if (smi is null) return "nvidia-smi не найдена";

        string first = Run(smi, "-rgc", captureError: true) ?? string.Empty;
        string second = Run(smi, "-rmc", captureError: true) ?? string.Empty;
        NvidiaGpuStatus after = Read();

        return $"Авторежим частот возвращён (ядро {after.CoreClockMhz} МГц, память {after.MemoryClockMhz} МГц)" +
               (first.Length + second.Length > 0 ? " · " + (first + " " + second).Trim() : string.Empty);
    }

    /// <summary>Сведения о поддержке блокировки памяти.</summary>
    public static string MemoryLockInfo()
    {
        string? smi = Path;
        return smi is null ? "nvidia-smi не найдена" : (Run(smi, "-lmci", captureError: true) ?? "нет сведений").Trim();
    }

    private static double Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;

    private static string? Run(string executable, string arguments, bool captureError = false)
    {
        try
        {
            var info = new ProcessStartInfo(executable, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using Process? process = Process.Start(info);
            if (process is null) return null;

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(15000);

            if (process.ExitCode != 0 && captureError)
                return string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;

            return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
        }
        catch (Exception ex)
        {
            return captureError ? ex.Message : null;
        }
    }
}
