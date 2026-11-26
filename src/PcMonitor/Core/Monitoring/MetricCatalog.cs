using LibreHardwareMonitor.Hardware;

namespace PcMonitor.Core;

/// <summary>Метрика для плитки: либо готовый агрегат, либо конкретный датчик.</summary>
public sealed class MetricDefinition
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Group { get; init; }
    public required SensorType Type { get; init; }
    public string Unit { get; init; } = string.Empty;

    /// <summary>Верхняя граница шкалы для плиток-шкал (null — считать автоматически).</summary>
    public double? Scale { get; init; }

    public required Func<IReadOnlyList<SensorRow>, double?> Resolve { get; init; }
}

/// <summary>Каталог метрик: температуры, обороты, частоты, сеть, объёмы и прочее.</summary>
public static class MetricCatalog
{
    public const string SensorPrefix = "sensor:";

    public const string CpuTemperature = "cpu.temp";
    public const string CpuLoad = "cpu.load";
    public const string CpuClock = "cpu.clock";
    public const string CpuPower = "cpu.power";
    public const string CpuVoltage = "cpu.voltage";
    public const string GpuTemperature = "gpu.temp";
    public const string GpuHotSpot = "gpu.hotspot";
    public const string GpuLoad = "gpu.load";
    public const string GpuClock = "gpu.clock";
    public const string GpuMemoryClock = "gpu.memclock";
    public const string GpuPower = "gpu.power";
    public const string GpuFan = "gpu.fan";
    public const string VramUsed = "gpu.vram.used";
    public const string VramTotal = "gpu.vram.total";
    public const string RamLoad = "ram.load";
    public const string RamUsedGb = "ram.used";
    public const string RamFreeGb = "ram.free";
    public const string RamTotalGb = "ram.total";
    public const string BoardTemperature = "board.temp";
    public const string BoardFanMax = "board.fan";
    public const string BoardVoltage = "board.voltage";
    public const string StorageTemperature = "storage.temp";
    public const string StorageRead = "storage.read";
    public const string StorageWrite = "storage.write";
    public const string MaxFanRpm = "fan.max";
    public const string MaxStorageTemperature = "storage.maxtemp";
    public const string NetworkDownload = "net.down";
    public const string NetworkUpload = "net.up";
    public const string NetworkDownloaded = "net.downloaded";
    public const string NetworkUploaded = "net.uploaded";

