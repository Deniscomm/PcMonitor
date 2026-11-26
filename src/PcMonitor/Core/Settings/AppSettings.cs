using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace PcMonitor.Core;

/// <summary>
/// Настройки приложения. Хранятся рядом с приложением (portable-режим),
/// а если папка недоступна для записи — в %LOCALAPPDATA%\PcMonitor.
/// </summary>
public sealed class AppSettings
{
    public int SettingsSchemaVersion { get; set; } = 1;

    /// <summary>Режим интерфейса: Easy или Pro.</summary>
    public string InterfaceMode { get; set; } = "Easy";

    /// <summary>Язык интерфейса: ru, en-US, en-GB или zh-CN.</summary>
    public string Language { get; set; } = "en-US";

    /// <summary>Тема интерфейса: Dark, Gray или Light.</summary>
    public string Theme { get; set; } = "Dark";
    public int IntervalMs { get; set; } = 1000;
    public double TemperatureWarning { get; set; } = 70;
    public double TemperatureCritical { get; set; } = 85;

    /// <summary>Пороги предупреждения для накопителей (у SSD/HDD они ниже, чем у CPU).</summary>
    public double StorageTemperatureWarning { get; set; } = 55;
    public double StorageTemperatureCritical { get; set; } = 70;

    /// <summary>
    /// Температуры не ниже этого предела считаются недостоверными: на многих платах
    /// неподключённый термистор отдаёт фиксированные 100-127 °C. Такие значения
    /// видны в таблице, но не участвуют в сводке и не вызывают сигнал тревоги.
    /// </summary>
    public double MaxPlausibleTemperature { get; set; } = 105;

    /// <summary>Пороги по категориям устройств: ключ — группа («Процессор», «Видеокарта», …).</summary>
    public Dictionary<string, ThresholdProfile> Thresholds { get; set; } = CreateDefaultThresholds();

    public int HistoryPoints { get; set; } = 900;

    /// <summary>
    /// Окно графиков в секундах: плитки и график датчика показывают только последние
    /// N секунд, а не всю накопленную историю.
    /// </summary>
    public double ChartWindowSeconds { get; set; } = 120;

    public bool ShowOnlyWithValues { get; set; }

    // ============================ Трей и автозапуск ============================

    /// <summary>Запускать приложение при входе в Windows.</summary>
    public bool RunAtStartup { get; set; }

    /// <summary>Публиковать активность приложения в Discord при наличии Application ID.</summary>
    public bool DiscordPresenceEnabled { get; set; } = true;

    /// <summary>Application ID приложения PcMonitor из Discord Developer Portal.</summary>
    public string DiscordApplicationId { get; set; } = "1575105366331760731";

    /// <summary>Показывать подсказку при первом сворачивании в трей.</summary>
    public bool TrayNotifications { get; set; } = true;
    public bool LogToCsv { get; set; }
    public bool TopMost { get; set; }
    public bool AlarmSound { get; set; } = true;

    /// <summary>Согласие с disclaimer для стресс-тестов: null — решение ещё не принято.</summary>
    public bool? StressTestsAccepted { get; set; }

    /// <summary>Скрывать в таблице датчики-пороги (например, «Critical Temperature» накопителя).</summary>
    public bool HideThresholdSensors { get; set; }

    /// <summary>Скрывать в таблице недостоверные значения (неподключённые датчики платы).</summary>
    public bool HideImplausibleSensors { get; set; }

    /// <summary>Правила пользователя для отдельных датчиков: ключ — идентификатор датчика.</summary>
    public Dictionary<string, SensorOverride> SensorOverrides { get; set; } = new();

    /// <summary>Профили вентиляторов. Применяются только после явного включения в текущем запуске.</summary>
    public Dictionary<string, FanProfile> FanProfiles { get; set; } = new();

    /// <summary>Плитки дашборда в порядке отображения.</summary>
    public List<TileConfig> Tiles { get; set; } = DefaultTiles();

    /// <summary>Последняя открытая вкладка.</summary>
    public int LastTab { get; set; }

    /// <summary>Последняя открытая вкладка внутри раздела тестов.</summary>
    public int LastStressTab { get; set; }

