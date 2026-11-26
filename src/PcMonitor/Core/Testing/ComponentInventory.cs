using System.Management;
using LibreHardwareMonitor.Hardware;

namespace PcMonitor.Core.Testing;

/// <summary>
/// Инвентаризация комплектующих: что за процессор, сколько модулей памяти,
/// какие накопители (NVMe/SATA/HDD), какая видеокарта, есть ли NPU,
/// сколько вентиляторов видно и есть ли устройства с подсветкой.
/// </summary>
public static class ComponentInventory
{
    public static List<ComponentInfo> Collect(IReadOnlyList<SensorRow> rows)
    {
        var list = new List<ComponentInfo>
        {
            Cpu(),
            Gpu(rows),
            Ram(rows),
            Motherboard(rows),
            Fans(rows),
            Npu(),
            Network(),
        };

        list.AddRange(Storage(rows));
        return list.Where(item => item is not null).Select(item => item!).ToList();
    }

    // ============================ Процессор ============================

    private static ComponentInfo Cpu()
    {
        Dictionary<string, object?>? cpu = Wmi.One("Win32_Processor");
        if (cpu is null) return new ComponentInfo(ComponentKind.Cpu, "Процессор", "не определён", false);

        string name = Wmi.Get(cpu, "Name");
        int cores = (int)Wmi.Double(cpu, "NumberOfCores");
        int threads = (int)Wmi.Double(cpu, "NumberOfLogicalProcessors");
        double baseClock = Wmi.Double(cpu, "MaxClockSpeed");
        double l3 = Wmi.Double(cpu, "L3CacheSize") / 1024;

        return new ComponentInfo(ComponentKind.Cpu, name,
            $"ядер {cores}, потоков {threads}, базовая частота {baseClock:0} МГц, кэш L3 {l3:0.#} МБ", true, threads);
    }

    // ============================ Видеокарта ============================

    private static ComponentInfo Gpu(IReadOnlyList<SensorRow> rows)
    {
        string? lhmName = rows.FirstOrDefault(r => r.HardwareGroup == SummaryBuilder.Gpu)?.Hardware;
        string? wmiName = Wmi.Get(Wmi.One("Win32_VideoController"), "Name");

        double? vram = rows.FirstOrDefault(r => r.HardwareGroup == SummaryBuilder.Gpu
            && r.Sensor.Contains("GPU Memory Total", StringComparison.OrdinalIgnoreCase))?.DisplayValue;

        string name = lhmName ?? (string.IsNullOrWhiteSpace(wmiName) ? "Видеокарта" : wmiName);
        string details = vram.HasValue ? $"видеопамять {vram.Value:0} МБ" : "объём видеопамяти неизвестен";
        return new ComponentInfo(ComponentKind.Gpu, name, details, true);
    }

    // ============================ Память ============================

