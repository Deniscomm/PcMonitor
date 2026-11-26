using System.Collections.ObjectModel;

namespace PcMonitor.Core;

/// <summary>
/// Общее состояние приложения: настройки, служба опроса, строки датчиков и плитки.
/// Передаётся во все вкладки интерфейса.
/// </summary>
public sealed class MonitorContext
{
    public MonitorContext(AppSettings settings, MonitorService service)
    {
        Settings = settings;
        Service = service;

        foreach (TileConfig tile in settings.Tiles)
            Tiles.Add(new TileViewModel(tile, settings.HistoryPoints));
    }

    public AppSettings Settings { get; }

    public MonitorService Service { get; }

    /// <summary>Все известные датчики в порядке появления.</summary>
    public ObservableCollection<SensorRow> Rows { get; } = new();

    /// <summary>Плитки дашборда.</summary>
    public ObservableCollection<TileViewModel> Tiles { get; } = new();

    public Dictionary<string, SensorRow> ById { get; } = new(StringComparer.Ordinal);

    public DateTime LastUpdate { get; set; }

    /// <summary>Снимок применён, интерфейс можно обновлять (вызывается в UI-потоке).</summary>
    public event Action? Updated;

    public void RaiseUpdated() => Updated?.Invoke();

    /// <summary>Пользовательское правило датчика (создаётся при необходимости).</summary>
    public SensorOverride GetOrCreateRule(SensorRow row)
    {
        if (!Settings.SensorOverrides.TryGetValue(row.Id, out SensorOverride? rule))
        {
            rule = new SensorOverride();
            Settings.SensorOverrides[row.Id] = rule;
        }

        return rule;
    }

    /// <summary>Сохраняет правило датчика и обновляет строку.</summary>
    public void ApplyRule(SensorRow row, Action<SensorOverride> change)
    {
        SensorOverride rule = GetOrCreateRule(row);
        change(rule);

        if (!rule.Hidden && !rule.Ignore && rule.PlausibilityOverride is null &&
            string.IsNullOrWhiteSpace(rule.Alias))
            Settings.SensorOverrides.Remove(row.Id);

        row.SetRule(rule.Hidden || rule.Ignore || rule.PlausibilityOverride is not null ||
                    !string.IsNullOrWhiteSpace(rule.Alias) ? rule : null);
        Settings.Save();
    }

    /// <summary>Записывает текущий порядок и состав плиток в настройки.</summary>
    public void SaveTiles()
    {
        Settings.Tiles = Tiles.Select(t => t.Config).ToList();
        Settings.Save();
    }

    public void AddTile(TileConfig config)
    {
        Tiles.Add(new TileViewModel(config, Settings.HistoryPoints));
        SaveTiles();
    }

    public void RemoveTile(TileViewModel tile)
    {
        Tiles.Remove(tile);
        SaveTiles();
    }

    public void MoveTile(int from, int to)
    {
        if (from < 0 || to < 0 || from >= Tiles.Count || to >= Tiles.Count || from == to) return;
        Tiles.Move(from, to);
        SaveTiles();
    }

    /// <summary>Строки, участвующие в сводках и на плитках (без скрытых и неважных).</summary>
    public List<SensorRow> MeasurementRows() => Rows.Where(r => r.IsMeasurement).ToList();
}
