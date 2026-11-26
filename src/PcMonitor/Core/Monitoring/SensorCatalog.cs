using System.Globalization;
using LibreHardwareMonitor.Hardware;

namespace PcMonitor.Core;

/// <summary>Типы датчиков и форматирование значений с локализованными единицами.</summary>
public static class SensorCatalog
{
    /// <summary>Единица измерения для типа датчика.</summary>
    public static string Unit(SensorType type, string? language = null) => type switch
    {
        SensorType.Voltage => LocalizedUnit("В", "V", "V", language),
        SensorType.Current => LocalizedUnit("А", "A", "A", language),
        SensorType.Power => LocalizedUnit("Вт", "W", "瓦", language),
        SensorType.Clock => LocalizedUnit("МГц", "MHz", "MHz", language),
        SensorType.Temperature => "°C",
        SensorType.Load => "%",
        SensorType.Frequency => LocalizedUnit("Гц", "Hz", "Hz", language),
        SensorType.Fan => LocalizedUnit("об/мин", "RPM", "转/分", language),
        SensorType.Flow => LocalizedUnit("л/ч", "L/h", "升/小时", language),
        SensorType.Control => "%",
        SensorType.Level => "%",
        SensorType.Factor => "",
        SensorType.Data => LocalizedUnit("ГБ", "GB", "GB", language),
        SensorType.SmallData => LocalizedUnit("МБ", "MB", "MB", language),
        SensorType.Throughput => LocalizedUnit("Б/с", "B/s", "B/s", language),
        SensorType.TimeSpan => "",
        SensorType.Energy => LocalizedUnit("мВт·ч", "mWh", "毫瓦时", language),
        SensorType.Humidity => "%",
        SensorType.Conductivity => LocalizedUnit("мкСм/см", "µS/cm", "微西门子/厘米", language),
        SensorType.Timing => "",
        _ => "",
    };

    private static string LocalizedUnit(string russian, string english, string chinese, string? language)
    {
        string selected = language ?? LocalizationManager.Language;
        return selected switch
        {
            "ru" or "ru-RU" => russian,
            "zh-CN" => chinese,
            _ => english,
        };
    }

    /// <summary>Русское название типа датчика.</summary>
    public static string TypeName(SensorType type) => type switch
    {
        SensorType.Voltage => "Напряжение",
        SensorType.Current => "Ток",
        SensorType.Power => "Мощность",
        SensorType.Clock => "Частота",
        SensorType.Temperature => "Температура",
        SensorType.Load => "Загрузка",
        SensorType.Frequency => "Частота шины",
        SensorType.Fan => "Вентилятор",
        SensorType.Flow => "Поток",
        SensorType.Control => "Управление",
        SensorType.Level => "Уровень",
        SensorType.Factor => "Множитель",
        SensorType.Data => "Объём данных",
        SensorType.SmallData => "Объём данных",
        SensorType.Throughput => "Скорость",
        SensorType.TimeSpan => "Время",
        SensorType.Energy => "Энергия",
        SensorType.Humidity => "Влажность",
        SensorType.Conductivity => "Проводимость",
        _ => type.ToString(),
    };

    /// <summary>Порядок групп датчиков в интерфейсе.</summary>
    public static int TypeOrder(SensorType type) => type switch
    {
        SensorType.Temperature => 0,
        SensorType.Fan => 1,
        SensorType.Control => 2,
        SensorType.Load => 3,
        SensorType.Clock => 4,
        SensorType.Power => 5,
        SensorType.Voltage => 6,
        SensorType.Current => 7,
        SensorType.Data => 8,
        SensorType.SmallData => 9,
        SensorType.Throughput => 10,
        SensorType.Level => 11,
        SensorType.Factor => 12,
        SensorType.Frequency => 13,
        SensorType.Energy => 14,
        SensorType.TimeSpan => 15,
        SensorType.Humidity => 16,
        SensorType.Conductivity => 17,
        SensorType.Flow => 18,
        _ => 99,
    };

