using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using LibreHardwareMonitor.Hardware;
using PcMonitor.Core;

namespace PcMonitor.Views;

/// <summary>Вкладка «Датчики»: таблица всех датчиков, фильтры и график истории.</summary>
public partial class SensorsView : UserControl
{
    private readonly ObservableCollection<FilterItem> _typeFilters = new();
    private readonly ObservableCollection<HardwareFilterItem> _hardwareFilters = new();

    private MonitorContext? _context;
    private ListCollectionView? _view;
    private SensorRow? _selected;
    private string? _hardwareFilter;
    private string _search = string.Empty;
    private bool _ready;
    private bool _structureDirty;
    private string _hardwareLabel = string.Empty;

    public SensorsView()
    {
        InitializeComponent();
        HardwareList.ItemsSource = _hardwareFilters;
        TypeFilters.ItemsSource = _typeFilters;
    }

    /// <summary>Сообщение для строки состояния главного окна.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Запрос на создание плитки по датчику.</summary>
    public event Action<SensorRow>? TileRequested;

    public void Attach(MonitorContext context)
    {
        _context = context;

        _view = new ListCollectionView(context.Rows) { Filter = FilterRow };
        _view.SortDescriptions.Add(new SortDescription(nameof(SensorRow.HardwareOrder), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(SensorRow.Hardware), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(SensorRow.TypeOrder), ListSortDirection.Ascending));
        _view.SortDescriptions.Add(new SortDescription(nameof(SensorRow.DisplayName), ListSortDirection.Ascending));
        SensorGrid.ItemsSource = _view;

        OnlyWithValuesCheck.IsChecked = context.Settings.ShowOnlyWithValues;
        HideThresholdsCheck.IsChecked = context.Settings.HideThresholdSensors;
        HideImplausibleCheck.IsChecked = context.Settings.HideImplausibleSensors;

        _ready = true;
        context.Updated += OnUpdated;
        UpdateCounters();
    }

    private void OnUpdated()
    {
        if (_context is null) return;

        if (_structureDirty)
        {
            _structureDirty = false;
            RebuildHardwareList();
            SyncTypeFilters();
        }
        else
        {
            RefreshTypeCounts();
        }

        UpdateChart();
        UpdateCounters();
    }

    /// <summary>Вызывается главным окном при появлении новых датчиков.</summary>
    public void NotifyStructureChanged() => _structureDirty = true;

    public void ApplyLanguage()
    {
        if (_context is null) return;
        foreach (SensorRow row in _context.Rows) row.RefreshLanguage();
        foreach (FilterItem filter in _typeFilters) filter.RefreshLanguage();
        RebuildHardwareList();
        UpdateCounters();
        UpdateChart();
        string[] headers = { "Устройство", "Тип", "Датчик", "Значение", "Минимум", "Максимум", "Ед." };
        for (int i = 0; i < headers.Length && i < SensorGrid.Columns.Count; i++)
            SensorGrid.Columns[i].Header = LocalizationManager.T(headers[i]);
        if (SensorGrid.ContextMenu is ContextMenu menu)
        {
            string[] menuText = { "Не учитывать в сводке", "Скрыть датчик", "Пометить достоверным", "Пометить недостоверным", "Доверие: автоматическое", "Переименовать…", "Переименовать по имени железа", "Создать плитку на дашборде", "Сбросить правила для всех датчиков" };
            int textIndex = 0;
            foreach (object item in menu.Items)
                if (item is MenuItem menuItem && textIndex < menuText.Length) menuItem.Header = LocalizationManager.T(menuText[textIndex++]);
        }
        LocalizationManager.Apply(this, LocalizationManager.Language);
    }

    // ============================ Фильтры ============================

    private bool FilterRow(object item)
    {
        if (item is not SensorRow row) return false;
        if (row.IsHidden) return false;

        if (_hardwareFilter is not null && !string.Equals(row.Hardware, _hardwareFilter, StringComparison.Ordinal))
            return false;

        if (_context?.Settings.ShowOnlyWithValues == true && !row.HasValue) return false;

        FilterItem? filter = _typeFilters.FirstOrDefault(f => f.Type == row.Type);
        if (filter is not null && !filter.IsChecked) return false;

        if (_search.Length > 0)
        {
            return row.DisplayName.Contains(_search, StringComparison.CurrentCultureIgnoreCase)
                   || row.Sensor.Contains(_search, StringComparison.CurrentCultureIgnoreCase)
                   || row.Hardware.Contains(_search, StringComparison.CurrentCultureIgnoreCase)
                   || row.TypeName.Contains(_search, StringComparison.CurrentCultureIgnoreCase)
                   || row.HardwareGroup.Contains(_search, StringComparison.CurrentCultureIgnoreCase);
        }

        return true;
    }

