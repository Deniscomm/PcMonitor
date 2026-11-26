namespace PcMonitor.Core;

/// <summary>Управляемый вентилятор: то, чем приложение может управлять напрямую.</summary>
public sealed record FanControlInfo(
    string Id,
    string Hardware,
    string Name,
    string Mode,
    float Value,
    float? Rpm = null,
    string RpmSensorId = "",
    string Status = "")
{
    public string Display => $"{Hardware} · {Name}";

    public string ModeText => Mode.Contains("Software", StringComparison.OrdinalIgnoreCase)
        ? "ручной"
        : Mode.Contains("Default", StringComparison.OrdinalIgnoreCase)
            ? "автоматический"
            : Mode;

    public double Percent => Math.Clamp(Value, 0, 100);
}