    /// <summary>Готовые агрегаты, доступные для плиток.</summary>
    public static IReadOnlyList<MetricDefinition> Aggregates { get; } = new List<MetricDefinition>
    {
        new()
        {
            Key = CpuTemperature, Title = "Температура процессора", Group = "Процессор",
            Type = SensorType.Temperature, Unit = "°C", Scale = 100,
            Resolve = rows => Max(rows, SummaryBuilder.Cpu, SensorType.Temperature),
        },
        new()
        {
            Key = CpuLoad, Title = "Загрузка процессора", Group = "Процессор",
            Type = SensorType.Load, Unit = "%", Scale = 100,
            Resolve = rows => BestLoad(rows, SummaryBuilder.Cpu, "CPU Total"),
        },
        new()
        {
            Key = CpuClock, Title = "Частота процессора", Group = "Процессор",
            Type = SensorType.Clock, Unit = "МГц",
            Resolve = rows => First(rows, SummaryBuilder.Cpu, SensorType.Clock, "Cores (Average)")
                             ?? Max(rows, SummaryBuilder.Cpu, SensorType.Clock),
        },
        new()
        {
            Key = CpuPower, Title = "Мощность процессора", Group = "Процессор",
            Type = SensorType.Power, Unit = "Вт",
            Resolve = rows => First(rows, SummaryBuilder.Cpu, SensorType.Power, "Package")
                             ?? Max(rows, SummaryBuilder.Cpu, SensorType.Power),
        },
        new()
        {
            Key = CpuVoltage, Title = "Напряжение ядра", Group = "Процессор",
            Type = SensorType.Voltage, Unit = "В",
            Resolve = rows => First(rows, SummaryBuilder.Cpu, SensorType.Voltage, "Core (SVI2 TFN)")
                             ?? Max(rows, SummaryBuilder.Cpu, SensorType.Voltage),
        },
        new()
        {
            Key = GpuTemperature, Title = "Температура видеокарты", Group = "Видеокарта",
            Type = SensorType.Temperature, Unit = "°C", Scale = 100,
            Resolve = rows => First(rows, SummaryBuilder.Gpu, SensorType.Temperature, "GPU Core")
                             ?? Max(rows, SummaryBuilder.Gpu, SensorType.Temperature),
        },
        new()
        {
            Key = GpuHotSpot, Title = "Hot Spot видеокарты", Group = "Видеокарта",
            Type = SensorType.Temperature, Unit = "°C", Scale = 110,
            Resolve = rows => First(rows, SummaryBuilder.Gpu, SensorType.Temperature, "Hot Spot"),
        },
        new()
        {
            Key = GpuLoad, Title = "Загрузка видеокарты", Group = "Видеокарта",
            Type = SensorType.Load, Unit = "%", Scale = 100,
            Resolve = rows => BestLoad(rows, SummaryBuilder.Gpu, "GPU Core"),
        },
        new()
        {
            Key = GpuClock, Title = "Частота ядра GPU", Group = "Видеокарта",
            Type = SensorType.Clock, Unit = "МГц",
            Resolve = rows => First(rows, SummaryBuilder.Gpu, SensorType.Clock, "GPU Core"),
        },
        new()
        {
            Key = GpuMemoryClock, Title = "Частота памяти GPU", Group = "Видеокарта",
            Type = SensorType.Clock, Unit = "МГц",
            Resolve = rows => First(rows, SummaryBuilder.Gpu, SensorType.Clock, "GPU Memory"),
        },
        new()
        {
            Key = GpuPower, Title = "Мощность видеокарты", Group = "Видеокарта",
            Type = SensorType.Power, Unit = "Вт", Scale = 250,
            Resolve = rows => First(rows, SummaryBuilder.Gpu, SensorType.Power, "GPU Package"),
        },
        new()
        {
            Key = GpuFan, Title = "Обороты вентилятора GPU", Group = "Видеокарта",
            Type = SensorType.Fan, Unit = "об/мин",
            Resolve = rows => Max(rows, SummaryBuilder.Gpu, SensorType.Fan),
        },
        new()
        {
            Key = VramUsed, Title = "Видеопамять занято", Group = "Видеокарта",
            Type = SensorType.SmallData, Unit = "МБ",
            Resolve = rows => First(rows, SummaryBuilder.Gpu, SensorType.SmallData, "GPU Memory Used"),
        },
        new()
        {
            Key = VramTotal, Title = "Видеопамять всего", Group = "Видеокарта",
            Type = SensorType.SmallData, Unit = "МБ",
            Resolve = rows => First(rows, SummaryBuilder.Gpu, SensorType.SmallData, "GPU Memory Total"),
        },
        new()
        {
            Key = RamLoad, Title = "Загрузка памяти", Group = "Оперативная память",
            Type = SensorType.Load, Unit = "%", Scale = 100,
            Resolve = rows => PhysicalRam(rows, r => r.Type == SensorType.Load &&
                (r.Sensor.Contains("Memory", StringComparison.OrdinalIgnoreCase) ||
                 r.Sensor.Contains("RAM", StringComparison.OrdinalIgnoreCase))),
        },
        new()
        {
            Key = RamUsedGb, Title = "Память занято", Group = "Оперативная память",
            Type = SensorType.Data, Unit = "ГБ",
            Resolve = rows => PhysicalRam(rows, r => r.Type == SensorType.Data && r.Sensor.Contains("Used", StringComparison.OrdinalIgnoreCase)),
        },
        new()
        {
            Key = RamFreeGb, Title = "Память свободно", Group = "Оперативная память",
            Type = SensorType.Data, Unit = "ГБ",
            Resolve = rows => PhysicalRam(rows, r => r.Type == SensorType.Data && r.Sensor.Contains("Available", StringComparison.OrdinalIgnoreCase)),
        },
        new()
        {
            Key = RamTotalGb, Title = "Память всего", Group = "Оперативная память",
            Type = SensorType.Data, Unit = "ГБ",
            Resolve = rows =>
            {
                double? used = PhysicalRam(rows, r => r.Type == SensorType.Data && r.Sensor.Contains("Used", StringComparison.OrdinalIgnoreCase));
                double? free = PhysicalRam(rows, r => r.Type == SensorType.Data && r.Sensor.Contains("Available", StringComparison.OrdinalIgnoreCase));
                return used.HasValue && free.HasValue ? used.Value + free.Value : null;
            },
        },
        new()
        {
            Key = BoardTemperature, Title = "Температура мат. платы", Group = "Мат. плата",
            Type = SensorType.Temperature, Unit = "°C", Scale = 100,
            Resolve = rows => Max(rows, SummaryBuilder.Board, SensorType.Temperature),
        },
        new()
        {
            Key = BoardFanMax, Title = "Вентиляторы платы (макс.)", Group = "Мат. плата",
            Type = SensorType.Fan, Unit = "об/мин",
            Resolve = rows => Max(rows, SummaryBuilder.Board, SensorType.Fan),
        },
        new()
        {
            Key = BoardVoltage, Title = "Напряжение платы (макс.)", Group = "Мат. плата",
            Type = SensorType.Voltage, Unit = "В",
            Resolve = rows => Max(rows, SummaryBuilder.Board, SensorType.Voltage),
        },
        new()
        {
            Key = StorageTemperature, Title = "Температура накопителей", Group = "Накопители",
            Type = SensorType.Temperature, Unit = "°C", Scale = 90,
            Resolve = rows => Max(rows, SummaryBuilder.Storage, SensorType.Temperature),
        },
        new()
        {
            Key = StorageRead, Title = "Чтение с дисков", Group = "Накопители",
            Type = SensorType.Throughput, Unit = "Б/с",
            Resolve = rows => SumThroughput(rows, SummaryBuilder.Storage, "Read"),
        },
        new()
        {
            Key = StorageWrite, Title = "Запись на диски", Group = "Накопители",
            Type = SensorType.Throughput, Unit = "Б/с",
            Resolve = rows => SumThroughput(rows, SummaryBuilder.Storage, "Write"),
        },
        new()
        {
            Key = MaxFanRpm, Title = "Вентиляторы (макс.)", Group = "Охлаждение",
            Type = SensorType.Fan, Unit = "об/мин", Scale = 2500,
            Resolve = rows => Max(rows, null, SensorType.Fan),
        },
        new()
        {
            Key = MaxStorageTemperature, Title = "Накопители (макс. темп.)", Group = "Накопители",
            Type = SensorType.Temperature, Unit = "°C", Scale = 90,
            Resolve = rows => Max(rows, SummaryBuilder.Storage, SensorType.Temperature),
        },
        new()
        {
            Key = NetworkDownload, Title = "Сеть: приём", Group = "Сеть",
            Type = SensorType.Throughput, Unit = "Б/с",
            Resolve = rows => MaxThroughput(rows, "Download"),
        },
        new()
        {
            Key = NetworkUpload, Title = "Сеть: отдача", Group = "Сеть",
            Type = SensorType.Throughput, Unit = "Б/с",
            Resolve = rows => MaxThroughput(rows, "Upload"),
        },
        new()
        {
            Key = NetworkDownloaded, Title = "Сеть: скачано", Group = "Сеть",
            Type = SensorType.Data, Unit = "ГБ",
            Resolve = rows => MaxData(rows, "Data Downloaded"),
        },
        new()
        {
            Key = NetworkUploaded, Title = "Сеть: отдано", Group = "Сеть",
            Type = SensorType.Data, Unit = "ГБ",
            Resolve = rows => MaxData(rows, "Data Uploaded"),
        },
    };

