using System.Collections.ObjectModel;
using System.Diagnostics;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using PcMonitor.Core;

namespace PcMonitor.Views.Dialogs;

/// <summary>Устройство, которое пользователь проверяет на подсветку.</summary>
public sealed class RgbDevice : INotifyPropertyChanged
{
    private bool _isChecked = true;

    public RgbDevice(string name) => Name = name;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }
}

/// <summary>Один режим подсветки для проверки «на глаз».</summary>
public sealed record RgbMode(string Name, string Hint, Color First, Color Second, bool Animated, double Period);

/// <summary>
/// Мастер проверки подсветки: показывает эталонный цвет/режим, пользователь сравнивает
/// с реальными устройствами и отвечает «совпадает / не совпадает / нет такого».
/// </summary>
public partial class RgbWizardWindow : Window
{
    private readonly List<RgbMode> _modes = new()
    {
        new RgbMode("Красный", "Устройства должны светиться чистым красным", Colors.Red, Colors.Red, false, 1),
        new RgbMode("Зелёный", "Проверьте, что зелёный не уходит в жёлтый или бирюзовый", Color.FromRgb(0, 220, 60), Color.FromRgb(0, 220, 60), false, 1),
        new RgbMode("Синий", "Проверьте, что синий не уходит в фиолетовый", Color.FromRgb(30, 90, 255), Color.FromRgb(30, 90, 255), false, 1),
        new RgbMode("Белый", "Белый показывает качество смешивания: не должно быть розового или зелёного оттенка", Colors.White, Colors.White, false, 1),
        new RgbMode("Плавный переход", "Цвет должен плавно переходить без рывков", Colors.Red, Color.FromRgb(30, 90, 255), true, 4),
        new RgbMode("Радуга", "Полный спектр, все цвета по кругу", Colors.Red, Colors.Violet, true, 6),
        new RgbMode("Дыхание", "Яркость плавно нарастает и угасает", Color.FromRgb(180, 30, 30), Color.FromRgb(255, 200, 200), true, 3),
        new RgbMode("Мигание", "Резкое переключение цветов", Colors.OrangeRed, Colors.Cyan, true, 1),
        new RgbMode("Выключено", "Подсветка должна полностью погаснуть", Colors.Black, Colors.Black, false, 1),
    };

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly List<string> _answers = new();
    private int _index;

    public RgbWizardWindow()
    {
        InitializeComponent();
        LocalizationManager.Apply(this, LocalizationManager.Language);
        Devices = new ObservableCollection<RgbDevice>
        {
            new("Планки памяти"),
            new("Вентиляторы корпуса"),
            new("Кулер процессора"),
            new("Видеокарта"),
            new("Лента ARGB на плате"),
            new("Клавиатура / мышь"),
            new("Другое устройство"),
        };

        DeviceList.ItemsSource = Devices;
        ModeList.ItemsSource = _modes.Select(m => m.Name).ToList();
        DetectSoftware();
        _timer.Tick += (_, _) => Animate();
        _timer.Start();
        ShowMode(0);
    }

    private List<RgbSoftware> _software = new();

    /// <summary>Ищет установленные программы управления подсветкой.</summary>
    private void DetectSoftware()
    {
        try
        {
            _software = RgbSoftwareDetector.Detect().ToList();
            SoftwareList.ItemsSource = _software;

            SoftwareHint.Text = _software.Count == 0
                ? "Программы управления подсветкой не найдены. Установите утилиту производителя платы " +
                  "(или OpenRGB) — через неё можно менять цвета и режимы, а мастер проверит результат."
                : "Переключайте режимы в этих программах и сравнивайте с эталоном на экране: " +
                  string.Join("; ", _software.Take(4).Select(s => s.Name)) +
                  (_software.Count > 4 ? " и другие." : ".");
        }
        catch (Exception ex)
        {
            SoftwareHint.Text = "Не удалось выполнить поиск программ: " + ex.Message;
        }
    }

    private void DetectSoftware_Click(object sender, RoutedEventArgs e) => DetectSoftware();

