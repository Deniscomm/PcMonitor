namespace PcMonitor.Core;

/// <summary>Пороги температуры для категории устройств.</summary>
public sealed class ThresholdProfile
{
    public double Warning { get; set; } = 70;
    public double Critical { get; set; } = 85;

    public ThresholdProfile() { }

    public ThresholdProfile(double warning, double critical)
    {
        Warning = warning;
        Critical = critical;
    }
}

/// <summary>Пользовательские правила для конкретного датчика.</summary>
public sealed class SensorOverride
{
    /// <summary>Не показывать в таблице датчиков.</summary>
    public bool Hidden { get; set; }

    /// <summary>Ручная оценка датчика; null — автоматическая проверка диапазона.</summary>
    public bool? PlausibilityOverride { get; set; }

    /// <summary>Не учитывать в сводке, плитках и сигналах тревоги.</summary>
    public bool Ignore { get; set; }

    /// <summary>Своё понятное название датчика.</summary>
    public string? Alias { get; set; }
}

/// <summary>Внешний вид плитки на дашборде.</summary>
public enum TileVisual
{
    /// <summary>Крупное значение с подписью.</summary>
    Value,

    /// <summary>Круговая шкала.</summary>
    Gauge,

    /// <summary>Горизонтальная полоса заполнения.</summary>
    Bar,

    /// <summary>Только график истории.</summary>
    Graph,
}

/// <summary>Плитка дашборда: метрика, оформление и размер.</summary>
public sealed class TileConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string MetricKey { get; set; } = MetricCatalog.RamLoad;
    public string Title { get; set; } = string.Empty;
    public TileVisual Visual { get; set; } = TileVisual.Value;
    public int Width { get; set; } = 260;
    public int Height { get; set; } = 150;
    public string Accent { get; set; } = string.Empty;
    public double? Warning { get; set; }
    public double? Critical { get; set; }
    public bool ShowHistory { get; set; } = true;

    public TileConfig Clone() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        MetricKey = MetricKey,
        Title = Title,
        Visual = Visual,
        Width = Width,
        Height = Height,
        Accent = Accent,
        Warning = Warning,
        Critical = Critical,
        ShowHistory = ShowHistory,
    };
}