    private void RebuildHardwareList()
    {
        if (_context is null) return;

        string previous = _hardwareFilter ?? string.Empty;
        _hardwareFilters.Clear();
        _hardwareFilters.Add(new HardwareFilterItem($"{LocalizationManager.T("Все устройства")} ({_context.Rows.Count})", null));

        foreach (var group in _context.Rows
                     .GroupBy(r => new { r.HardwareGroup, r.Hardware })
                     .OrderBy(g => SensorCatalog.HardwareOrder(g.First().HardwareType))
                     .ThenBy(g => g.Key.Hardware, StringComparer.CurrentCulture))
        {
            _hardwareFilters.Add(new HardwareFilterItem(
                $"{LocalizationManager.T(group.Key.HardwareGroup)}: {group.Key.Hardware} ({group.Count()})",
                group.Key.Hardware));
        }

        HardwareList.SelectedIndex = 0;
        if (previous.Length > 0)
        {
            HardwareFilterItem? match = _hardwareFilters.FirstOrDefault(h => h.Key == previous);
            if (match is not null) HardwareList.SelectedItem = match;
        }
    }

    private void SyncTypeFilters()
    {
        if (_context is null) return;

        List<SensorType> present = _context.Rows.Select(r => r.Type).Distinct()
            .OrderBy(SensorCatalog.TypeOrder)
            .ToList();

        HashSet<SensorType> known = _typeFilters.Select(f => f.Type).ToHashSet();
        foreach (SensorType type in present.Where(t => !known.Contains(t)))
        {
            var filter = new FilterItem(type);
            int index = 0;
            while (index < _typeFilters.Count &&
                   SensorCatalog.TypeOrder(_typeFilters[index].Type) <= SensorCatalog.TypeOrder(type))
                index++;
            _typeFilters.Insert(index, filter);
        }

        RefreshTypeCounts();
        _view?.Refresh();
    }

    private void RefreshTypeCounts()
    {
        if (_context is null) return;
        foreach (FilterItem filter in _typeFilters)
            filter.Count = _context.Rows.Count(r => r.Type == filter.Type);
    }

