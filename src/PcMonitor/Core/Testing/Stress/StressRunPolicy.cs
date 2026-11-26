namespace PcMonitor.Core.Stress;

/// <summary>Политика длительности: null означает отсутствие дедлайна, а не большой таймаут.</summary>
public static class StressRunPolicy
{
    public static bool ShouldContinue(bool untilStopped, TimeSpan elapsed, TimeSpan duration,
        CancellationToken token) => !token.IsCancellationRequested && (untilStopped || elapsed < duration);

    public static string FormatElapsed(TimeSpan elapsed) =>
        $"{(long)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
}

/// <summary>Термостоп нельзя отключить выбором бессрочного режима.</summary>
public sealed class StressThermalGuard
{
    public const double CpuLimit = 95;
    public const double GpuLimit = 85;
    private bool _cpuSeen;
    private bool _gpuSeen;

    public string? Observe(double? cpu, double? gpu, bool fresh)
    {
        if (!fresh) cpu = gpu = null;
        if (cpu >= CpuLimit || gpu >= GpuLimit)
            return $"Термостоп: CPU {cpu:0.0} °C / GPU {gpu:0.0} °C (пределы {CpuLimit:0}/{GpuLimit:0} °C)";
        if ((_cpuSeen && !cpu.HasValue) || (_gpuSeen && !gpu.HasValue))
            return "Защитная остановка: температурные датчики пропали или старше 15 секунд";
        _cpuSeen |= cpu.HasValue;
        _gpuSeen |= gpu.HasValue;
        return null;
    }
}
