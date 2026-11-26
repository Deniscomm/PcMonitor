using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using LibreHardwareMonitor.Hardware;

namespace PcMonitor.Core;

/// <summary>Строка таблицы датчиков: значение, минимум, максимум, история, цветовая индикация.</summary>
public sealed class SensorRow : INotifyPropertyChanged
{
    private double _min = double.NaN;
    private double _max = double.NaN;
    private bool _automaticPlausibility = true;
    private AppSettings? _settings;

    public SensorRow(SensorReading reading, AppSettings settings, SensorOverride? rule = null)
    {
        Id = reading.Id;
        Hardware = reading.Hardware;
        OwnName = reading.OwnName;
        HardwareGroup = reading.HardwareGroup;
        HardwareType = reading.HardwareType;
        HardwareOrder = SensorCatalog.HardwareOrder(reading.HardwareType);
        Type = reading.SensorType;
        TypeOrder = SensorCatalog.TypeOrder(reading.SensorType);
        Sensor = reading.SensorName;
        TypeName = LocalizationManager.T(SensorCatalog.TypeName(reading.SensorType));
        IsThreshold = SensorCatalog.IsThresholdSensor(reading.SensorType, reading.SensorName);
        History = new HistoryBuffer(Math.Clamp(settings.HistoryPoints, 60, 20000));
        Rule = rule;
        Apply(reading, settings);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }
    public string Hardware { get; }

    /// <summary>Собственное имя устройства без имени родителя (например, модуль памяти).</summary>
    public string OwnName { get; }

    public string HardwareGroup { get; private set; }
    public HardwareType HardwareType { get; }

    /// <summary>Порядок категории железа для сортировки по умолчанию.</summary>
    public int HardwareOrder { get; }

    public SensorType Type { get; }

    /// <summary>Порядок типа датчика для сортировки по умолчанию.</summary>
    public int TypeOrder { get; }

    /// <summary>Исходное название датчика от библиотеки.</summary>
    public string Sensor { get; }

    public string TypeName { get; private set; }
    public string Unit => SensorCatalog.Unit(Type);

    /// <summary>Датчик отдаёт настроенный порог, а не измерение (например, у накопителя).</summary>
    public bool IsThreshold { get; }

    public HistoryBuffer History { get; }

    /// <summary>Пользовательское правило для датчика (может быть null).</summary>
    public SensorOverride? Rule { get; private set; }

    /// <summary>Скрыт пользователем или автоматически (пороговые/недостоверные значения).</summary>
    public bool IsHidden =>
        Rule?.Hidden == true ||
        (IsThreshold && HideThresholds) ||
        (!IsPlausible && HideImplausible);

    /// <summary>Не учитывать в сводке, плитках и сигналах тревоги.</summary>
    public bool IsIgnored => Rule?.Ignore == true;

    public bool HideThresholds { get; private set; }
    public bool HideImplausible { get; private set; }

    public float? Value { get; private set; }

    /// <summary>Значение, пригодное для отображения (0 °C у температур означает «нет данных»).</summary>
    public float? DisplayValue =>
        Value.HasValue && !(Type == SensorType.Temperature && Math.Abs(Value.Value) < 0.01f)
            ? Value
            : null;

    /// <summary>Значение в разумных пределах (отсекает «мёртвые» датчики плат).</summary>
    public bool IsPlausible { get; private set; } = true;

    /// <summary>Датчик пригоден для сводки и сигналов тревоги.</summary>
    public bool IsMeasurement => IsPlausible && !IsThreshold && !IsIgnored;

    /// <summary>Название с учётом переименования пользователем.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Rule?.Alias) ? Sensor : Rule!.Alias!;

    public string ValueText
    {
        get
        {
            if (DisplayValue is not { } value) return "—";
            string text = SensorCatalog.Format(Type, value);
            if (IsThreshold) return $"{text} ({LocalizationManager.T("порог")})";
            if (!IsPlausible) return $"{text} ({LocalizationManager.T("недостоверно")})";
            return text;
        }
    }

    public string MinText => double.IsNaN(_min) ? "—" : SensorCatalog.Format(Type, _min);
    public string MaxText => double.IsNaN(_max) ? "—" : SensorCatalog.Format(Type, _max);
    public double Min => _min;
    public double Max => _max;

    public bool HasValue => DisplayValue.HasValue;

    public bool HasBar => Type is SensorType.Load or SensorType.Level or SensorType.Control;
    public double Bar => DisplayValue.HasValue ? Math.Clamp(DisplayValue.Value, 0, 100) : 0;

    /// <summary>Ширина мини-индикатора в пикселях (контейнер — 42 px).</summary>
    public double BarWidth => Math.Max(1, Bar / 100.0 * 42);

    public bool IsAlert { get; private set; }

    public Brush ValueBrush { get; private set; } = Theme.TextDim;
    public Brush BarBrush { get; private set; } = Theme.Accent;

