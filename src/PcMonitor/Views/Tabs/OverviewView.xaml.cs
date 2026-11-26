using System.Windows;
using PcMonitor.Core;

namespace PcMonitor.Views;

/// <summary>Новый независимый экран обзора с настраиваемыми плитками и графиками.</summary>
public partial class OverviewView : System.Windows.Controls.UserControl
{
    private MonitorContext? _context;
    private string _language = "ru";
    private bool _loadingPeriod;

    public OverviewView()
    {
        InitializeComponent();
        ApplyLanguage("ru");
    }

    public void Attach(MonitorContext context)
    {
        if (ReferenceEquals(_context, context)) return;
        if (_context is not null) _context.Updated -= Context_Updated;
        _context = context;
        TileHost.ItemsSource = context.Tiles;
        _context.Updated += Context_Updated;
        SelectPeriod(context.Settings.ChartWindowSeconds);
        foreach (TileViewModel tile in context.Tiles)
            tile.ApplyHistoryWindow(context.Settings);
        RefreshTiles();
    }

    public void ApplyLanguage(string language)
    {
        _language = language;
        bool english = language is "en-US" or "en-GB";
        bool chinese = language == "zh-CN";
        TitleText.Text = english ? "Overview" : chinese ? "概览" : "Обзор";
        SubtitleText.Text = english ? "Beautiful live tiles for your PC sensors." : chinese ? "用于电脑传感器的实时卡片。" : "Красивые живые плитки для датчиков компьютера.";
        AddButton.Content = english ? "＋ Add tile" : chinese ? "＋ 添加卡片" : "＋ Добавить плитку";
        ResetButton.Content = english ? "Reset history" : chinese ? "重置历史" : "Сбросить историю";
        PeriodLabel.Text = english ? "Chart period:" : chinese ? "图表时间范围：" : "Период графика:";
        string[] periodLabels = english
            ? ["10 seconds", "30 seconds", "60 seconds", "2 minutes", "5 minutes", "10 minutes", "All time"]
            : chinese
                ? ["10 秒", "30 秒", "60 秒", "2 分钟", "5 分钟", "10 分钟", "全部时间"]
                : ["10 секунд", "30 секунд", "60 секунд", "2 минуты", "5 минут", "10 минут", "За всё время"];
        for (int i = 0; i < PeriodBox.Items.Count && i < periodLabels.Length; i++)
            if (PeriodBox.Items[i] is System.Windows.Controls.ComboBoxItem item)
                item.Content = periodLabels[i];
        RefreshTiles();
    }

    private void SelectPeriod(double seconds)
    {
        for (int index = 0; index < PeriodBox.Items.Count; index++)
        {
            if (PeriodBox.Items[index] is not System.Windows.Controls.ComboBoxItem item) continue;
            if (item.Tag is string tag && double.TryParse(tag, out double value) && Math.Abs(value - seconds) < 1)
            {
                _loadingPeriod = true;
                PeriodBox.SelectedIndex = index;
                _loadingPeriod = false;
                return;
            }
        }
    }

    private void Period_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingPeriod || _context is null || PeriodBox.SelectedItem is not System.Windows.Controls.ComboBoxItem { Tag: string tag } ||
            !double.TryParse(tag, out double seconds))
            return;

        _context.Settings.ChartWindowSeconds = seconds;
        _context.Settings.Save();
        foreach (TileViewModel tile in _context.Tiles)
            tile.ApplyHistoryWindow(_context.Settings);
    }

    private void Context_Updated() => Dispatcher.Invoke(RefreshTiles);

    private void RefreshTiles()
    {
        if (_context is null) return;
        foreach (TileViewModel tile in _context.Tiles)
            tile.RefreshLanguage(_context.MeasurementRows(), _context.Settings);
    }

    private void AddTile_Click(object sender, RoutedEventArgs e) => OpenEditor(null);

    private void EditTile_Click(object sender, RoutedEventArgs e)
    {
        if (TileFromMenu(sender) is { } tile) OpenEditor(tile);
    }

    private void DuplicateTile_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null || TileFromMenu(sender) is not { } tile) return;
        _context.AddTile(tile.Config.Clone());
        RefreshTiles();
    }

    private void RemoveTile_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null || TileFromMenu(sender) is not { } tile) return;
        _context.RemoveTile(tile);
    }

    private void ResetTileHistory_Click(object sender, RoutedEventArgs e)
    {
        if (TileFromMenu(sender) is { } tile) tile.ResetHistory();
    }

    private void ResetHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null) return;
        foreach (TileViewModel tile in _context.Tiles) tile.ResetHistory();
    }

    private void OpenEditor(TileViewModel? tile)
    {
        if (_context is null) return;
        var editor = new TileEditorWindow(_context.Rows, tile?.Config)
        {
            Owner = Window.GetWindow(this)
        };
        if (editor.ShowDialog() != true || editor.Result is null) return;

        if (tile is null)
        {
            _context.AddTile(editor.Result);
        }
        else
        {
            int index = _context.Tiles.IndexOf(tile);
            if (index < 0) return;
            _context.Tiles[index] = new TileViewModel(editor.Result, _context.Settings.HistoryPoints);
            _context.SaveTiles();
        }
        RefreshTiles();
    }

    private static TileViewModel? TileFromMenu(object sender) =>
        (sender as System.Windows.Controls.MenuItem)?.DataContext as TileViewModel;
}