    private void TypeFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _view?.Refresh();
        UpdateCounters();
    }

    private void Hardware_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HardwareList.SelectedItem is HardwareFilterItem item)
        {
            _hardwareFilter = item.Key;
            _hardwareLabel = item.Key is null ? string.Empty : item.Label;
        }
        else
        {
            _hardwareFilter = null;
            _hardwareLabel = string.Empty;
        }

        if (!_ready) return;
        _view?.Refresh();
        UpdateCounters();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text.Trim();
        _view?.Refresh();
        UpdateCounters();
    }

    private void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _context is null) return;

        _context.Settings.ShowOnlyWithValues = OnlyWithValuesCheck.IsChecked == true;
        _context.Settings.HideThresholdSensors = HideThresholdsCheck.IsChecked == true;
        _context.Settings.HideImplausibleSensors = HideImplausibleCheck.IsChecked == true;
        _context.Settings.Save();

        foreach (SensorRow row in _context.Rows)
            row.SetRule(row.Rule);

        _view?.Refresh();
        UpdateCounters();
    }

    private void UpdateCounters()
    {
        if (_view is null || _context is null) return;
        Counters.Text = LocalizationManager.Shown(_view.Count, _context.Rows.Count, _context.Rows.Count(r => r.HasValue)) +
                        (_hardwareLabel.Length > 0 ? $" · {_hardwareLabel}" : string.Empty);
    }

    // ============================ График ============================

    private void SensorGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = SensorGrid.SelectedItem as SensorRow;
        UpdateChart();
    }

    private void UpdateChart()
    {
        if (_selected is null)
        {
            ChartTitle.Text = LocalizationManager.T("График: выберите датчик в таблице");
            ChartStats.Text = string.Empty;
            Chart.Values = null;
            return;
        }

        SensorRow row = _selected;
        ChartTitle.Text = LocalizationManager.ChartSelected(row.Hardware, row.TypeName, row.DisplayName);
        ChartStats.Text = $"{LocalizationManager.T("Текущее")} {row.ValueText} · " +
                          $"{LocalizationManager.T("Минимум")} {row.MinText} · " +
                          $"{LocalizationManager.T("Максимум")} {row.MaxText} · " +
                          $"{LocalizationManager.T("Окно")} {row.History.Count} {LocalizationManager.T("точек")}";

        bool scaleThroughput = row.Type == SensorType.Throughput;
        float[] raw = row.History.ToArray();
        if (scaleThroughput)
        {
            for (int i = 0; i < raw.Length; i++)
                if (!float.IsNaN(raw[i])) raw[i] /= 1024f;
        }

        // Показываем только выбранный период, а не всю накопленную историю.
        if (_context is not null)
        {
            double intervalSeconds = Math.Max(0.2, _context.Settings.Interval.TotalSeconds);
            int windowPoints = (int)Math.Round(Math.Max(10, _context.Settings.ChartWindowSeconds) / intervalSeconds);
            windowPoints = Math.Clamp(windowPoints, 20, row.History.Capacity);
            if (raw.Length > windowPoints) raw = raw[^windowPoints..];
        }

        Chart.MetricType = row.Type;
        Chart.TooltipValueScale = scaleThroughput ? 1024 : 1;
        Chart.Values = raw;
        Chart.LineBrush = row.Type == SensorType.Temperature ? row.ValueBrush : Theme.Accent;
        Chart.Unit = scaleThroughput ? (LocalizationManager.Language is "ru" or "ru-RU" ? "КБ/с" : "KB/s") : row.Unit;
        Chart.Format = ChartFormat(row.Type);
        Chart.FixedRange = row.HasBar ? 100 : 0;
    }

    private static string ChartFormat(SensorType type) => type switch
    {
        SensorType.Voltage or SensorType.Current => "0.000",
        SensorType.Temperature or SensorType.Load or SensorType.Control or SensorType.Level => "0.0",
        SensorType.Clock or SensorType.Fan or SensorType.Frequency => "0",
        SensorType.Data or SensorType.SmallData or SensorType.Throughput => "0.0",
        _ => "0.##",
    };

    // ============================ Контекстное меню ============================

    private SensorRow? ContextRow => SensorGrid.SelectedItem as SensorRow;

    private void ToggleIgnore_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null || ContextRow is not { } row) return;
        _context.ApplyRule(row, rule => rule.Ignore = !rule.Ignore);
        StatusChanged?.Invoke(row.IsIgnored
            ? $"Датчик «{row.DisplayName}» помечен как неважный"
            : $"Датчик «{row.DisplayName}» снова учитывается в сводке");
    }

    private void ToggleHidden_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null || ContextRow is not { } row) return;
        _context.ApplyRule(row, rule => rule.Hidden = !rule.Hidden);
        _view?.Refresh();
        StatusChanged?.Invoke($"Датчик «{row.DisplayName}» скрыт — снять можно в настройках");
    }

    private void MarkReliable_Click(object sender, RoutedEventArgs e) => SetPlausibilityOverride(true);

    private void MarkUnreliable_Click(object sender, RoutedEventArgs e) => SetPlausibilityOverride(false);

    private void ResetPlausibility_Click(object sender, RoutedEventArgs e) => SetPlausibilityOverride(null);

    private void SetPlausibilityOverride(bool? value)
    {
        if (_context is null || ContextRow is not { } row) return;
        _context.ApplyRule(row, rule => rule.PlausibilityOverride = value);
        _view?.Refresh();
        string status = value switch
        {
            true => "вручную помечен как достоверный",
            false => "вручную исключён как недостоверный",
            _ => "проверяется автоматически",
        };
        StatusChanged?.Invoke($"Датчик «{row.DisplayName}»: {status}");
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null || ContextRow is not { } row) return;

        string? name = InputDialog.Ask(Window.GetWindow(this), "Новое название датчика",
            row.DisplayName, row.Sensor);
        if (name is null) return;

        _context.ApplyRule(row, rule => rule.Alias = name.Trim().Length == 0 ? null : name.Trim());
        _view?.Refresh();
        StatusChanged?.Invoke($"Датчик переименован: {row.DisplayName}");
    }

    private void RenameByHardware_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null || ContextRow is not { } row) return;

        string proposed = $"{row.HardwareGroup}: {row.Sensor}";
        _context.ApplyRule(row, rule => rule.Alias = proposed);
        _view?.Refresh();
        StatusChanged?.Invoke($"Датчик переименован: {row.DisplayName}");
    }

    private void CreateTile_Click(object sender, RoutedEventArgs e)
    {
        if (ContextRow is { } row) TileRequested?.Invoke(row);
    }

    private void ResetRules_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null) return;

        _context.Settings.SensorOverrides.Clear();
        foreach (SensorRow row in _context.Rows)
            row.SetRule(null);

        _context.Settings.Save();
        _view?.Refresh();
        StatusChanged?.Invoke("Правила для датчиков сброшены");
    }
}
