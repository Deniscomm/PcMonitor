using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LibreHardwareMonitor.Hardware;
using PcMonitor.Core;

namespace PcMonitor.Avalonia;

public partial class MainWindow : Window
{
    private readonly MonitorService _service = new();
    private readonly List<SensorReading> _readings = new();
    private string _language = "en-US";

    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => _service.Start();
        Closing += (_, _) => _service.Stop();
        _service.SnapshotReady += OnSnapshot;
        DashboardClick(this, new RoutedEventArgs());
    }

    private void OnSnapshot(IReadOnlyList<SensorReading> readings, DateTime timestamp)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _readings.Clear();
            _readings.AddRange(readings);
            StatusText.Text = $"Sensors: {_readings.Count} · updated {timestamp:HH:mm:ss}";
            if (PageHost.Content is Control page && page.Tag is string tag)
            {
                switch (tag) { case "dashboard": DashboardClick(this, new RoutedEventArgs()); break; case "sensors": SensorsClick(this, new RoutedEventArgs()); break; }
            }
        }, DispatcherPriority.Background);
    }

    private void DashboardClick(object? sender, RoutedEventArgs e) => ShowDashboard();
    private void SensorsClick(object? sender, RoutedEventArgs e) => ShowSensors();
    private void SystemClick(object? sender, RoutedEventArgs e) => ShowSystem();
    private void SettingsClick(object? sender, RoutedEventArgs e) => ShowSettings();

    private void ShowDashboard()
    {
        var root = Page("dashboard");
        root.Children.Add(Heading("Dashboard", "Live values from the existing monitoring core"));
        var grid = new UniformGrid { Columns = 3, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (string name in new[] { "CPU Total", "CPU Core Reported Clock", "Memory Load", "Memory Used", "Memory Available", "Download", "Upload" })
        {
            SensorReading? sensor = _readings.FirstOrDefault(x => x.SensorName == name);
            if (sensor is not null) grid.Children.Add(Card(sensor));
        }
        root.Children.Add(grid);
        if (grid.Children.Count == 0) root.Children.Add(new TextBlock { Text = "Waiting for sensors…", Classes = { "muted" }, Margin = new Thickness(0, 18) });
        PageHost.Content = root;
    }

    private void ShowSensors()
    {
        var root = Page("sensors");
        root.Children.Add(Heading("Sensors", "All values collected by C# logic and Rust sidecar"));
        var search = new TextBox { Watermark = "Search sensors", Margin = new Thickness(0, 0, 0, 12) };
        root.Children.Add(search);
        var list = new StackPanel { Spacing = 2 };
        void Rebuild(string query)
        {
            list.Children.Clear();
            foreach (SensorReading sensor in _readings.Where(x => string.IsNullOrWhiteSpace(query) || x.SensorName.Contains(query, StringComparison.OrdinalIgnoreCase) || x.Hardware.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                list.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#202832")), Padding = new Thickness(12, 8), Margin = new Thickness(0, 1), Child = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,2*,*,3*"), Children = { Label(sensor.Hardware), Label(sensor.SensorName, 1), Label(Format(sensor), 2), Label(sensor.Id, 3, true) } } });
            }
        }
        search.TextChanged += (_, _) => Rebuild(search.Text ?? string.Empty);
        Rebuild(string.Empty);
        root.Children.Add(new ScrollViewer { Content = list, MaxHeight = 560 });
        PageHost.Content = root;
    }

    private void ShowSystem()
    {
        var root = Page("system");
        root.Children.Add(Heading("System", "Live hardware groups and available measurements"));
        foreach (var group in _readings.GroupBy(x => x.HardwareGroup).OrderBy(x => x.Key))
            root.Children.Add(new Border { Classes = { "card" }, Margin = new Thickness(0, 4), Child = new StackPanel { Children = { new TextBlock { Text = group.Key, FontSize = 17, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = $"{group.Count()} sensors · {group.Count(x => x.Value.HasValue)} with values", Classes = { "muted" }, Margin = new Thickness(0, 5) } } } });
        PageHost.Content = root;
    }

    private void ShowSettings()
    {
        var root = Page("settings");
        root.Children.Add(Heading("Settings", "Avalonia UI settings"));
        var language = new ComboBox { SelectedIndex = 1, ItemsSource = new[] { "English (UK)", "English (US)", "Русский", "简体中文" }, Width = 220 };
        language.SelectionChanged += (_, _) => _language = language.SelectedIndex switch { 0 => "en-GB", 2 => "ru", 3 => "zh-CN", _ => "en-US" };
        root.Children.Add(new Border { Classes = { "card" }, Child = new StackPanel { Spacing = 12, Children = { new TextBlock { Text = "Language / Язык / 语言" }, language, new CheckBox { Content = "Start with Windows", IsChecked = false }, new CheckBox { Content = "Show tray notifications", IsChecked = true } } } });
        PageHost.Content = root;
    }

    private static StackPanel Page(string tag) => new() { Tag = tag, Spacing = 12 };
    private static TextBlock Heading(string title, string subtitle) => new() { Text = title + "\n" + subtitle, FontSize = 25, FontWeight = FontWeight.SemiBold, LineHeight = 32, Margin = new Thickness(0, 0, 0, 12) };
    private Border Card(SensorReading sensor) => new() { Classes = { "card" }, Margin = new Thickness(0, 0, 12, 12), Child = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = sensor.SensorName, Classes = { "muted" } }, new TextBlock { Text = Format(sensor), FontSize = 25, FontWeight = FontWeight.SemiBold }, new ProgressBar { IsVisible = sensor.SensorType == SensorType.Load, Minimum = 0, Maximum = 100, Value = Math.Clamp(sensor.Value ?? 0, 0, 100), Height = 8 }, new TextBlock { Text = sensor.HardwareGroup + " · " + sensor.Hardware, Classes = { "muted" }, FontSize = 11 } } } };
    private static TextBlock Label(string text, int column = 0, bool muted = false) { var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }; if (muted) label.Classes.Add("muted"); Grid.SetColumn(label, column); return label; }
    private static string Format(SensorReading sensor) => sensor.Value is not float value ? "—" : sensor.SensorType switch { SensorType.Load => $"{value:0.0} %", SensorType.Data => $"{value:0.00} GB", SensorType.Throughput => FormatRate(value), SensorType.Clock => $"{value:0} MHz", _ => $"{value:0.0} {sensor.Unit}" };
    private static string FormatRate(float value) => Math.Abs(value) < 1024 ? $"{value:0} B/s" : Math.Abs(value) < 1_048_576 ? $"{value / 1024:0.0} KB/s" : $"{value / 1_048_576:0.00} MB/s";
}
