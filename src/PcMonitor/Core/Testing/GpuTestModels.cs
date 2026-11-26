using System.IO;
using System.Text.Json;

namespace PcMonitor.Core;

/// <summary>Результат одного этапа GPU-теста.</summary>
public sealed record GpuStageResult(
    int Stage,
    double FpsAverage,
    double FpsMinimum,
    double TemperatureMax,
    double PowerMax,
    long ArtifactPixels,
    TimeSpan Duration);

/// <summary>Полный прогон GPU-теста (стресс или бенчмарк).</summary>
public sealed class GpuTestRun
{
    public DateTime Started { get; set; } = DateTime.Now;
    public string Mode { get; set; } = "Стресс-тест";
    public string Adapter { get; set; } = string.Empty;
    public int LoadLevel { get; set; } = 5;
    public double DurationSeconds { get; set; }
    public double FpsAverage { get; set; }
    public double FpsMinimum { get; set; }
    public double FpsMaximum { get; set; }
    public double TemperatureMaximum { get; set; }
    public double TemperatureAverage { get; set; }
    public double PowerMaximum { get; set; }
    public double ClockMaximum { get; set; }
    public long Artifacts { get; set; }
    public int Score { get; set; }
    public bool Passed { get; set; }
    public bool RunUntilStopped { get; set; }
    public bool Cancelled { get; set; }
    public bool ThermalStopped { get; set; }
    public string Summary { get; set; } = string.Empty;
    public List<GpuStageResult> Stages { get; set; } = new();

    public string StartedText => Started.ToString("dd.MM.yyyy HH:mm:ss");
    public string DurationText => Stress.StressRunPolicy.FormatElapsed(TimeSpan.FromSeconds(DurationSeconds));
    public string ModeText => Mode;
    public string ResultText => ThermalStopped ? "защитная остановка" : Cancelled ? "остановлено" : Passed ? "успешно" : "ошибки/перегрев";
}

/// <summary>Хранение истории GPU-прогонов рядом с данными приложения.</summary>
public static class GpuTestStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Path => System.IO.Path.Combine(AppSettings.HistoryDirectory, "gpu-tests.json");

    public static List<GpuTestRun> Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<List<GpuTestRun>>(File.ReadAllText(Path), Options) ?? new List<GpuTestRun>();
        }
        catch
        {
            // Повреждённая история не должна ломать приложение.
        }

        return new List<GpuTestRun>();
    }

    public static void Save(IEnumerable<GpuTestRun> runs)
    {
        try
        {
            File.WriteAllText(Path, JsonSerializer.Serialize(runs.Take(60).ToList(), Options));
        }
        catch
        {
            // История — не критичные данные.
        }
    }
}
