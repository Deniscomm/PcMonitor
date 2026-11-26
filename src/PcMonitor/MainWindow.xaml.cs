using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Win32;
using PcMonitor.Core;
using PcMonitor.Core.Diagnostics;
using PcMonitor.Core.Integrations;
using PcMonitor.Views;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace PcMonitor;

/// <summary>Главное окно: панель управления, вкладки, строка состояния и значок в трее.</summary>
public partial class MainWindow : Window
{
    private const int HotKeyId = 0x5043;
    private const int StopAllHotKeyId = 0x5044;
    private const uint ModWin = 0x0008;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const int WmHotKey = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly MonitorService _service = new();
    private readonly MonitorContext _context;
    private DiscordPresenceService? _discordPresence;

    private CsvWriter? _logger;
    private OsdWindow? _osd;
    private DateTime _lastAlarm = DateTime.MinValue;
    private bool _ready;
    private int _firstSnapshotApplied;
    private WinForms.NotifyIcon? _tray;
    private WinForms.ToolStripMenuItem? _trayOsdItem;
    private bool _trayHintShown;
    private readonly HashSet<int> _loadedTabs = new();

    public MainWindow()
    {
        StartupDiagnostics.Mark("MainWindow.begin");
        ThemeManager.Apply(_settings.Theme);
        InitializeComponent();
        StartupDiagnostics.Mark("MainWindow.xaml.initialized");

        _context = new MonitorContext(_settings, _service);
        _service.Interval = _settings.Interval;
        StartupDiagnostics.Mark("context.created");

        EnsureTabLoaded(0);
        StartupDiagnostics.Mark("overview.loaded");

        _service.SnapshotReady += OnSnapshotReady;
        _service.Failed += OnServiceFailed;

        RestoreUiState();
        ApplyInterfaceMode();
        ApplyLanguage();
        StartupDiagnostics.Mark("language.and.interface.applied");

        AdminBadge.Visibility = _service.IsElevated ? Visibility.Visible : Visibility.Collapsed;
        ElevationBadge.Visibility = _service.IsElevated ? Visibility.Collapsed : Visibility.Visible;

        SetupTray();
        _ready = true;
        UpdateDiscordPresence();
        StartupDiagnostics.Mark("MainWindow.ready");

        if (_settings.OsdEnabled)
            ToggleOsd(true);
    }

    private void ToolsToggle_Click(object sender, RoutedEventArgs e)
    {
        bool show = ToolsToggle.IsChecked == true;
        TranslateTransform transform = new(0, show ? -10 : 0);
        ToolsFlyout.RenderTransform = transform;

        if (show)
        {
            ToolsFlyout.Visibility = Visibility.Visible;
            ToolsFlyout.Opacity = 0;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            var slideIn = new DoubleAnimation(-10, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            ToolsFlyout.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            transform.BeginAnimation(TranslateTransform.YProperty, slideIn);
            return;
        }

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140));
        fadeOut.Completed += (_, _) =>
        {
            if (ToolsToggle.IsChecked != true)
                ToolsFlyout.Visibility = Visibility.Collapsed;
        };
        var slideOut = new DoubleAnimation(0, -8, TimeSpan.FromMilliseconds(140));
        ToolsFlyout.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        transform.BeginAnimation(TranslateTransform.YProperty, slideOut);
    }
}