    private static ComponentInfo Ram(IReadOnlyList<SensorRow> rows)
    {
        List<Dictionary<string, object?>> modules = Wmi.All("Win32_PhysicalMemory");
        double totalGb = Wmi.ToGb(Wmi.Long(Wmi.One("Win32_ComputerSystem"), "TotalPhysicalMemory"));

        if (modules.Count == 0)
            return new ComponentInfo(ComponentKind.Ram, "Оперативная память", $"всего {totalGb:0.#} ГБ", false);

        var speeds = modules.Select(m => (int)Wmi.Double(m, "ConfiguredClockSpeed")).Where(s => s > 0).ToList();
        var capacity = modules.Select(m => Wmi.ToGb(Wmi.Long(m, "Capacity"))).ToList();
        var types = modules.Select(m => Wmi.Get(m, "SMBIOSMemoryType")).Distinct().ToList();

        string typeText = types.Count == 1 ? MemoryTypeName(types[0]) : "разные типы";
        string details = $"{modules.Count} модул(я) по {string.Join(" + ", capacity.Select(c => $"{c:0.#}"))} ГБ, " +
                         $"{typeText}, фактическая частота {(speeds.Count > 0 ? speeds.Max() + " МГц" : "неизвестна")}";

        return new ComponentInfo(ComponentKind.Ram, $"Оперативная память ({modules.Count} шт.)", details, true, modules.Count);
    }

    // ============================ Мат. плата ============================

    private static ComponentInfo Motherboard(IReadOnlyList<SensorRow> rows)
    {
        Dictionary<string, object?>? board = Wmi.One("Win32_BaseBoard");
        Dictionary<string, object?>? bios = Wmi.One("Win32_BIOS");

        string name = $"{Wmi.Get(board, "Manufacturer")} {Wmi.Get(board, "Product")}".Trim();
        string details = $"BIOS {Wmi.Get(bios, "SMBIOSBIOSVersion")} от {Wmi.Date(bios, "ReleaseDate")}";

        int superIo = rows.Count(r => r.HardwareGroup == SummaryBuilder.Board);
        return new ComponentInfo(ComponentKind.Motherboard, name.Length > 0 ? name : "Материнская плата",
            $"{details}, датчиков платы: {superIo}", true);
    }

    // ============================ Вентиляторы ============================

    private static ComponentInfo Fans(IReadOnlyList<SensorRow> rows)
    {
        List<SensorRow> fans = rows.Where(r => r.Type == SensorType.Fan).ToList();
        int spinning = fans.Count(r => r.DisplayValue.HasValue && r.DisplayValue.Value > 0.5);
        var names = fans.Where(r => r.DisplayValue.HasValue && r.DisplayValue.Value > 0.5)
            .Select(r => r.DisplayName).Distinct().Take(8).ToList();

        string details = fans.Count == 0
            ? "датчики оборотов не найдены"
            : $"видно {fans.Count} датчик(ов), вращаются {spinning}: {(names.Count > 0 ? string.Join(", ", names) : "—")}";

        return new ComponentInfo(ComponentKind.Fans, "Вентиляторы", details, fans.Count > 0, fans.Count);
    }

    // ============================ NPU ============================

    private static ComponentInfo Npu()
    {
        // Нейромодули встречаются у Intel Core Ultra (NPU 3.x/4), AMD Ryzen AI и Snapdragon.
        string[] markers = { "npu", "neural", "ai boost", "ryzen ai", "hexagon", "vpu" };

        foreach (Dictionary<string, object?> device in Wmi.All("Win32_PnPEntity"))
        {
            string name = Wmi.Get(device, "Name");
            if (name.Length == 0) continue;
            if (markers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                return new ComponentInfo(ComponentKind.Npu, name, "нейромодуль обнаружен", true);
        }

        Dictionary<string, object?>? cpu = Wmi.One("Win32_Processor");
        string cpuName = Wmi.Get(cpu, "Name");
        if (cpuName.Contains("Core Ultra", StringComparison.OrdinalIgnoreCase) ||
            cpuName.Contains("Ryzen AI", StringComparison.OrdinalIgnoreCase))
        {
            return new ComponentInfo(ComponentKind.Npu, "NPU (в составе процессора)",
                "процессор содержит нейромодуль, отдельного устройства в системе нет", true);
        }

        return new ComponentInfo(ComponentKind.Npu, "NPU", "нейромодуль не обнаружен (в этой системе его нет)", false);
    }

    // ============================ Сеть ============================

    private static ComponentInfo Network()
    {
        List<Dictionary<string, object?>> adapters = Wmi.All("Win32_NetworkAdapterConfiguration", "IPEnabled=True");
        var names = adapters.Select(a => Wmi.Get(a, "Description")).Where(n => n.Length > 0).ToList();

        return new ComponentInfo(ComponentKind.Network,
            names.Count > 0 ? names[0] : "Сетевые адаптеры",
            names.Count > 1 ? $"активных адаптеров: {names.Count}" : "активный адаптер один",
            names.Count > 0, names.Count);
    }

    // ============================ Накопители ============================

    private static IEnumerable<ComponentInfo> Storage(IReadOnlyList<SensorRow> rows)
    {
        foreach (Dictionary<string, object?> disk in Wmi.All("Win32_DiskDrive"))
        {
            string model = Wmi.Get(disk, "Model");
            string interfaceType = Wmi.Get(disk, "InterfaceType");
            double sizeGb = Wmi.ToGb(Wmi.Long(disk, "Size"));
            string mediaType = Wmi.Get(disk, "MediaType");
            string serial = Wmi.Get(disk, "SerialNumber");

            bool isNvme = model.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ||
                          interfaceType.Contains("SCSI", StringComparison.OrdinalIgnoreCase) && model.Contains("NVMe", StringComparison.OrdinalIgnoreCase);

            ComponentKind kind = isNvme
                ? ComponentKind.StorageNvme
                : mediaType.Contains("Removable", StringComparison.OrdinalIgnoreCase) || interfaceType.Contains("USB", StringComparison.OrdinalIgnoreCase)
                    ? ComponentKind.StorageSata
                    : mediaType.Contains("Fixed", StringComparison.OrdinalIgnoreCase) && !model.Contains("SSD", StringComparison.OrdinalIgnoreCase) && sizeGb > 0
                        ? ComponentKind.StorageHdd
                        : ComponentKind.StorageSata;

            string kindText = kind switch
            {
                ComponentKind.StorageNvme => "NVMe",
                ComponentKind.StorageHdd => "HDD",
                _ => "SATA/SSD",
            };

            yield return new ComponentInfo(kind, model.Length > 0 ? model : "Накопитель",
                $"{kindText}, {sizeGb:0.#} ГБ, интерфейс {interfaceType}, серийный номер {serial}", true);
        }
    }

    private static string MemoryTypeName(string raw) => raw switch
    {
        "20" => "DDR",
        "21" => "DDR2",
        "24" => "DDR3",
        "26" => "DDR4",
        "34" => "DDR5",
        _ => "тип неизвестен",
    };
}
