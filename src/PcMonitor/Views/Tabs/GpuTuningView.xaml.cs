using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PcMonitor.Core;
using PcMonitor.Core.Gpu;

namespace PcMonitor.Views;

/// <summary>
/// Вкладка «Разгон GPU»: лимит мощности и блокировка частот через официальный
/// интерфейс драйвера (nvidia-smi), с живым контролем результата.
/// </summary>
public partial class GpuTuningView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private NvidiaGpuStatus _status = new(false, "NVIDIA GPU", 0, 0, 0, 0, 0, 0, 0, 0, "нет данных");
    private bool _busy;

    public GpuTuningView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>Сообщение для строки состояния окна.</summary>
    public event Action<string>? StatusChanged;

    public void Attach(MonitorContext context)
    {
        // Настройки мониторинга здесь не нужны: состояние читается у драйвера.
    }

    public void Stop() => _timer.Stop();

    /// <summary>Опрашивать драйвер только пока вкладка видна.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            if (value) _timer.Start();
            else _timer.Stop();
        }
    }

    private bool _isActive = true;

    // ============================ Чтение состояния ============================

    private void Refresh()
    {
        if (_busy) return;

        _status = NvidiaSmi.Read();
        GpuNameText.Text = _status.Available
            ? $"{_status.Name} · драйвер отвечает"
            : "Видеокарта NVIDIA не найдена: " + _status.Diagnostics;

        if (!_status.Available)
        {
            StatePowerText.Text = StateTempText.Text = StateCoreText.Text = StateMemoryText.Text = "—";
            return;
        }

        StatePowerText.Text = $"{_status.PowerLimitWatts:0} Вт";
        StateTempText.Text = $"{_status.TemperatureC} °C";
        StateCoreText.Text = $"{_status.CoreClockMhz} МГц";
        StateMemoryText.Text = $"{_status.MemoryClockMhz} МГц";

        PowerRangeText.Text = $"Драйвер допускает {_status.PowerRangeText}; заводское значение {_status.PowerDefaultWatts:0} Вт";

        if (Math.Abs(PowerSlider.Minimum - _status.PowerMinWatts) > 0.5 || Math.Abs(PowerSlider.Maximum - _status.PowerMaxWatts) > 0.5)
        {
            PowerSlider.Minimum = Math.Max(30, _status.PowerMinWatts);
            PowerSlider.Maximum = Math.Max(PowerSlider.Minimum + 5, _status.PowerMaxWatts);
        }

        // Значение ползунка показываем по драйверу, но не мешаем пользователю тянуть его мышью.
        if (!PowerSlider.IsMouseOver && !PowerSlider.IsKeyboardFocusWithin && !_busy)
            PowerSlider.Value = _status.PowerLimitWatts;

        PowerValueText.Text = $"{PowerSlider.Value:0} Вт";
        LimitsText.Text = $"Текущий режим: лимит {_status.PowerLimitWatts:0} Вт, вентилятор {_status.FanPercent} %, " +
                          $"температура {_status.TemperatureC} °C, частоты {_status.CoreClockMhz}/{_status.MemoryClockMhz} МГц " +
                          "(ядро/память).";
        MemoryLockText.Text = "Блокировка памяти: " + NvidiaSmi.MemoryLockInfo();
    }

    private void Power_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        PowerValueText.Text = $"{e.NewValue:0} Вт";
    }

    // ============================ Действия ============================

    private void ApplyPower_Click(object sender, RoutedEventArgs e) =>
        Apply($"Лимит мощности {PowerSlider.Value:0} Вт", () => NvidiaSmi.SetPowerLimit(PowerSlider.Value));

    private void DefaultPower_Click(object sender, RoutedEventArgs e) =>
        Apply($"Заводской лимит {_status.PowerDefaultWatts:0} Вт", () => NvidiaSmi.SetPowerLimit(_status.PowerDefaultWatts));

    private void MaxPower_Click(object sender, RoutedEventArgs e) =>
        Apply($"Максимальный лимит {_status.PowerMaxWatts:0} Вт", () => NvidiaSmi.SetPowerLimit(_status.PowerMaxWatts));

    private void Quiet_Click(object sender, RoutedEventArgs e) =>
        Apply($"Тихий режим {_status.PowerMinWatts:0} Вт", () => NvidiaSmi.SetPowerLimit(_status.PowerMinWatts));

    private void ApplyCore_Click(object sender, RoutedEventArgs e)
    {
        int minimum = ParseInt(CoreMinBox.Text, 0);
        int maximum = ParseInt(CoreMaxBox.Text, 0);
        if (minimum <= 0 && maximum <= 0)
        {
            Log("Укажите частоты: например 1600 и 1600 для фиксации.");
            return;
        }

        if (minimum <= 0) minimum = maximum;
        if (maximum <= 0) maximum = minimum;

        Apply($"Блокировка ядра {minimum}…{maximum} МГц", () => NvidiaSmi.LockCoreClocks(minimum, maximum));
    }

    private void ResetClocks_Click(object sender, RoutedEventArgs e) =>
        Apply("Снятие блокировки частот", () => NvidiaSmi.ResetClocks());

    private void Lock1600_Click(object sender, RoutedEventArgs e)
    {
        CoreMinBox.Text = "1600";
        CoreMaxBox.Text = "1600";
        Apply("Стабильные 1600 МГц", () => NvidiaSmi.LockCoreClocks(1600, 1600));
    }

    private void Afterburner_Click(object sender, RoutedEventArgs e)
    {
        string? path = RgbSoftwareDetector.FindExecutable("afterburner");
        if (path is null)
        {
            Log("внешних приложений не найден. Кривые вентиляторов и смещения частот меняются в нём или в фирменной утилите.");
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            Log("Запущена программа: " + path);
        }
        catch (Exception ex)
        {
            Log("Не удалось запустить Afterburner: " + ex.Message);
        }
    }

    private void Apply(string title, Func<string> action)
    {
        _busy = true;
        try
        {
            string result = action();
            Log($"{title}: {result}");
            StatusChanged?.Invoke(result);
        }
        catch (Exception ex)
        {
            Log($"{title}: ошибка — {ex.Message}");
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int value) ? value : fallback;
}