    /// <summary>Своя папка для журналов (пусто — logs рядом с данными приложения).</summary>
    public string LogDirectoryOverride { get; set; } = string.Empty;

    /// <summary>Показывать экранный оверлей (в духе внешних приложений).</summary>
    public bool OsdEnabled { get; set; }

    /// <summary>Угол экрана для оверлея: LeftTop, RightTop, LeftBottom, RightBottom.</summary>
    public string OsdCorner { get; set; } = "LeftTop";

    public int OsdOffsetX { get; set; } = 16;

    public int OsdOffsetY { get; set; } = 16;

    public double OsdOpacity { get; set; } = 0.85;

    public double OsdFontSize { get; set; } = 14;

    /// <summary>Метрики, выводимые в оверлее (ключи из каталога метрик).</summary>
    public List<string> OsdMetrics { get; set; } = new()
    {
        MetricCatalog.CpuTemperature,
        MetricCatalog.CpuLoad,
        MetricCatalog.CpuClock,
        MetricCatalog.GpuTemperature,
        MetricCatalog.GpuLoad,
        MetricCatalog.GpuPower,
        MetricCatalog.RamLoad,
        MetricCatalog.MaxFanRpm,
    };

    /// <summary>Размер памяти для тестов, МБ.</summary>
    public int StressMemoryMegabytes { get; set; } = 1024;

    /// <summary>Размер файла для тестов диска, МБ.</summary>
    public int StressDiskMegabytes { get; set; } = 512;

    public int AutoTestPhaseSeconds { get; set; } = 30;
    public int AutoTestIdleSeconds { get; set; } = 10;
    public int AutoTestMemoryPatternSeconds { get; set; } = 10;

    /// <summary>Папка для файлов тестов диска (пусто — временная папка).</summary>
    public string StressDiskPath { get; set; } = string.Empty;

    /// <summary>Ручные типы накопителей: ключ — серийный номер, иначе идентификатор модели/слота.</summary>
    public Dictionary<string, string> StorageTypeOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public TimeSpan Interval => TimeSpan.FromMilliseconds(Math.Clamp(IntervalMs, 200, 60000));

    /// <summary>Каталог данных приложения (см. <see cref="ResolveDataDirectory"/>).</summary>
    public static string DataDirectory => _dataDirectory ??= ResolveDataDirectory();

    private static string? _dataDirectory;

    /// <summary>Папка, из которой запущено приложение.</summary>
    public static string BaseDirectory => _baseDirectory ??=
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    private static string? _baseDirectory;

    /// <summary>Файл-указатель на внешнюю папку данных (рядом с приложением).</summary>
    public static string DataPathFile => Path.Combine(BaseDirectory, "datapath.txt");

    /// <summary>Журналы измерений.</summary>
    public static string LogDirectory => Prepare(Path.Combine(DataDirectory, "logs"));

    /// <summary>Отчёты тестов и бенчмарков.</summary>
    public static string ReportsDirectory => Prepare(Path.Combine(DataDirectory, "reports"));

    /// <summary>История прогонов (JSON).</summary>
    public static string HistoryDirectory => Prepare(Path.Combine(DataDirectory, "history"));

    /// <summary>Временные файлы тестов диска.</summary>
    public static string TestsDirectory => Prepare(Path.Combine(DataDirectory, "tests"));

    /// <summary>Профили и резервные копии настроек.</summary>
    public static string ProfilesDirectory => Prepare(Path.Combine(DataDirectory, "profiles"));

    /// <summary>Данные лежат внутри папки приложения (portable-режим).</summary>
    public static bool IsPortable =>
        DataDirectory.StartsWith(BaseDirectory, StringComparison.OrdinalIgnoreCase);

    /// <summary>Папка для файлов теста диска по умолчанию (с учётом настройки пользователя).</summary>
    public string DiskTestDirectory =>
        string.IsNullOrWhiteSpace(StressDiskPath) ? TestsDirectory : StressDiskPath;

    private static string Prepare(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
            // Папку создадим при первой записи; ошибки обрабатываются вызывающим кодом.
        }