    private static readonly Dictionary<string, MetricDefinition> Index =
        Aggregates.ToDictionary(m => m.Key, StringComparer.Ordinal);

    public static MetricDefinition? Find(string key) =>
        Index.TryGetValue(key, out MetricDefinition? metric) ? metric : null;

    /// <summary>Метрика для конкретного датчика (ключ вида «sensor:/amdcpu/0/temperature/0»).</summary>
    public static MetricDefinition ForSensor(SensorRow row) => new()
    {
        Key = SensorPrefix + row.Id,
        Title = row.Sensor,
        Group = row.HardwareGroup,
        Type = row.Type,
        Unit = row.Unit,
        Scale = row.HasBar ? 100 : null,
        Resolve = rows =>
        {
            SensorRow? found = rows.FirstOrDefault(r => r.Id == row.Id);
            return found is { DisplayValue: { } value } ? value : null;
        },
    };

    public static bool IsSensorKey(string key) => key.StartsWith(SensorPrefix, StringComparison.Ordinal);

    public static string SensorIdFromKey(string key) => key[SensorPrefix.Length..];

    private static double? First(IEnumerable<SensorRow> rows, string group, SensorType type, string namePart)
    {
        foreach (SensorRow row in rows)
        {
            if (!row.IsMeasurement || row.HardwareGroup != group || row.Type != type) continue;
            if (row.Sensor.Contains(namePart, StringComparison.OrdinalIgnoreCase) && row.DisplayValue.HasValue)
                return row.DisplayValue.Value;
        }
        return null;
    }

