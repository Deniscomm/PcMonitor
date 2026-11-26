using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PcMonitor.Core;

namespace PcMonitor.Views;

/// <summary>Строка таблицы порогов в настройках.</summary>
public sealed class ThresholdRow
{
    private readonly string _group;

    public ThresholdRow(string group, double warning, double critical)
    {
        _group = group;
        Warning = warning;
        Critical = critical;
    }

    public string Group => LocalizationManager.T(_group);
    public double Warning { get; set; }
    public double Critical { get; set; }
}

/// <summary>Метрика оверлея с флажком включения.</summary>
public sealed class OsdMetricItem
{
    private readonly string _title;

    public OsdMetricItem(string key, string title, bool isChecked)
    {
        Key = key;
        _title = title;
        IsChecked = isChecked;
    }

    public string Key { get; }
    public string Title => LocalizationManager.T(_title);
    public bool IsChecked { get; set; }
}

/// <summary>Вкладка «Настройки»: пороги, отображение, пути, параметры тестов.</summary>
public partial class SettingsView : UserControl
{
    private MonitorContext? _context;
    private List<ThresholdRow> _thresholds = new();
    private List<OsdMetricItem> _osdMetrics = new();
    private bool _loading;
    private bool _suppressLanguageSelection;
    private bool _userChangingLanguage;
    private string _pendingLanguage = "ru";

    public SettingsView()
    {
        InitializeComponent();
        LanguageBox.PreviewMouseDown += (_, _) => _userChangingLanguage = true;
        LanguageBox.PreviewKeyDown += (_, _) => _userChangingLanguage = true;
        LanguageBox.DropDownClosed += Language_DropDownClosed;
    }

    public event Action? SettingsChanged;

    public event Action<string>? StatusChanged;
    public event Action<string>? LanguageChanged;

