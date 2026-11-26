namespace PcMonitor.Core.Testing;

/// <summary>Комплектующая, которую можно проверить авто-тестом.</summary>
public enum ComponentKind
{
    Cpu,
    Gpu,
    Ram,
    StorageNvme,
    StorageSata,
    StorageHdd,
    StorageUsb,
    StorageOther,
    Motherboard,
    Fans,
    Npu,
    Network,
    Rgb,
}

/// <summary>Итоговая оценка пункта отчёта.</summary>
public enum VerdictLevel
{
    /// <summary>Всё в порядке.</summary>
    Good,

    /// <summary>Стоит обратить внимание.</summary>
    Attention,

    /// <summary>Проблема.</summary>
    Bad,

    /// <summary>Просто информация.</summary>
    Info,
}

/// <summary>Найденное устройство.</summary>
public sealed record ComponentInfo(
    ComponentKind Kind,
    string Name,
    string Details,
    bool Present,
    int Units = 1);

/// <summary>Минимум, среднее и максимум метрики за фазу теста.</summary>
public sealed class MetricSummary
{
    public MetricSummary(string name, string unit)
    {
        Name = name;
        Unit = unit;
    }

    public string Name { get; }
    public string Unit { get; }
    public double Min { get; private set; } = double.MaxValue;
    public double Max { get; private set; } = double.MinValue;
    public double Sum { get; private set; }
    public int Count { get; private set; }

    public double Average => Count == 0 ? 0 : Sum / Count;
    public bool HasData => Count > 0;
    public double MinValue => Count == 0 ? 0 : Min;
    public double MaxValue => Count == 0 ? 0 : Max;

    public void Add(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return;
        Min = Math.Min(Min, value);
        Max = Math.Max(Max, value);
        Sum += value;
        Count++;
    }

    public string MinText => HasData ? $"{MinValue:0.##} {Unit}".Trim() : "—";
    public string AverageText => HasData ? $"{Average:0.##} {Unit}".Trim() : "—";
    public string MaxText => HasData ? $"{MaxValue:0.##} {Unit}".Trim() : "—";
}

/// <summary>Результат одной фазы авто-теста.</summary>
public sealed class AutoTestPhase
{
    public AutoTestPhase(string name, string description)
    {
        Name = name;
        Description = description;
    }

    public string Name { get; }
    public string Description { get; }
    public TimeSpan Duration { get; set; }
    public long Errors { get; set; }
    public string Notes { get; set; } = string.Empty;
    public Dictionary<string, MetricSummary> Metrics { get; } = new(StringComparer.Ordinal);
    public List<string> Log { get; } = new();

    public MetricSummary Metric(string name, string unit)
    {
        if (!Metrics.TryGetValue(name, out MetricSummary? metric))
        {
            metric = new MetricSummary(name, unit);
            Metrics[name] = metric;
        }

        return metric;
    }

    public double? MaxOf(string name) =>
        Metrics.TryGetValue(name, out MetricSummary? metric) && metric.HasData ? metric.MaxValue : null;

    public double? AverageOf(string name) =>
        Metrics.TryGetValue(name, out MetricSummary? metric) && metric.HasData ? metric.Average : null;
}

/// <summary>Результат проверки одного накопителя.</summary>
public sealed record StorageDiskResult(
    string Disk,
    string Volume,
    double WriteMegabytesPerSecond,
    double ReadMegabytesPerSecond,
    double RandomReadIops,
    double RandomWriteIops,
    double? TemperatureCelsius,
    long Errors,
    string Note)
{
    public string SpeedsText =>
        $"запись {WriteMegabytesPerSecond:0} МБ/с, чтение {ReadMegabytesPerSecond:0} МБ/с, 4К {RandomReadIops:0}/{RandomWriteIops:0} IOPS";
}

/// <summary>Результат одного шаблона теста памяти (в духе шаблонного теста памяти).</summary>
public sealed record MemoryPatternResult(
    string Pattern,
    long Errors,
    double Throughput,
    TimeSpan Duration,
    string Status);

