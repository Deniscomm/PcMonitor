using LibreHardwareMonitor.Hardware;

namespace PcMonitor.Core;

/// <summary>Одно показание датчика в момент опроса.</summary>
public sealed class SensorReading
{
    public required string Id { get; init; }

    /// <summary>Полное имя устройства с учётом родителя (для отображения).</summary>
    public required string Hardware { get; init; }

    /// <summary>Собственное имя устройства без имени родителя (для поиска и группировки).</summary>
    public required string OwnName { get; init; }

    public required string HardwareGroup { get; init; }
    public required HardwareType HardwareType { get; init; }
    public required SensorType SensorType { get; init; }
    public required string SensorName { get; init; }
    public float? Value { get; init; }
    public float Min { get; init; }
    public float Max { get; init; }

    public string Unit => SensorCatalog.Unit(SensorType);
}