    /// <summary>Выбирает сохранённый язык, не трактуя это как выбор пользователя.</summary>
    public void RestoreLanguageSelection(string language)
    {
        bool loading = _loading;
        _suppressLanguageSelection = true;
        _loading = true;
        try
        {
            SelectTag(LanguageBox, language);
        }
        finally
        {
            _loading = loading;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            _suppressLanguageSelection = false;
        }));
    }

    public void RestoreThemeSelection(string theme) => SelectTag(ThemeBox, theme);

    public void ApplyLanguage(string language)
    {
        Load();
        ThresholdList.Items.Refresh();
        OsdMetricList.Items.Refresh();
        LocalizationManager.Apply(this, language);
        string[] themeNames = language == "zh-CN"
            ? new[] { "深色", "灰色", "浅色" }
            : language is "ru" or "ru-RU"
                ? new[] { "Тёмная", "Серая", "Светлая" }
                : new[] { "Dark", "Gray", "Light" };
        for (int i = 0; i < Math.Min(themeNames.Length, ThemeBox.Items.Count); i++)
            if (ThemeBox.Items[i] is ComboBoxItem item) item.Content = themeNames[i];
    }

    public void Attach(MonitorContext context)
    {
        _context = context;
        DataPathBox.Text = AppSettings.DataDirectory;
        Load();
    }

    private void Load()
    {
        if (_context is null) return;

        _loading = true;
        AppSettings settings = _context.Settings;
        _pendingLanguage = settings.Language;

        foreach (RadioButton radio in new[] { Interval05, Interval1, Interval2, Interval5, Interval10 })
        {
            if (radio.Tag is string tag && int.TryParse(tag, out int ms) && ms == settings.IntervalMs)
                radio.IsChecked = true;
        }

        SelectTag(LanguageBox, settings.Language);
        SelectTag(ThemeBox, settings.Theme);

        HistoryBox.Text = settings.HistoryPoints.ToString(CultureInfo.CurrentCulture);
        PlausibleBox.Text = settings.MaxPlausibleTemperature.ToString("0.#", CultureInfo.CurrentCulture);

        _thresholds = settings.Thresholds
            .Select(pair => new ThresholdRow(pair.Key, pair.Value.Warning, pair.Value.Critical))
            .ToList();
        ThresholdList.ItemsSource = _thresholds;

        HideThresholdsCheck.IsChecked = settings.HideThresholdSensors;
        HideImplausibleCheck.IsChecked = settings.HideImplausibleSensors;
        OnlyWithValuesCheck.IsChecked = settings.ShowOnlyWithValues;
        AlarmSoundCheck.IsChecked = settings.AlarmSound;
        TopMostCheck.IsChecked = settings.TopMost;
        DiscordPresenceCheck.IsChecked = settings.DiscordPresenceEnabled;
        DiscordApplicationIdBox.Text = settings.DiscordApplicationId;

        StartupCheck.IsChecked = StartupRegistration.IsEnabled;
        UpdateTrayStatus();

        LogPathBox.Text = settings.LogDirectoryOverride;
        StressMemoryBox.Text = settings.StressMemoryMegabytes.ToString(CultureInfo.CurrentCulture);
        StressDiskBox.Text = settings.StressDiskMegabytes.ToString(CultureInfo.CurrentCulture);
        StressPathBox.Text = settings.StressDiskPath;

        RulesInfo.Text = settings.SensorOverrides.Count == 0
            ? "Правил пока нет. Их можно задать правым кликом по датчику на вкладке «Датчики»."
            : $"Задано правил: {settings.SensorOverrides.Count} (переименование, скрытие, «не учитывать»).";

        UpdateFoldersText();

        OsdEnabledCheck.IsChecked = settings.OsdEnabled;
        foreach (ComboBoxItem item in OsdCornerBox.Items)
        {
            if (item.Tag is string tag && tag == settings.OsdCorner)
                OsdCornerBox.SelectedItem = item;
        }

        if (OsdCornerBox.SelectedItem is null && OsdCornerBox.Items.Count > 0)
            OsdCornerBox.SelectedIndex = 0;

        OsdOffsetBox.Text = $"{settings.OsdOffsetX}x{settings.OsdOffsetY}";
        OsdStyleBox.Text = $"{settings.OsdOpacity:0.##} / {settings.OsdFontSize:0.#}";

        _osdMetrics = MetricCatalog.Aggregates
            .Select(m => new OsdMetricItem(m.Key, $"{m.Group}: {m.Title}", settings.OsdMetrics.Contains(m.Key)))
            .ToList();
        OsdMetricList.ItemsSource = _osdMetrics;

        _loading = false;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveSettingsCore();
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(LocalizationManager.T("Не удалось применить настройки", _pendingLanguage) + ": " + ex.Message);
        }
    }

    private void SaveSettingsCore()
    {
        if (_context is null) return;

        AppSettings settings = _context.Settings;

        foreach (RadioButton radio in new[] { Interval05, Interval1, Interval2, Interval5, Interval10 })
        {
            if (radio.IsChecked == true && radio.Tag is string tag && int.TryParse(tag, out int ms))
                settings.IntervalMs = ms;
        }

        settings.Language = _pendingLanguage;
        if (ThemeBox.SelectedItem is ComboBoxItem { Tag: string theme }) settings.Theme = theme;
        settings.HistoryPoints = ParseInt(HistoryBox.Text, settings.HistoryPoints, 60, 20000);
        settings.MaxPlausibleTemperature = ParseDouble(PlausibleBox.Text, settings.MaxPlausibleTemperature);

        foreach (ThresholdRow row in _thresholds)
        {
            if (!settings.Thresholds.TryGetValue(row.Group, out ThresholdProfile? profile))
            {
                profile = new ThresholdProfile();
                settings.Thresholds[row.Group] = profile;
            }

            profile.Warning = row.Warning;
            profile.Critical = row.Critical;
        }

        settings.HideThresholdSensors = HideThresholdsCheck.IsChecked == true;
        settings.HideImplausibleSensors = HideImplausibleCheck.IsChecked == true;
        settings.ShowOnlyWithValues = OnlyWithValuesCheck.IsChecked == true;
        settings.AlarmSound = AlarmSoundCheck.IsChecked == true;
        settings.TopMost = TopMostCheck.IsChecked == true;
        settings.DiscordPresenceEnabled = DiscordPresenceCheck.IsChecked == true;
        settings.DiscordApplicationId = DiscordApplicationIdBox.Text.Trim();

        // Трей и автозапуск

        bool startupRequested = StartupCheck.IsChecked == true;
        settings.RunAtStartup = startupRequested;
        string startupResult = StartupRegistration.Set(startupRequested, false,
            _context.Service.IsElevated);
        settings.RunAtStartup = StartupRegistration.IsEnabled;
        StartupCheck.IsChecked = settings.RunAtStartup;
        UpdateTrayStatus();
        StatusChanged?.Invoke(startupResult);

        settings.LogDirectoryOverride = LogPathBox.Text.Trim();
        settings.StressMemoryMegabytes = ParseInt(StressMemoryBox.Text, settings.StressMemoryMegabytes, 64, 65536);
        settings.StressDiskMegabytes = ParseInt(StressDiskBox.Text, settings.StressDiskMegabytes, 16, 65536);
        settings.StressDiskPath = StressPathBox.Text.Trim();

        // Оверлей
        settings.OsdEnabled = OsdEnabledCheck.IsChecked == true;
        if (OsdCornerBox.SelectedItem is ComboBoxItem { Tag: string corner }) settings.OsdCorner = corner;
        (settings.OsdOffsetX, settings.OsdOffsetY) = ParseIntPair(OsdOffsetBox.Text, settings.OsdOffsetX, settings.OsdOffsetY);
        (settings.OsdOpacity, settings.OsdFontSize) = ParsePair(OsdStyleBox.Text, settings.OsdOpacity, settings.OsdFontSize, 0.2, 1.0, 10, 28);
        settings.OsdMetrics = _osdMetrics.Where(m => m.IsChecked).Select(m => m.Key).ToList();

        settings.Save();

        _context.Service.Interval = settings.Interval;
        foreach (SensorRow row in _context.Rows)
            row.SetRule(row.Rule);

        StatusText.Text = $"{LocalizationManager.T("настройки сохранены", settings.Language)} · {DateTime.Now:HH:mm:ss} · {LocalizationManager.Interval(settings.IntervalMs, settings.Language)}";
        StatusChanged?.Invoke(LocalizationManager.T("Настройки сохранены", settings.Language));
        SettingsChanged?.Invoke();
    }

    private void Language_DropDownClosed(object? sender, EventArgs e)
    {
        if (_loading || _suppressLanguageSelection || !_userChangingLanguage ||
            LanguageBox.SelectedItem is not ComboBoxItem { Tag: string language }) return;
        _userChangingLanguage = false;
        // WPF may raise a deferred SelectionChanged while the view is attaching.
        // Such an event is not a user action and must not overwrite the stored locale.
        if (!LanguageBox.IsKeyboardFocusWithin) return;
        if (_context is not null && string.Equals(_context.Settings.Language, language, StringComparison.Ordinal))
            return;
        _pendingLanguage = language;
        if (_context is null) return;

        // Language selection is an immediate setting; do not require a second click
        // on Save just to refresh the UI. Other settings still use Save settings.
        _context.Settings.Language = language;
        _context.Settings.Save();
        // MainWindow re-applies the language to every loaded view; applying here too
        // left parts of the window on the previous language.
        LanguageChanged?.Invoke(language);
    }

    private void OsdEnabledCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _context is null) return;

        bool enabled = OsdEnabledCheck.IsChecked == true;
        if (_context.Settings.OsdEnabled == enabled) return;

        _context.Settings.OsdEnabled = enabled;
        _context.Settings.Save();
        SettingsChanged?.Invoke();
        StatusChanged?.Invoke(LocalizationManager.T(
            enabled ? "Оверлей включён" : "Оверлей выключен", _pendingLanguage));
    }

    // ============================ Трей и автозапуск ============================

    private void StartupCheck_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = StartupRegistration.IsEnabled;
        StartupCheck.IsChecked = enabled;
        UpdateTrayStatus();
        StatusChanged?.Invoke(enabled
            ? LocalizationManager.T("Автозапуск включён", _pendingLanguage) + ": " + StartupRegistration.CurrentCommand
            : LocalizationManager.T("Автозапуск выключен", _pendingLanguage));
    }

    private void UpdateTrayStatus()
    {
        TrayStatusText.Text = StartupRegistration.IsEnabled
            ? LocalizationManager.T("Автозапуск включён", _pendingLanguage) + ": " + StartupRegistration.CurrentCommand
            : LocalizationManager.T("Автозапуск выключен", _pendingLanguage) + ". " + LocalizationManager.T("Путь к программе", _pendingLanguage) + ": " + StartupRegistration.ExecutablePath;
    }

    private void ResetRules_Click(object sender, RoutedEventArgs e)    {
        if (_context is null) return;

        _context.Settings.SensorOverrides.Clear();
        foreach (SensorRow row in _context.Rows)
            row.SetRule(null);

        _context.Settings.Save();
        RulesInfo.Text = LocalizationManager.T("Все правила для датчиков сброшены.", _pendingLanguage);
        StatusText.Text = LocalizationManager.T("правила сброшены", _pendingLanguage);
        SettingsChanged?.Invoke();
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => OpenFolder(AppSettings.DataDirectory);

    private void OpenReports_Click(object sender, RoutedEventArgs e) => OpenFolder(AppSettings.ReportsDirectory);

    private void OpenHistory_Click(object sender, RoutedEventArgs e) => OpenFolder(AppSettings.HistoryDirectory);

    /// <summary>Переносит папку данных в выбранное место (применяется после перезапуска).</summary>
    private void ChangeData_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите папку для хранения настроек, журналов и отчётов",
            InitialDirectory = AppSettings.DataDirectory,
            Multiselect = false,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        if (!AppSettings.SetDataDirectory(dialog.FolderName, out string message))
        {
            StatusText.Text = message;
            return;
        }

        DataPathBox.Text = AppSettings.DataDirectory;
        UpdateFoldersText();

        MessageBoxResult answer = MessageBox.Show(
            Window.GetWindow(this),
            "Новая папка данных:\n" + message + "\n\n" +
            "Перенести туда уже накопленные файлы и перезапустить приложение?",
            "Мониторинг ПК", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        try
        {
            CopyDirectory(AppSettings.DataDirectory, message);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "Не удалось перенести файлы:\n" + ex.Message,
                "Мониторинг ПК", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        try
        {
            string? exe = Environment.ProcessPath;
            if (exe is not null)
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                Application.Current.Shutdown();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Перезапустите приложение вручную: " + ex.Message;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination)) File.Copy(file, destination);
        }
    }

    private void UpdateFoldersText()
    {
        string language = _pendingLanguage;
        FoldersText.Text =
            $"{LocalizationManager.T("Журналы", language)}: {AppSettings.LogDirectory}\n" +
            $"{LocalizationManager.T("Отчёты", language)}: {AppSettings.ReportsDirectory}\n" +
            $"{LocalizationManager.T("История", language)}: {AppSettings.HistoryDirectory}\n" +
            $"{LocalizationManager.T("Файлы тестов диска", language)}: {AppSettings.TestsDirectory}\n" +
            (AppSettings.IsPortable
                ? LocalizationManager.T("Режим: переносимый — данные лежат внутри папки программы.", language)
                : LocalizationManager.T("Режим: данные вне папки программы, путь можно сменить кнопкой «Изменить папку данных…».", language));
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null) return;
        try
        {
            OpenFolder(_context.Settings.EnsureLogDirectory());
        }
        catch (Exception ex)
        {
            StatusText.Text = LocalizationManager.T("не удалось открыть журналы", _pendingLanguage) + ": " + ex.Message;
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            System.IO.Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = LocalizationManager.T("не удалось открыть папку", _pendingLanguage) + ": " + ex.Message;
        }
    }

    private static void SelectTag(ComboBox box, string tag)
    {
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
            string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
        if (box.SelectedItem is null && box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text.Trim(), out int value) ? Math.Clamp(value, min, max) : fallback;

    private static double ParseDouble(string text, double fallback) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : fallback;

    /// <summary>Разбор пары целых значений вида «16x16».</summary>
    private static (int First, int Second) ParseIntPair(string text, int fallbackFirst, int fallbackSecond)
    {
        (double first, double second) = ParsePair(text, fallbackFirst, fallbackSecond, 0, 4000);
        return ((int)first, (int)second);
    }

    /// <summary>Разбор пары значений вида «16x16» или «0.85 / 14».</summary>
    private static (double First, double Second) ParsePair(
        string text, double fallbackFirst, double fallbackSecond,
        double minFirst = -10000, double maxFirst = 10000,
        double minSecond = -10000, double maxSecond = 10000)
    {
        char[] separators = { 'x', 'х', '/', ';', ',', ' ' };
        string[] parts = text.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return (fallbackFirst, fallbackSecond);

        double first = ParseDouble(parts[0], fallbackFirst);
        double second = ParseDouble(parts[1], fallbackSecond);
        return (Math.Clamp(first, minFirst, maxFirst), Math.Clamp(second, minSecond, maxSecond));
    }
}