        return path;
    }

    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Dictionary<string, ThresholdProfile> CreateDefaultThresholds() => new()
    {
        ["Процессор"] = new ThresholdProfile(75, 90),
        ["Видеокарта"] = new ThresholdProfile(75, 83),
        ["Мат. плата"] = new ThresholdProfile(60, 75),
        ["Накопители"] = new ThresholdProfile(55, 70),
        ["По умолчанию"] = new ThresholdProfile(70, 85),
    };

    /// <summary>Пороги для группы устройств.</summary>
    public ThresholdProfile ThresholdsFor(string hardwareGroup)
    {
        if (Thresholds.TryGetValue(hardwareGroup, out ThresholdProfile? profile)) return profile;
        if (Thresholds.TryGetValue("По умолчанию", out ThresholdProfile? fallback)) return fallback;
        return new ThresholdProfile(TemperatureWarning, TemperatureCritical);
    }

    /// <summary>
    /// Папка данных определяется так (по порядку):
    /// 1) ключ командной строки <c>--data &lt;папка&gt;</c>;
    /// 2) путь из файла <c>datapath.txt</c> рядом с приложением — так данные можно держать
    ///    на другом диске, а саму программу переносить куда угодно;
    /// 3) подпапка <c>data</c> рядом с приложением (portable), если туда есть запись;
    /// 4) <c>%LOCALAPPDATA%\PcMonitor\data</c>.
    /// </summary>
    private static string ResolveDataDirectory()
    {
        string? fromArgs = null;
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--data", StringComparison.OrdinalIgnoreCase))
            {
                fromArgs = args[i + 1];
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(fromArgs) && TryPrepare(fromArgs, out string fromArgPath))
            return fromArgPath;

        try
        {
            if (File.Exists(DataPathFile))
            {
                string? candidate = File.ReadAllLines(DataPathFile)
                    .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim();

                if (!string.IsNullOrWhiteSpace(candidate) && TryPrepare(candidate, out string fromFile))
                    return fromFile;
            }
        }
        catch
        {
            // Повреждённый файл-указатель игнорируем и переходим к обычным вариантам.
        }

        if (TryPrepare(Path.Combine(BaseDirectory, "data"), out string portable))
            return portable;

        string local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcMonitor", "data");
        TryPrepare(local, out string fallback);
        return fallback;
    }

    /// <summary>Проверяет, что в папку можно писать (создаёт её и пробный файл).</summary>
    private static bool TryPrepare(string path, out string resolved)
    {
        resolved = path;
        try
        {
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(full);
            string probe = Path.Combine(full, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            resolved = full;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Запоминает новую папку данных в <c>datapath.txt</c>. Применяется после перезапуска.
    /// </summary>
    public static bool SetDataDirectory(string path, out string message)
    {
        message = string.Empty;
        if (!TryPrepare(path, out string full))
        {
            message = "В выбранную папку нет доступа на запись.";
            return false;
        }

        try
        {
            File.WriteAllText(DataPathFile, full + Environment.NewLine);
            message = full;
            return true;
        }
        catch (Exception ex)
        {
            message = "Не удалось сохранить путь: " + ex.Message;
            return false;
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
            else
            {
                return CreateFirstRunSettings();
            }
        }
        catch
        {
            // Повреждённый файл настроек игнорируем и берём значения по умолчанию.
        }

        return new AppSettings();
    }

    private static AppSettings CreateFirstRunSettings()
    {
        var settings = new AppSettings
        {
            Language = DetectWindowsLanguage(),
            Theme = DetectWindowsTheme(),
        };
        settings.Save();
        return settings;
    }

    private static string DetectWindowsLanguage()
    {
        try
        {
            CultureInfo culture = CultureInfo.CurrentUICulture;
            if (culture.Name.Equals("en-GB", StringComparison.OrdinalIgnoreCase)) return "en-GB";
            if (culture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase)) return "ru";
            if (culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
            return "en-US";
        }
        catch
        {
            return "en-US";
        }
    }

    private static string DetectWindowsTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? value = key?.GetValue("AppsUseLightTheme") ?? key?.GetValue("SystemUsesLightTheme");
            return value is not null && Convert.ToInt32(value, CultureInfo.InvariantCulture) == 1
                ? "Light"
                : "Dark";
        }
        catch
        {
            return "Dark";
        }
    }

    /// <summary>Дополняет настройки недостающими значениями (после обновления версии).</summary>
    private void Normalize()
    {
        if (SettingsSchemaVersion < 1)
        {
            SettingsSchemaVersion = 1;
        }

        if (!string.Equals(InterfaceMode, "Pro", StringComparison.OrdinalIgnoreCase)) InterfaceMode = "Easy";
        if (Language is not ("ru" or "en-US" or "en-GB" or "zh-CN")) Language = "en-US";
        if (Theme is not ("Dark" or "Gray" or "Light")) Theme = "Dark";
        DiscordApplicationId ??= string.Empty;

        if (SensorOverrides is null) SensorOverrides = new Dictionary<string, SensorOverride>();
        StorageTypeOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Tiles is null) Tiles = new List<TileConfig>();
        if (OsdMetrics is null || OsdMetrics.Count == 0)
        {
            OsdMetrics = new List<string>
            {
                MetricCatalog.CpuTemperature,
                MetricCatalog.CpuLoad,
                MetricCatalog.GpuTemperature,
                MetricCatalog.GpuLoad,
                MetricCatalog.RamLoad,
            };
        }

        if (Thresholds is null || Thresholds.Count == 0)
        {
            Thresholds = CreateDefaultThresholds();
        }
        else
        {
            foreach (KeyValuePair<string, ThresholdProfile> pair in CreateDefaultThresholds())
            {
                if (!Thresholds.ContainsKey(pair.Key)) Thresholds[pair.Key] = pair.Value;
            }
        }

        if (Tiles.Count == 0) Tiles = DefaultTiles();
        FanProfiles ??= new Dictionary<string, FanProfile>();
        foreach (string id in FanProfiles.Keys.ToArray())
        {
            if (FanProfiles[id] is null) FanProfiles.Remove(id);
            else FanProfiles[id].Normalize();
        }
    }

    /// <summary>Набор плиток, который видит пользователь при первом запуске.</summary>
    public static List<TileConfig> DefaultTiles() => new()
    {
        new TileConfig { MetricKey = MetricCatalog.CpuTemperature, Visual = TileVisual.Gauge, Width = 300, Height = 220 },
        new TileConfig { MetricKey = MetricCatalog.CpuLoad, Visual = TileVisual.Bar, Width = 300, Height = 220 },
        new TileConfig { MetricKey = MetricCatalog.GpuTemperature, Visual = TileVisual.Gauge, Width = 300, Height = 220 },
        new TileConfig { MetricKey = MetricCatalog.GpuLoad, Visual = TileVisual.Bar, Width = 300, Height = 220 },
        new TileConfig { MetricKey = MetricCatalog.RamLoad, Visual = TileVisual.Bar, Width = 300, Height = 220 },
        new TileConfig { MetricKey = MetricCatalog.MaxFanRpm, Visual = TileVisual.Value },
        new TileConfig { MetricKey = MetricCatalog.MaxStorageTemperature, Visual = TileVisual.Value },
        new TileConfig { MetricKey = MetricCatalog.NetworkDownload, Visual = TileVisual.Value },
        new TileConfig { MetricKey = MetricCatalog.CpuClock, Visual = TileVisual.Value },
        new TileConfig { MetricKey = MetricCatalog.GpuPower, Visual = TileVisual.Value },
        new TileConfig { MetricKey = MetricCatalog.RamUsedGb, Visual = TileVisual.Value },
        new TileConfig { MetricKey = MetricCatalog.VramUsed, Visual = TileVisual.Value },
    };

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            string temporary = SettingsPath + ".tmp";
            string json = JsonSerializer.Serialize(this, Options);
            using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(true);
            }
            File.Move(temporary, SettingsPath, true);
        }
        catch
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Options));
            }
            catch
            {
                // Если папка недоступна, приложение продолжает работу без потери текущего сеанса.
            }
        }
    }

    /// <summary>Папка журналов с учётом пользовательской настройки.</summary>
    public string ActiveLogDirectory =>
        string.IsNullOrWhiteSpace(LogDirectoryOverride) ? LogDirectory : Prepare(LogDirectoryOverride);

    /// <summary>Создаёт папку журналов, если её ещё нет.</summary>
    public string EnsureLogDirectory() => Prepare(ActiveLogDirectory);
}