/// <summary>Реакция вентилятора на нагрузку.</summary>
public sealed record FanResponse(
    string Fan,
    string Group,
    double IdleRpm,
    double LoadRpm,
    double MaximumRpm,
    bool Responds,
    bool Applicable,
    string Note);

/// <summary>Вывод по комплектующей: что хорошо, что требует внимания.</summary>
public sealed record Verdict(VerdictLevel Level, string Component, string Title, string Detail, string Recommendation)
{
    public string LevelText => Level switch
    {
        VerdictLevel.Good => "хорошо",
        VerdictLevel.Attention => "обратить внимание",
        VerdictLevel.Bad => "проблема",
        _ => "информация",
    };
}

/// <summary>Полный отчёт авто-теста.</summary>
public sealed class AutoTestReport
{
    public DateTime Started { get; set; } = DateTime.Now;
    public TimeSpan Duration { get; set; }
    public string Machine { get; set; } = string.Empty;
    public string Operator { get; set; } = string.Empty;
    public bool Elevated { get; set; }
    public List<ComponentInfo> Components { get; } = new();
    public List<AutoTestPhase> Phases { get; } = new();
    public List<Verdict> Verdicts { get; } = new();
    public List<string> Log { get; } = new();

    /// <summary>Результат по каждому накопителю (по строке на диск).</summary>
    public List<StorageDiskResult> DiskResults { get; } = new();

    /// <summary>Результат по каждому шаблону теста памяти (как в шаблонного теста памяти).</summary>
    public List<MemoryPatternResult> MemoryPatterns { get; } = new();

    /// <summary>Реакция каждого вентилятора на нагрузку.</summary>
    public List<FanResponse> FanResponses { get; } = new();

    /// <summary>Комплектующие, выбранные для этой проверки: выводы делаются только по ним.</summary>
    public HashSet<ComponentKind> SelectedComponents { get; set; } = new();

    public int Score { get; set; }
    public string Summary { get; set; } = string.Empty;

    public int GoodCount => Verdicts.Count(v => v.Level == VerdictLevel.Good);
    public int AttentionCount => Verdicts.Count(v => v.Level == VerdictLevel.Attention);
    public int BadCount => Verdicts.Count(v => v.Level == VerdictLevel.Bad);
}

/// <summary>Параметры запуска авто-теста.</summary>
public sealed class AutoTestOptions
{
    public HashSet<ComponentKind> Components { get; init; } = new(Enum.GetValues<ComponentKind>());

    /// <summary>Длительность каждой нагрузочной фазы.</summary>
    public TimeSpan PhaseDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Длительность замера в простое (базовая линия).</summary>
    public TimeSpan IdleDuration { get; init; } = TimeSpan.FromSeconds(10);

    public bool IncludeCombinedLoad { get; init; } = true;
    public int MemoryMegabytes { get; init; } = 1024;
    public int DiskMegabytes { get; init; } = 256;
    public string DiskPath { get; init; } = string.Empty;

    /// <summary>Сколько секунд гонять каждый шаблон теста памяти (шаблонов около двух десятков).</summary>
    public int MemorySecondsPerPattern { get; init; } = 10;

    /// <summary>Индексы накопителей для проверки; пусто — проверять все.</summary>
    public HashSet<int> StorageDiskIndexes { get; init; } = new();

    /// <summary>Истина — проверять ровно выбранные в UI диски, включая пустой список.</summary>
    public bool RestrictStorageToSelectedDisks { get; init; }

    /// <summary>Переопределения типа накопителей по стабильному ID.</summary>
    public IReadOnlyDictionary<string, string> StorageTypeOverrides { get; init; } = new Dictionary<string, string>();

    /// <summary>Длительность ступени нагрузки при проверке вентиляторов.</summary>
    public double FanLoadSeconds { get; init; } = 20;

    public bool SaveReport { get; init; } = true;

    public bool Has(ComponentKind kind) => Components.Contains(kind);

    /// <summary>Нужно ли проверять накопители (любого типа).</summary>
    public bool HasAnyStorage => Has(ComponentKind.StorageNvme) || Has(ComponentKind.StorageSata) ||
                                 Has(ComponentKind.StorageHdd) || Has(ComponentKind.StorageUsb) ||
                                 Has(ComponentKind.StorageOther);
}
