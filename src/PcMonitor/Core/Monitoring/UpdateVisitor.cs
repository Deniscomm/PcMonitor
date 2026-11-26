using LibreHardwareMonitor.Hardware;

namespace PcMonitor.Core;

/// <summary>
/// Обновляет показания железа. Отдельный посетитель нужен потому, что
/// в LibreHardwareMonitor обход дерева отделён от чтения значений.
/// </summary>
internal sealed class UpdateVisitor : IVisitor
{
    public static readonly UpdateVisitor Instance = new();

    public void VisitComputer(IComputer computer)
    {
        // Обход выполняется вручную в MonitorService.
    }

    public void VisitHardware(IHardware hardware)
    {
#pragma warning disable CS0618
        hardware.Update();
#pragma warning restore CS0618
    }

    public void VisitSensor(ISensor sensor) { }

    public void VisitParameter(IParameter parameter) { }
}