    public string Tooltip
    {
        get
        {
            string extra = string.Empty;
            if (Rule?.PlausibilityOverride is bool trusted)
                extra = trusted
                    ? "\n\n" + LocalizationManager.T("Пользователь вручную разрешил учитывать это значение,\nдаже если оно выходит за автоматический диапазон.")
                    : "\n\n" + LocalizationManager.T("Пользователь вручную пометил значение как недостоверное;\nоно исключено из сводок и тревог.")
                      + " " + LocalizationManager.T("Выберите «Доверие: автоматическое», чтобы вернуть проверку диапазона.") + "\n"
                      + LocalizationManager.T("Это может скрыть реальный перегрев — применяйте осторожно.");
            else if (IsThreshold)
                extra = "\n\n" + LocalizationManager.T("Это настроенный порог устройства, а не измерение:\nтакое значение не участвует в сводке и тревогах.");
            else if (!IsPlausible)
                extra = "\n\n" + LocalizationManager.T("Внимание: значение вне допустимого диапазона —\nвероятно, датчик не подключён. Оно не влияет\nна сводку и сигнал тревоги.");
            else if (IsIgnored)
                extra = "\n\n" + LocalizationManager.T("Датчик помечен как неважный: не влияет на сводку.");

            return $"{LocalizationManager.T(HardwareGroup)} · {Hardware}\n{TypeName}: {DisplayName}\n" +
                   $"{LocalizationManager.T("Значение")}: {ValueText}\n" +
                   $"{LocalizationManager.T("Мин")}: {MinText}   {LocalizationManager.T("Макс")}: {MaxText}{extra}\n\n{Id}";
        }
    }

    public void Apply(SensorReading reading, AppSettings settings)
    {
        _settings = settings;
        // Обновляем группу из машинного типа на каждом снимке. Это исправляет
        // строки, созданные ранним snapshot со старым/повреждённым текстом группы.
        HardwareGroup = SensorCatalog.HardwareGroupName(reading.HardwareType);
        // Несколько источников объединяются по одному ID. LHM иногда публикует
        // пустой промежуточный снимок после корректного Rust-снимка; такой null
        // не должен стирать последнее действительное значение плитки и графика.
        if (reading.Value.HasValue || !Value.HasValue)
            Value = reading.Value;
        HideThresholds = settings.HideThresholdSensors;
        HideImplausible = settings.HideImplausibleSensors;

        if (DisplayValue.HasValue)
        {
            double v = DisplayValue.Value;
            _automaticPlausibility = Type != SensorType.Temperature || v < settings.MaxPlausibleTemperature;
            IsPlausible = Rule?.PlausibilityOverride ?? _automaticPlausibility;

            if (IsPlausible)
            {
                _min = double.IsNaN(_min) ? v : Math.Min(_min, v);
                _max = double.IsNaN(_max) ? v : Math.Max(_max, v);
            }

            History.Add((float)v);
        }
        else
        {
            _automaticPlausibility = true;
            IsPlausible = Rule?.PlausibilityOverride ?? true;
            History.Add(float.NaN);
        }

        UpdateVisuals(settings);
        RaiseAll();
    }

    /// <summary>Применение изменённого правила пользователя.</summary>
    public void SetRule(SensorOverride? rule)
    {
        Rule = rule;
        IsPlausible = rule?.PlausibilityOverride ?? _automaticPlausibility;
        if (_settings is not null) UpdateVisuals(_settings);
        RaiseAll();
    }

    public void RefreshLanguage()
    {
        TypeName = LocalizationManager.T(SensorCatalog.TypeName(Type));
        RaiseAll();
    }

    public void ResetMinMax()
    {
        _min = double.NaN;
        _max = double.NaN;
        History.Clear();
        RaiseAll();
    }

    private void UpdateVisuals(AppSettings settings)
    {
        if (!DisplayValue.HasValue)
        {
            ValueBrush = Theme.TextFaint;
            BarBrush = Theme.TextFaint;
            IsAlert = false;
            return;
        }

        if (!IsPlausible || IsThreshold || IsIgnored)
        {
            ValueBrush = Theme.TextFaint;
            BarBrush = Theme.TextFaint;
            IsAlert = false;
            return;
        }

        double value = DisplayValue.Value;
        bool isTemperature = Type == SensorType.Temperature;
        ThresholdProfile profile = settings.ThresholdsFor(HardwareGroup);

        IsAlert = isTemperature && value >= profile.Critical;

        if (isTemperature)
        {
            ValueBrush = value >= profile.Critical ? Theme.Bad : value >= profile.Warning ? Theme.Warn : Theme.Good;
            BarBrush = ValueBrush;
        }
        else if (Type is SensorType.Load or SensorType.Level or SensorType.Control)
        {
            BarBrush = value >= 90 ? Theme.Bad : value >= 70 ? Theme.Warn : Theme.Accent;
            ValueBrush = Theme.Text;
        }
        else if (Type == SensorType.Fan && value <= 0.5)
        {
            ValueBrush = Theme.TextFaint;
            BarBrush = Theme.TextFaint;
        }
        else
        {
            ValueBrush = Theme.Text;
            BarBrush = Theme.Accent;
        }
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(DisplayValue));
        OnPropertyChanged(nameof(ValueText));
        OnPropertyChanged(nameof(MinText));
        OnPropertyChanged(nameof(MaxText));
        OnPropertyChanged(nameof(Unit));
        OnPropertyChanged(nameof(HasValue));
        OnPropertyChanged(nameof(HasBar));
        OnPropertyChanged(nameof(Bar));
        OnPropertyChanged(nameof(BarWidth));
        OnPropertyChanged(nameof(IsAlert));
        OnPropertyChanged(nameof(IsPlausible));
        OnPropertyChanged(nameof(IsHidden));
        OnPropertyChanged(nameof(IsIgnored));
        OnPropertyChanged(nameof(IsMeasurement));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ValueBrush));
        OnPropertyChanged(nameof(BarBrush));
        OnPropertyChanged(nameof(Tooltip));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