    private static readonly Dictionary<string, string> HardwareGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cpu"] = "Процессор",
        ["Motherboard"] = "Мат. плата",
        ["SuperIO"] = "Мат. плата",
        ["Memory"] = "Оперативная память",
        ["GpuNvidia"] = "Видеокарта",
        ["GpuAmd"] = "Видеокарта",
        ["GpuIntel"] = "Видеокарта",
        ["Storage"] = "Накопители",
        ["Network"] = "Сеть",
        ["Cooler"] = "Охлаждение",
        ["EmbeddedController"] = "Контроллер",
        ["Psu"] = "Блок питания",
        ["Battery"] = "Батарея",
        ["PowerMonitor"] = "Питание",
    };

    public static string HardwareGroupName(HardwareType type) =>
        HardwareGroups.TryGetValue(type.ToString(), out string? name) ? name : type.ToString();

    private static readonly string[] ThresholdMarkers =
    {
        "warning temperature",
        "critical temperature",
        "threshold",
        "порог",
        "alarm",
        "limit",
    };

    /// <summary>
    /// Некоторые устройства отдают не измерение, а настроенный порог
    /// (например, у NVMe: «Warning Temperature» и «Critical Temperature»).
    /// Такие датчики показываются, но не участвуют в сводке и тревогах.
    /// </summary>
    public static bool IsThresholdSensor(SensorType type, string name)
    {
        if (type != SensorType.Temperature) return false;
        foreach (string marker in ThresholdMarkers)
        {
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Порядок групп железа в интерфейсе.</summary>
    public static int HardwareOrder(HardwareType type) => type.ToString() switch
    {
        "Cpu" => 0,
        "GpuNvidia" or "GpuAmd" or "GpuIntel" => 1,
        "Memory" => 2,
        "Motherboard" or "SuperIO" => 3,
        "Storage" => 4,
        "Cooler" or "EmbeddedController" => 5,
        "Network" => 6,
        "Psu" or "PowerMonitor" => 7,
        "Battery" => 8,
        _ => 9,
    };

    /// <summary>Форматирует показание и единицу по выбранной локали.</summary>
    public static string Format(SensorType type, double value, string? language = null)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return "—";

        string Number(string pattern) => LocalizationManager.FormatNumber(value, language, pattern);
        string unit = Unit(type, language);
        return type switch
        {
            SensorType.Temperature => $"{Number("0.0")} {unit}",
            SensorType.Voltage or SensorType.Current => $"{Number("0.###")} {unit}",
            SensorType.Power => $"{Number(Math.Abs(value) < 10 ? "0.00" : "0.0")} {unit}",
            SensorType.Clock => $"{Number("0")} {unit}",
            SensorType.Load or SensorType.Level or SensorType.Control or SensorType.Humidity => $"{Number("0.0")} {unit}",
            SensorType.Fan => $"{Number("0")} {unit}",
            SensorType.Data => $"{Number("0.00")} {unit}",
            SensorType.SmallData => $"{Number("0.0")} {unit}",
            SensorType.Throughput => FormatThroughput(value, language),
            SensorType.Frequency => FormatFrequency(value, language),
            SensorType.Energy => $"{Number("0.000")} {unit}",
            SensorType.Conductivity or SensorType.Flow => $"{Number("0.0")} {unit}",
            SensorType.TimeSpan => FormatTime(TimeSpan.FromSeconds(value), language),
            SensorType.Factor => Number("0.###"),
            _ => Number("0.###"),
        };
    }

    public static string FormatThroughput(double bytesPerSecond, string? language = null)
    {
        double v = Math.Abs(bytesPerSecond);
        if (v < 1024) return $"{LocalizationManager.FormatNumber(bytesPerSecond, language, "0")} {Unit(SensorType.Throughput, language)}";
        if (v < 1024 * 1024) return $"{LocalizationManager.FormatNumber(bytesPerSecond / 1024, language, "0.0")} {LocalizedUnit("КБ/с", "KB/s", "KB/s", language)}";
        if (v < 1024L * 1024 * 1024) return $"{LocalizationManager.FormatNumber(bytesPerSecond / (1024 * 1024), language, "0.00")} {LocalizedUnit("МБ/с", "MB/s", "MB/s", language)}";
        return $"{LocalizationManager.FormatNumber(bytesPerSecond / (1024L * 1024 * 1024), language, "0.00")} {LocalizedUnit("ГБ/с", "GB/s", "GB/s", language)}";
    }

    public static string FormatFrequency(double hz, string? language = null)
    {
        double v = Math.Abs(hz);
        if (v < 1000) return $"{LocalizationManager.FormatNumber(hz, language, "0")} {Unit(SensorType.Frequency, language)}";
        if (v < 1_000_000) return $"{LocalizationManager.FormatNumber(hz / 1000, language, "0.0")} {LocalizedUnit("кГц", "kHz", "kHz", language)}";
        if (v < 1_000_000_000) return $"{LocalizationManager.FormatNumber(hz / 1_000_000, language, "0.0")} {LocalizedUnit("МГц", "MHz", "MHz", language)}";
        return $"{LocalizationManager.FormatNumber(hz / 1_000_000_000, language, "0.00")} {LocalizedUnit("ГГц", "GHz", "GHz", language)}";
    }

    public static string FormatTime(TimeSpan span, string? language = null)
    {
        string days = LocalizedUnit("д", "d", "天", language);
        string hours = LocalizedUnit("ч", "h", "小时", language);
        string minutes = LocalizedUnit("мин", "min", "分", language);
        string seconds = LocalizedUnit("с", "s", "秒", language);
        string Number(double value, string pattern) => LocalizationManager.FormatNumber(value, language, pattern);

        if (span.TotalDays >= 1) return $"{Number((int)span.TotalDays, "0")} {days} {Number(span.Hours, "00")} {hours} {Number(span.Minutes, "00")} {minutes} {Number(span.Seconds, "00")} {seconds}";
        if (span.TotalHours >= 1) return $"{Number((int)span.TotalHours, "0")} {hours} {Number(span.Minutes, "00")} {minutes} {Number(span.Seconds, "00")} {seconds}";
        if (span.TotalMinutes >= 1) return $"{Number((int)span.TotalMinutes, "0")} {minutes} {Number(span.Seconds, "00")} {seconds}";
        return $"{Number(span.TotalSeconds, "0.0")} {seconds}";
    }

    /// <summary>Короткая подпись для нижней строки состояния.</summary>
    public static string ShortValue(SensorType type, double value) => Format(type, value);
}
