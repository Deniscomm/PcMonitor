using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;

namespace PcMonitor.Core;

/// <summary>Крупная карточка-сводка в верхней части окна.</summary>
public sealed class SummaryCard : INotifyPropertyChanged
{
    private string _primary = "—";
    private string _secondary = "нет данных";
    private double _progress;
    private Brush _accent = Theme.Accent;
    private Brush _progressBrush = Theme.Accent;
    private string _badge = "";

    public SummaryCard(string title, string icon)
    {
        Title = title;
        Icon = icon;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; }
    public string Icon { get; }

    public string Primary
    {
        get => _primary;
        set => Set(ref _primary, value);
    }

    public string Secondary
    {
        get => _secondary;
        set => Set(ref _secondary, value);
    }

    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, Math.Clamp(value, 0, 1));
    }

    public Brush Accent
    {
        get => _accent;
        set => Set(ref _accent, value);
    }

    public Brush ProgressBrush
    {
        get => _progressBrush;
        set => Set(ref _progressBrush, value);
    }

    public string Badge
    {
        get => _badge;
        set => Set(ref _badge, value);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>Собирает сводку по категориям: процессор, видеокарта, память, плата, диски, сеть.</summary>
public static class SummaryBuilder
{
    public const string Cpu = "Процессор";
    public const string Gpu = "Видеокарта";
    public const string Ram = "Оперативная память";
    public const string Board = "Мат. плата";
    public const string Storage = "Накопители";
    public const string Network = "Сеть";

    public static IReadOnlyList<SummaryCard> CreateCards() => new List<SummaryCard>
    {
        new("Процессор", "CPU"),
        new("Видеокарта", "GPU"),
        new("Оперативная память", "RAM"),
        new("Мат. плата", "MB"),
        new("Накопители", "SSD"),
        new("Сеть", "NET"),
    };

    public static void Update(IReadOnlyList<SummaryCard> cards, IEnumerable<SensorRow> rows, AppSettings settings)
    {
        var list = rows as IList<SensorRow> ?? rows.ToList();
        if (cards.Count < 6) return;

        UpdateCpu(cards[0], list, settings);
        UpdateGpu(cards[1], list, settings);
        UpdateRam(cards[2], list);
        UpdateBoard(cards[3], list, settings);
        UpdateStorage(cards[4], list, settings);
        UpdateNetwork(cards[5], list);
    }

    private static void UpdateCpu(SummaryCard card, IList<SensorRow> rows, AppSettings settings)
    {
        var cpu = rows.Where(r => r.HardwareGroup == Cpu).ToList();
        double? temp = MaxValue(cpu, SensorType.Temperature);
        double? load = BestLoad(cpu, "CPU Total");
        double? clock = First(cpu, SensorType.Clock, "Cores (Average)");
        if (clock is <= 0) clock = null;
        clock ??= MaxValue(cpu.Where(r => r.DisplayValue > 0), SensorType.Clock);
        double? power = First(cpu, SensorType.Power, "Package");

        card.Primary = temp.HasValue ? SensorCatalog.Format(SensorType.Temperature, temp.Value) : "—";
        card.Accent = TemperatureBrush(temp, settings);
        var parts = new List<string>();
        if (load.HasValue) parts.Add($"загрузка {load.Value:0.0} %");
        if (clock.HasValue) parts.Add($"{clock.Value:0} МГц");
        if (power.HasValue) parts.Add($"{power.Value:0.0} Вт");
        parts.Add($"{cpu.Count} датчиков");
        card.Secondary = string.Join(" · ", parts);
        card.Progress = (load ?? 0) / 100.0;
        card.ProgressBrush = LoadBrush(load);
        card.Badge = cpu.Any(r => r.IsAlert) ? "перегрев" : "";
    }

    private static void UpdateGpu(SummaryCard card, IList<SensorRow> rows, AppSettings settings)
    {
        var gpu = rows.Where(r => r.HardwareGroup == Gpu).ToList();
        double? temp = First(gpu, SensorType.Temperature, "GPU Core") ?? MaxValue(gpu, SensorType.Temperature);
        double? load = BestLoad(gpu, "GPU Core");
        double? memoryUsed = First(gpu, SensorType.SmallData, "GPU Memory Used");
        double? memoryTotal = First(gpu, SensorType.SmallData, "GPU Memory Total");
        double? power = First(gpu, SensorType.Power, "GPU Package");
        double? fan = MaxValue(gpu, SensorType.Fan);

        card.Primary = temp.HasValue ? SensorCatalog.Format(SensorType.Temperature, temp.Value) : "—";
        card.Accent = TemperatureBrush(temp, settings);
        var parts = new List<string>();
        if (load.HasValue) parts.Add($"загрузка {load.Value:0.0} %");
        if (memoryUsed.HasValue && memoryTotal.HasValue)
            parts.Add($"память {memoryUsed.Value:0} / {memoryTotal.Value:0} МБ");
        if (power.HasValue) parts.Add($"{power.Value:0.0} Вт");
        if (fan.HasValue && fan.Value > 0) parts.Add($"{fan.Value:0} об/мин");
        card.Secondary = parts.Count > 0 ? string.Join(" · ", parts) : "нет данных";
        card.Progress = (load ?? 0) / 100.0;
        card.ProgressBrush = LoadBrush(load);
        card.Badge = gpu.Any(r => r.IsAlert) ? "перегрев" : "";
    }

    private static void UpdateRam(SummaryCard card, IList<SensorRow> rows)
    {
        var ram = rows.Where(r => r.HardwareGroup == Ram).ToList();
        var physical = ram.Where(r => r.Hardware.Contains("Total Memory", StringComparison.OrdinalIgnoreCase)).ToList();
        if (physical.Count == 0) physical = ram;

        double? load = BestLoad(physical, "Memory");
        double? used = First(physical, SensorType.Data, "Memory Used");
        double? available = First(physical, SensorType.Data, "Memory Available");

        card.Primary = load.HasValue ? $"{load.Value:0.0} %" : "—";
        card.Accent = LoadBrush(load);
        card.Secondary = used.HasValue && available.HasValue
            ? $"{used.Value:0.00} ГБ занято из {used.Value + available.Value:0.00} ГБ"
            : "нет данных";
        card.Progress = (load ?? 0) / 100.0;
        card.ProgressBrush = LoadBrush(load);
        card.Badge = load >= 92 ? "мало памяти" : "";
    }

    private static void UpdateBoard(SummaryCard card, IList<SensorRow> rows, AppSettings settings)
    {
        var board = rows.Where(r => r.HardwareGroup == Board).ToList();
        double? temp = MaxValue(board, SensorType.Temperature);
        double? fan = MaxValue(board, SensorType.Fan);
        double? voltage = MaxValue(board, SensorType.Voltage);
        int implausible = board.Count(r => r.Type == SensorType.Temperature && !r.IsPlausible && r.HasValue);

        card.Primary = temp.HasValue ? SensorCatalog.Format(SensorType.Temperature, temp.Value) : "—";
        card.Accent = TemperatureBrush(temp, settings.TemperatureWarning, settings.TemperatureCritical);
        var parts = new List<string> { $"{board.Count} датчиков" };
        if (fan.HasValue) parts.Add($"вентиляторы до {fan.Value:0} об/мин");
        if (voltage.HasValue) parts.Add($"макс. {voltage.Value:0.###} В");
        if (implausible > 0) parts.Add($"недостоверных датчиков: {implausible}");
        card.Secondary = string.Join(" · ", parts);
        card.Progress = temp.HasValue ? Math.Clamp((temp.Value - 20) / 80, 0, 1) : 0;
        card.ProgressBrush = card.Accent;
        card.Badge = board.Any(r => r.IsAlert) ? "перегрев" : "";
    }

    private static void UpdateStorage(SummaryCard card, IList<SensorRow> rows, AppSettings settings)
    {
        var storage = rows.Where(r => r.HardwareGroup == Storage).ToList();
        double? temp = MaxValue(storage, SensorType.Temperature);
        int devices = storage.Select(r => r.Hardware).Distinct().Count();
        double read = SumThroughput(storage, "Read");
        double write = SumThroughput(storage, "Write");

        card.Primary = temp.HasValue ? SensorCatalog.Format(SensorType.Temperature, temp.Value) : "—";
        card.Accent = TemperatureBrush(temp, settings.StorageTemperatureWarning, settings.StorageTemperatureCritical);
        var parts = new List<string> { $"{devices} устр." };
        if (read > 0) parts.Add($"чтение {SensorCatalog.FormatThroughput(read)}");
        if (write > 0) parts.Add($"запись {SensorCatalog.FormatThroughput(write)}");
        if (!temp.HasValue && devices == 0) parts.Add("нет данных — нужны права администратора");
        card.Secondary = string.Join(" · ", parts);
        card.Progress = temp.HasValue
            ? Math.Clamp((temp.Value - 20) / Math.Max(1, settings.StorageTemperatureCritical - 20), 0, 1)
            : 0;
        card.ProgressBrush = card.Accent;
        card.Badge = storage.Any(r => r.IsAlert) ? "перегрев" : "";
    }

    private static void UpdateNetwork(SummaryCard card, IList<SensorRow> rows)
    {
        var net = rows.Where(r => r.HardwareGroup == Network).ToList();
        double download = MaxThroughput(net, "Download");
        double upload = MaxThroughput(net, "Upload");
        double? downloaded = First(net, SensorType.Data, "Data Downloaded");
        int active = net.Where(r => r.Type == SensorType.Throughput && r.DisplayValue > 1).Select(r => r.Hardware).Distinct().Count();

        card.Primary = download > 0 ? SensorCatalog.FormatThroughput(download) : "0 Б/с";
        card.Accent = Theme.Accent;
        var parts = new List<string> { $"отдача {SensorCatalog.FormatThroughput(upload)}" };
        if (downloaded.HasValue) parts.Add($"скачано {downloaded.Value:0.00} ГБ");
        parts.Add($"{active} активных");
        card.Secondary = string.Join(" · ", parts);
        card.Progress = Math.Clamp(download / (125 * 1024 * 1024), 0, 1);
        card.ProgressBrush = Theme.Accent;
    }

    private static Brush TemperatureBrush(double? temp, AppSettings settings) =>
        TemperatureBrush(temp, settings.TemperatureWarning, settings.TemperatureCritical);

    private static Brush TemperatureBrush(double? temp, double warning, double critical) => temp switch
    {
        null => Theme.TextFaint,
        var t when t >= critical => Theme.Bad,
        var t when t >= warning => Theme.Warn,
        _ => Theme.Good,
    };

    private static Brush LoadBrush(double? load) => load switch
    {
        null => Theme.TextFaint,
        var l when l >= 90 => Theme.Bad,
        var l when l >= 70 => Theme.Warn,
        _ => Theme.Good,
    };

    private static double? First(IEnumerable<SensorRow> rows, SensorType type, string nameContains)
    {
        foreach (SensorRow row in rows)
        {
            if (row.Type == type && row.DisplayValue.HasValue &&
                row.Sensor.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                return row.DisplayValue.Value;
        }
        return null;
    }

    private static double? BestLoad(IEnumerable<SensorRow> rows, string preferredName)
    {
        List<SensorRow> candidates = rows
            .Where(r => r.Type == SensorType.Load && r.IsMeasurement && r.DisplayValue.HasValue)
            .ToList();
        SensorRow? preferred = candidates.FirstOrDefault(r =>
            r.Sensor.Contains(preferredName, StringComparison.OrdinalIgnoreCase));
        return preferred?.DisplayValue is { } value
            ? value
            : candidates.Count == 0 ? null : candidates.Max(r => r.DisplayValue!.Value);
    }

    private static double? MaxValue(IEnumerable<SensorRow> rows, SensorType type)
    {
        double? max = null;
        foreach (SensorRow row in rows)
        {
            if (row.Type != type || !row.DisplayValue.HasValue || !row.IsPlausible) continue;
            if (max is null || row.DisplayValue.Value > max) max = row.DisplayValue.Value;
        }
        return max;
    }

    private static double SumThroughput(IEnumerable<SensorRow> rows, string nameContains) =>
        rows.Where(r => r.Type == SensorType.Throughput && r.DisplayValue.HasValue &&
                        r.Sensor.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            .Sum(r => (double)r.DisplayValue!.Value);

    private static double MaxThroughput(IEnumerable<SensorRow> rows, string nameContains)
    {
        double max = 0;
        foreach (SensorRow row in rows)
        {
            if (row.Type != SensorType.Throughput || !row.DisplayValue.HasValue || !row.IsPlausible) continue;
            if (!row.Sensor.Contains(nameContains, StringComparison.OrdinalIgnoreCase)) continue;
            max = Math.Max(max, row.DisplayValue.Value);
        }
        return max;
    }
}
