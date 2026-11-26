using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using PcMonitor.Core;
using PcMonitor.Core.Diagnostics;

namespace PcMonitor.Views;

/// <summary>Новая вкладка системной информации: собрана отдельно и не зависит от старого SystemInfoView.</summary>
public partial class SystemOverviewView : UserControl
{
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _scrollIdleTimer;
    private MonitorContext? _context;
    private int _refreshVersion;
    private bool _attached;
    private bool _isScrolling;
    private bool _hasLoadedSections;
    private IReadOnlyList<InfoSection>? _lastSections;
    private string? _renderedLanguage;

    public SystemOverviewView()
    {
        InitializeComponent();
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _refreshTimer.Tick += (_, _) =>
        {
            if (!_isScrolling && IsVisible) RefreshAsync();
        };

        _scrollIdleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(900),
        };
        _scrollIdleTimer.Tick += (_, _) =>
        {
            _isScrolling = false;
            _scrollIdleTimer.Stop();
        };

        PageScrollViewer.ScrollChanged += (_, e) =>
        {
            if (Math.Abs(e.VerticalChange) < 0.1) return;
            _isScrolling = true;
            _scrollIdleTimer.Stop();
            _scrollIdleTimer.Start();
        };
    }

    public void Attach(MonitorContext context)
    {
        if (_attached) return;
        _attached = true;
        _context = context;
        _refreshTimer.Start();
        RefreshAsync();
    }

    public void ApplyLanguage()
    {
        if (_context is null)
        {
            LocalizationManager.Apply(this, LocalizationManager.Language);
            return;
        }

        string language = LocalizationManager.Language;
        if (_lastSections is not null && string.Equals(_renderedLanguage, language, StringComparison.Ordinal))
            return;

        RefreshAsync();
    }

    public void ApplyTheme()
    {
        if (_lastSections is null) return;
        double verticalOffset = PageScrollViewer.VerticalOffset;
        BuildSections(_lastSections);
        PageScrollViewer.ScrollToVerticalOffset(verticalOffset);
    }

    private async void RefreshAsync()
    {
        if (_context is null) return;
        int version = ++_refreshVersion;
        string language = LocalizationManager.Language;
        IReadOnlyList<SensorRow> rows = _context.Rows.ToList();
        if (!_hasLoadedSections)
        {
            LoadingText.Text = LocalizationManager.T("Загрузка сведений о системе…", language);
            LoadingOverlay.Visibility = Visibility.Visible;
        }

        var stopwatch = Stopwatch.StartNew();
        StartupDiagnostics.Mark("system.refresh.begin");
        IReadOnlyList<InfoSection> sections;
        try
        {
            sections = await Task.Run(() => SystemInfoService.Collect(rows, language));
        }
        catch (Exception exception)
        {
            if (version != _refreshVersion || !IsLoaded) return;
            StartupDiagnostics.Mark($"system.refresh.failed type={exception.GetType().Name}");
            LoadingText.Text = LocalizationManager.T("Не удалось загрузить сведения о системе", language);
            LoadingOverlay.Visibility = Visibility.Visible;
            return;
        }

        stopwatch.Stop();
        StartupDiagnostics.Mark($"system.refresh.completed elapsedMs={stopwatch.ElapsedMilliseconds} sections={sections.Count}");
        if (version != _refreshVersion || !IsLoaded) return;
        double verticalOffset = PageScrollViewer.VerticalOffset;
        _lastSections = sections;
        _renderedLanguage = language;
        BuildSections(sections);
        _hasLoadedSections = true;
        LoadingOverlay.Visibility = Visibility.Collapsed;
        PageScrollViewer.ScrollToVerticalOffset(verticalOffset);
        LocalizationManager.Apply(this, language);
    }

    private void BuildSections(IReadOnlyList<InfoSection> sections)
    {
        SectionHost.Children.Clear();
        foreach (InfoSection section in sections)
            SectionHost.Children.Add(BuildSection(section));
    }

    private static Border BuildSection(InfoSection section)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = section.Title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextBrush"),
            Margin = new Thickness(0, 0, 0, 3),
        });

        if (!string.IsNullOrWhiteSpace(section.Subtitle))
        {
            body.Children.Add(new TextBlock
            {
                Text = section.Subtitle,
                FontSize = 11,
                Foreground = Brush("TextDimBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });
        }

        if (section.IsTable)
            body.Children.Add(BuildTable(section));
        else
            AddKeyValueRows(body, section.Items);

        return new Border
        {
            Background = Brush("PanelBg"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 10),
            Child = body,
        };
    }

    private static void AddKeyValueRows(Panel host, IEnumerable<InfoItem> items)
    {
        foreach (InfoItem item in items)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock
            {
                Text = item.Name,
                Foreground = Brush("TextDimBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            var value = new TextBlock
            {
                Text = item.Value,
                Foreground = Brush("TextBrush"),
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            host.Children.Add(row);
        }
    }

    private static DataGrid BuildTable(InfoSection section)
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            CanUserAddRows = false,
            CanUserResizeRows = false,
            GridLinesVisibility = DataGridGridLinesVisibility.None,
            Background = Brush("PanelBg"),
            Foreground = Brush("TextBrush"),
            BorderThickness = new Thickness(0),
            ItemsSource = section.Rows,
            MinHeight = 42,
        };

        for (int index = 0; index < section.Columns.Count; index++)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = section.Columns[index],
                Binding = new Binding($"[{index}]")
                {
                    Mode = BindingMode.OneWay,
                    ValidatesOnExceptions = false,
                },
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                ElementStyle = new Style(typeof(TextBlock))
                {
                    Setters =
                    {
                        new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap),
                        new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                    },
                },
            });
        }

        return grid;
    }

    private static Brush Brush(string key)
    {
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
}