    private void LaunchSoftware_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not RgbSoftware software) return;

        if (!software.CanLaunch)
        {
            MessageBox.Show(this,
                $"Не удалось найти исполняемый файл программы «{software.Name}».\n" +
                "Запустите её вручную и переключите режим подсветки.",
                "Проверка подсветки", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(software.ExecutablePath) { UseShellExecute = true });
            ResultsBox.Text = Summary + Environment.NewLine + $"Запущена программа: {software.Name}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось запустить программу:\n" + ex.Message,
                "Проверка подсветки", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public ObservableCollection<RgbDevice> Devices { get; }

    /// <summary>Текстовая сводка ответов мастера.</summary>
    public string Summary => _answers.Count == 0 ? "Проверка не выполнялась." : string.Join(Environment.NewLine, _answers);

    private void ShowMode(int index)
    {
        _index = Math.Clamp(index, 0, _modes.Count - 1);
        RgbMode mode = _modes[_index];

        ModeText.Text = $"{_index + 1} из {_modes.Count}: {mode.Name}";
        ModeHint.Text = mode.Hint;
        ProgressText.Text = $"Проверено режимов: {_answers.Count} из {_modes.Count}";
    }

    private void Animate()
    {
        RgbMode mode = _modes[_index];
        if (!mode.Animated)
        {
            Preview.Background = new SolidColorBrush(mode.First);
            return;
        }

        double phase = (DateTime.Now.TimeOfDay.TotalSeconds % mode.Period) / mode.Period;
        double wave = mode.Name switch
        {
            "Мигание" => phase < 0.5 ? 0 : 1,
            "Дыхание" => Math.Abs(Math.Sin(phase * Math.PI)),
            _ => phase,
        };

        byte Lerp(byte a, byte b) => (byte)(a + (b - a) * wave);

        Color color = mode.Name == "Радуга"
            ? FromHue(phase * 360)
            : Color.FromRgb(Lerp(mode.First.R, mode.Second.R), Lerp(mode.First.G, mode.Second.G), Lerp(mode.First.B, mode.Second.B));

        Preview.Background = new SolidColorBrush(color);
    }

    private static Color FromHue(double hue)
    {
        double h = hue / 60.0;
        double x = 1 - Math.Abs(h % 2 - 1);
        (double r, double g, double b) = (int)h switch
        {
            0 => (1.0, x, 0.0),
            1 => (x, 1.0, 0.0),
            2 => (0.0, 1.0, x),
            3 => (0.0, x, 1.0),
            4 => (x, 0.0, 1.0),
            _ => (1.0, 0.0, x),
        };

        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private void Match_Click(object sender, RoutedEventArgs e) => Answer("совпадает");

    private void Mismatch_Click(object sender, RoutedEventArgs e) => Answer("НЕ совпадает");

    private void NotApplicable_Click(object sender, RoutedEventArgs e) => Answer("нет такого устройства");

    private void Answer(string verdict)
    {
        RgbMode mode = _modes[_index];
        _answers.Add($"{mode.Name}: {verdict}");

        ResultsBox.Text = Summary;
        if (_index < _modes.Count - 1) ShowMode(_index + 1);
        else ProgressText.Text = $"Проверка завершена: {_answers.Count} режимов.";
    }

    private void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = Path.Combine(AppSettings.ReportsDirectory,
                $"rgb-check-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            var builder = new StringBuilder();
            builder.AppendLine("Проверка подсветки (RGB / ARGB) — Мониторинг ПК");
            builder.AppendLine($"Дата: {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
            builder.AppendLine();
            builder.AppendLine("Устройства для проверки:");
            foreach (RgbDevice device in Devices.Where(d => d.IsChecked))
                builder.AppendLine("  • " + device.Name);

            builder.AppendLine();
            builder.AppendLine("Ответы по режимам:");
            foreach (string answer in _answers) builder.AppendLine("  " + answer);

            builder.AppendLine();
            int mismatches = _answers.Count(a => a.Contains("НЕ совпадает", StringComparison.Ordinal));
            builder.AppendLine(mismatches == 0
                ? "Замечаний нет: все режимы отработали как на эталоне."
                : $"Режимов с расхождением: {mismatches}. Проверьте подключение ARGB-разъёмов, " +
                  "питание подсветки и настройки в фирменной утилите платы.");

            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
            MessageBox.Show(this, "Отчёт сохранён:\n" + path, "Проверка подсветки",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось сохранить отчёт:\n" + ex.Message, "Проверка подсветки",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
