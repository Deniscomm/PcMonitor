namespace PcMonitor.Core.Gpu;

/// <summary>Пресет качества (как в сцены отражений: чем выше, тем тяжелее сцена).</summary>
public enum GpuPreset
{
    Low,
    Medium,
    High,
    Extreme,
}

/// <summary>Сведения о видеодаптере.</summary>
public sealed record GpuAdapterInfo(
    int Index,
    string Name,
    long DedicatedVideoMemoryBytes,
    uint VendorId,
    uint DeviceId,
    bool IsSoftware)
{
    public string MemoryText => DedicatedVideoMemoryBytes <= 0
        ? "объём памяти неизвестен"
        : $"{DedicatedVideoMemoryBytes / 1024.0 / 1024 / 1024:0.#} ГБ видеопамяти";
}

/// <summary>Прогресс GPU-теста для интерфейса.</summary>
public sealed record GpuTestProgress(
    string Stage,
    int StageIndex,
    int StageCount,
    double Percent,
    double Value,
    string ValueUnit,
    long Errors,
    string Status);

/// <summary>Результат одного GPU-теста.</summary>
public sealed record GpuTestResult(
    string TestName,
    string SceneName,
    GpuPreset Preset,
    int Score,
    double AverageFps,
    double MinimumFps,
    double MaximumFps,
    double GpuTimeMs,
    long Frames,
    int Width,
    int Height,
    TimeSpan Duration,
    long Errors,
    string Verdict,
    string Details)
{
    public string ResolutionText => $"{Width}×{Height}";
}

/// <summary>Описание сцены для интерфейса.</summary>
public sealed record GpuSceneInfo(string Name, string Title, string Description, bool IsMultiPass);
