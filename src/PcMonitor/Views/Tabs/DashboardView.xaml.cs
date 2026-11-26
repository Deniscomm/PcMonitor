using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PcMonitor.Core;

namespace PcMonitor.Views;

/// <summary>Вкладка «Дашборд»: плитки датчиков, которые можно двигать и настраивать.</summary>
public partial class DashboardView : UserControl
{
    private MonitorContext? _context;
    private TileViewModel? _dragItem;
    private Point _dragStart;
    private bool _dragging;

    public DashboardView()
    {
        InitializeComponent();
    }

    /// <summary>Сообщение для строки состояния.</summary>
    public event Action<string>? StatusChanged;

    public void Attach(MonitorContext context)
    {
        _context = context;
        TileHost.ItemsSource = context.Tiles;
        SelectPeriod(context.Settings.ChartWindowSeconds);
    }

    public void ApplyLanguage()
    {
        if (_context is null) return;
        foreach (TileViewModel tile in _context.Tiles)
            tile.RefreshLanguage(_context.MeasurementRows(), _context.Settings);
        LocalizationManager.Apply(this, LocalizationManager.Language);
    }

    private bool _loadingPeriod;

    /// <summary>Отмечает в списке период, сохранённый в настройках.</summary>
    private void SelectPeriod(double seconds)
    {
        foreach (ComboBoxItem item in PeriodBox.Items)
        {
            if (item.Tag is string tag && double.TryParse(tag, out double value) &&
                Math.Abs(value - seconds) < 1)
            {
                _loadingPeriod = true;
                item.IsSelected = true;
                _loadingPeriod = false;
                return;
            }
        }
    }

    /// <summary>Смена периода: графики показывают выбранное окно или всю доступную историю.</summary>
    private void Period_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPeriod || _context is null) return;
        if (PeriodBox.SelectedItem is not ComboBoxItem { Tag: string tag } || !double.TryParse(tag, out double seconds))
            return;

        _context.Settings.ChartWindowSeconds = seconds;
        _context.Settings.Save();

        foreach (TileViewModel tile in _context.Tiles)
            tile.ApplyHistoryWindow(_context.Settings);

        string period = (PeriodBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
        string prefix = LocalizationManager.T("Период графика", LocalizationManager.Language);
        StatusChanged?.Invoke($"{prefix}: {period}");
    }

    // ============================ Кнопки ============================

    private void AddTile_Click(object sender, RoutedEventArgs e) => OpenEditor(null);

    private void EditTile_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemTile(sender) is { } tile) OpenEditor(tile);
    }

    private void DuplicateTile_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null || MenuItemTile(sender) is not { } tile) return;
        _context.AddTile(tile.Config.Clone());
        StatusChanged?.Invoke($"Плитка «{tile.Title}» продублирована");
    }

    private void RemoveTile_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null || MenuItemTile(sender) is not { } tile) return;
        _context.RemoveTile(tile);
        StatusChanged?.Invoke($"Плитка «{tile.Title}» удалена");
    }

    private void ResetTileHistory_Click(object sender, RoutedEventArgs e)
    {
        if (MenuItemTile(sender) is { } tile) tile.ResetHistory();
    }

    private void ResetHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null) return;
        foreach (TileViewModel tile in _context.Tiles) tile.ResetHistory();
        StatusChanged?.Invoke("История плиток сброшена");
    }

    private void DefaultTiles_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null) return;

        _context.Tiles.Clear();
        foreach (TileConfig config in AppSettings.DefaultTiles())
            _context.Tiles.Add(new TileViewModel(config, _context.Settings.HistoryPoints));

        _context.SaveTiles();
        StatusChanged?.Invoke("Восстановлен набор плиток по умолчанию");
    }

    private void OpenEditor(TileViewModel? tile)
    {
        if (_context is null) return;

        var editor = new TileEditorWindow(_context.Rows, tile?.Config)
        {
            Owner = Window.GetWindow(this),
        };

        if (editor.ShowDialog() != true || editor.Result is null) return;

        if (tile is null)
        {
            _context.AddTile(editor.Result);
            StatusChanged?.Invoke("Плитка добавлена на дашборд");
        }
        else
        {
            // Обновляем существующую плитку, сохраняя её позицию.
            int index = _context.Tiles.IndexOf(tile);
            var replacement = new TileViewModel(editor.Result, _context.Settings.HistoryPoints);
            if (index >= 0)
            {
                _context.Tiles[index] = replacement;
                _context.SaveTiles();
            }
        }
    }

    private static TileViewModel? MenuItemTile(object sender) =>
        (sender as MenuItem)?.DataContext as TileViewModel;

    // ============================ Перетаскивание ============================

    private void Tile_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not TileViewModel tile) return;

        _dragItem = tile;
        _dragStart = e.GetPosition(TileHost);
        _dragging = false;
        element.CaptureMouse();
    }

    private void Tile_MouseMove(object sender, MouseEventArgs e)
    {
        if (_context is null || _dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;

        Point position = e.GetPosition(TileHost);
        if (!_dragging && (Math.Abs(position.X - _dragStart.X) > 6 || Math.Abs(position.Y - _dragStart.Y) > 6))
            _dragging = true;

        if (!_dragging) return;

        TileViewModel? target = HitTestTile(position);
        if (target is null || ReferenceEquals(target, _dragItem)) return;

        int from = _context.Tiles.IndexOf(_dragItem);
        int to = _context.Tiles.IndexOf(target);
        if (from >= 0 && to >= 0 && from != to)
            _context.Tiles.Move(from, to);
    }

    private void Tile_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element) element.ReleaseMouseCapture();

        if (_dragging && _context is not null)
        {
            _context.SaveTiles();
            StatusChanged?.Invoke("Порядок плиток сохранён");
        }

        _dragging = false;
        _dragItem = null;
    }

    private TileViewModel? HitTestTile(Point position)
    {
        DependencyObject? hit = TileHost.InputHitTest(position) as DependencyObject;
        while (hit is not null)
        {
            if (hit is FrameworkElement { DataContext: TileViewModel tile }) return tile;
            hit = VisualTreeHelper.GetParent(hit);
        }

        return null;
    }
}