    /// <summary>Выбирает агрегатную загрузку даже при различающихся именах LHM/Rust.</summary>
    private static double? BestLoad(IEnumerable<SensorRow> rows, string group, string preferredName)
    {
        List<SensorRow> candidates = rows
            .Where(r => r.IsMeasurement && r.HardwareGroup == group &&
                        r.Type == SensorType.Load && r.DisplayValue.HasValue)
            .ToList();

        SensorRow? preferred = candidates.FirstOrDefault(r =>
            r.Sensor.Contains(preferredName, StringComparison.OrdinalIgnoreCase));
        if (preferred is not null) return preferred.DisplayValue!.Value;
        return candidates.Count == 0 ? null : candidates.Max(r => r.DisplayValue!.Value);
    }

    private static double? Max(IEnumerable<SensorRow> rows, string? group, SensorType type)
    {
        double? max = null;
        foreach (SensorRow row in rows)
        {
            if (!row.IsMeasurement || row.Type != type || !row.DisplayValue.HasValue) continue;
            if (group is not null && row.HardwareGroup != group) continue;
            if (max is null || row.DisplayValue.Value > max) max = row.DisplayValue.Value;
        }
        return max;
    }

    private static double? PhysicalRam(IEnumerable<SensorRow> rows, Func<SensorRow, bool> predicate)
    {
        foreach (SensorRow row in rows)
        {
            if (!row.IsMeasurement) continue;
            if (!row.HardwareGroup.Contains("память", StringComparison.OrdinalIgnoreCase)) continue;
            if (row.Hardware.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
            if (predicate(row) && row.DisplayValue.HasValue) return row.DisplayValue.Value;
        }

        foreach (SensorRow row in rows)
        {
            if (!row.IsMeasurement || !predicate(row) || !row.DisplayValue.HasValue) continue;
            if (row.Hardware.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;
            return row.DisplayValue.Value;
        }

        return null;
    }

    private static double? SumThroughput(IEnumerable<SensorRow> rows, string group, string namePart)
    {
        double sum = 0;
        bool any = false;
        foreach (SensorRow row in rows)
        {
            if (!row.IsMeasurement || row.HardwareGroup != group || row.Type != SensorType.Throughput) continue;
            if (!row.Sensor.Contains(namePart, StringComparison.OrdinalIgnoreCase) || !row.DisplayValue.HasValue) continue;
            sum += row.DisplayValue.Value;
            any = true;
        }
        return any ? sum : null;
    }

    private static double? MaxThroughput(IEnumerable<SensorRow> rows, string namePart)
    {
        double max = 0;
        bool any = false;
        foreach (SensorRow row in rows)
        {
            if (!row.IsMeasurement || row.Type != SensorType.Throughput || !row.DisplayValue.HasValue) continue;
            if (!row.Sensor.Contains(namePart, StringComparison.OrdinalIgnoreCase)) continue;
            if (row.DisplayValue.Value > max) max = row.DisplayValue.Value;
            any = true;
        }
        return any ? max : null;
    }

    private static double? MaxData(IEnumerable<SensorRow> rows, string namePart)
    {
        double max = 0;
        bool any = false;
        foreach (SensorRow row in rows)
        {
            if (!row.IsMeasurement || row.Type != SensorType.Data || !row.DisplayValue.HasValue) continue;
            if (!row.Sensor.Contains(namePart, StringComparison.OrdinalIgnoreCase)) continue;
            if (row.DisplayValue.Value > max) max = row.DisplayValue.Value;
            any = true;
        }
        return any ? max : null;
    }
}
