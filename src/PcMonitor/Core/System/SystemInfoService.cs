using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using LibreHardwareMonitor.Hardware;
using PcMonitor.Core.Gpu;

namespace PcMonitor.Core;

/// <summary>Строка «параметр — значение» в разделе информации.</summary>
public sealed record InfoItem(string Name, string Value);

/// <summary>Раздел вкладки «Система»: либо список параметров, либо таблица.</summary>
public sealed class InfoSection
{
    public InfoSection(string title, string subtitle = "")
    {
        Title = title;
        Subtitle = subtitle;
    }

    public string Title { get; }
    public string Subtitle { get; }
    public List<InfoItem> Items { get; } = new();
    public List<string> Columns { get; } = new();
    public List<string[]> Rows { get; } = new();

    public bool IsTable => Columns.Count > 0;
}

/// <summary>
/// Собирает сведения о железе в духе внешних системных утилит / внешних системных утилит / внешних утилит памяти:
/// BIOS, плата, процессор, модули памяти с таймингами SPD, видеокарты, диски, сеть.
/// </summary>
public static class SystemInfoService
{
    [DllImport("kernel32.dll")]
    private static extern bool GetFirmwareType(out int firmwareType);

    public static IReadOnlyList<InfoSection> Collect(IReadOnlyList<SensorRow> rows, string? language = null)
    {
        string currentLanguage = language ?? LocalizationManager.Language;
        IReadOnlyList<StorageDisk> disks = StorageInventory.Collect();
        var sections = new List<InfoSection>
        {
            ComputerSection(),
            BoardSection(),
            CpuSection(rows),
            MemorySection(rows),
            SpdTimingSection(rows),
            GpuSection(rows),
            StorageSection(rows, currentLanguage, disks),
        };

        InfoSection volumes = VolumesSection(currentLanguage, disks);
        if (volumes.Rows.Count > 0) sections.Add(volumes);

        sections.Add(NetworkSection());
        return LocalizeSections(sections, currentLanguage);
    }

    private static IReadOnlyList<InfoSection> LocalizeSections(IEnumerable<InfoSection> sections, string language)
    {
        static string Label(string text, string language) => LocalizationManager.T(text, language);
        static string Value(string text, string language) => LocalizationManager.SystemValue(text, language);

        return sections.Select(source =>
        {
            var localized = new InfoSection(Label(source.Title, language), Label(source.Subtitle, language));
            localized.Items.AddRange(source.Items.Select(item =>
                new InfoItem(Label(item.Name, language), Value(item.Value, language))));
            localized.Columns.AddRange(source.Columns.Select(column => Label(column, language)));
            localized.Rows.AddRange(source.Rows.Select(row => row.Select(cell => Value(cell, language)).ToArray()));
            return localized;
        }).ToList();
    }

    // ============================ Разделы ============================

    private static InfoSection ComputerSection()
    {
        var section = new InfoSection("Компьютер и операционная система");

        var cs = Wmi.One("Win32_ComputerSystem");
        var os = Wmi.One("Win32_OperatingSystem");
        var enclosure = Wmi.One("Win32_SystemEnclosure");

        string model = $"{Wmi.Get(cs, "Manufacturer")} {Wmi.Get(cs, "Model")}".Trim();
        Add(section, "Компьютер", model.Length > 0 ? model : "—");
        Add(section, "Тип корпуса", ChassisType(Wmi.Get(enclosure, "ChassisTypes")));
        Add(section, "Операционная система", Wmi.Get(os, "Caption"));
        Add(section, "Версия", $"{Wmi.Get(os, "Version")} (сборка {Wmi.Get(os, "BuildNumber")})");
        Add(section, "Архитектура", Wmi.Get(os, "OSArchitecture"));
        Add(section, "Имя компьютера", Wmi.Get(os, "CSName"));
        Add(section, "Пользователь", Environment.UserName);
        Add(section, "Установлена", Wmi.Date(os, "InstallDate"));
        Add(section, "Последняя загрузка", Wmi.Date(os, "LastBootUpTime"));
        Add(section, "Время работы", Uptime());
        Add(section, "Файл подкачки", SwapInfo());
        Add(section, "Всего памяти", $"{Wmi.ToGb(Wmi.Long(cs, "TotalPhysicalMemory")):0.00} ГБ");
        Add(section, "Режим загрузки", Firmware());
        Add(section, "Безопасная загрузка", SecureBoot());
        return section;
    }

    private static InfoSection BoardSection()
    {
        var section = new InfoSection("Материнская плата и BIOS");
        var board = Wmi.One("Win32_BaseBoard");
        var bios = Wmi.One("Win32_BIOS");

        Add(section, "Производитель платы", Wmi.Get(board, "Manufacturer"));
        Add(section, "Модель платы", Wmi.Get(board, "Product"));
        Add(section, "Версия платы", Wmi.Get(board, "Version"));
        Add(section, "Серийный номер платы", Wmi.Get(board, "SerialNumber"));
        Add(section, "Производитель BIOS", Wmi.Get(bios, "Manufacturer"));
        Add(section, "Версия BIOS", Wmi.Get(bios, "SMBIOSBIOSVersion"));
        Add(section, "Дата BIOS", Wmi.Date(bios, "ReleaseDate"));
        Add(section, "Версия SMBIOS", $"{Wmi.Get(bios, "SMBIOSMajorVersion")}.{Wmi.Get(bios, "SMBIOSMinorVersion")}");
        return section;
    }

    private static InfoSection CpuSection(IReadOnlyList<SensorRow> rows)
    {
        var section = new InfoSection("Процессор");
        var cpu = Wmi.One("Win32_Processor", properties:
        [
            "Name", "Manufacturer", "SocketDesignation", "NumberOfCores",
            "NumberOfLogicalProcessors", "MaxClockSpeed", "L2CacheSize", "L3CacheSize",
            "AddressWidth", "ProcessorId", "VirtualizationFirmwareEnabled",
        ]);
        if (cpu is null)
        {
            Add(section, "Процессор", "не удалось получить данные");
            return section;
        }

        double baseClock = Wmi.Double(cpu, "MaxClockSpeed");
        Add(section, "Модель", Wmi.Get(cpu, "Name"));
        Add(section, "Производитель", Wmi.Get(cpu, "Manufacturer"));
        Add(section, "Сокет", Wmi.Get(cpu, "SocketDesignation"));
        Add(section, "Ядра / потоки", $"{Wmi.Get(cpu, "NumberOfCores")} / {Wmi.Get(cpu, "NumberOfLogicalProcessors")}");
        Add(section, "Базовая частота", $"{baseClock:0} МГц");
        Add(section, "Кэш L2 / L3", $"{Wmi.Double(cpu, "L2CacheSize") / 1024:0.#} МБ / {Wmi.Double(cpu, "L3CacheSize") / 1024:0.#} МБ");
        Add(section, "Разрядность", Wmi.Get(cpu, "AddressWidth") + " бит");
        Add(section, "Идентификатор", Wmi.Get(cpu, "ProcessorId"));
        Add(section, "Виртуализация", Wmi.Get(cpu, "VirtualizationFirmwareEnabled") == "True" ? "включена" : "выключена");

        var cpuRows = rows.Where(r => r.HardwareGroup == SummaryBuilder.Cpu && r.IsMeasurement).ToList();
        // Some firmware exposes clock sensors whose value is permanently zero.
        // Do not present such a placeholder as the current CPU frequency.
        double? maxClock = cpuRows.Where(r => r.Type == SensorType.Clock)
            .Select(r => r.DisplayValue)
            .Where(value => value > 0)
            .DefaultIfEmpty()
            .Max();
        if (maxClock == 0) maxClock = null;
        double? maxTemp = cpuRows.Where(r => r.Type == SensorType.Temperature).Select(r => r.DisplayValue).Max();
        double? packagePower = cpuRows
            .Where(r => r.Type == SensorType.Power && r.Sensor.Contains("Package", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.DisplayValue).FirstOrDefault();

        if (maxClock.HasValue)
        {
            Add(section, "Текущая частота (макс. по ядрам)", $"{maxClock.Value:0} МГц");
        }

        if (maxTemp.HasValue) Add(section, "Температура сейчас", $"{maxTemp.Value:0.0} °C");
        if (packagePower is > 0) Add(section, "Потребление пакета", $"{packagePower.Value:0.0} Вт");
        return section;
    }

    private static InfoSection MemorySection(IReadOnlyList<SensorRow> rows)
    {
        var section = new InfoSection("Оперативная память");
        var modules = Wmi.All("Win32_PhysicalMemory");
        var array = Wmi.One("Win32_PhysicalMemoryArray");

        section.Columns.AddRange(new[] { "Слот", "Модуль", "Объём", "SPD, МГц", "BIOS, МГц", "Тип", "Форм-фактор", "Напряжение" });

        int ratedMax = 0, configuredMax = 0;
        foreach (Dictionary<string, object?> module in modules)
        {
            double capacity = Wmi.ToGb(Wmi.Long(module, "Capacity"));
            int rated = (int)Wmi.Double(module, "Speed");
            int configured = (int)Wmi.Double(module, "ConfiguredClockSpeed");
            ratedMax = Math.Max(ratedMax, rated);
            configuredMax = Math.Max(configuredMax, configured);

            // В SMBIOS банк (канал) и разъём — разные поля: показываем оба,
            // иначе все модули выглядят как «DIMM 0».
            string bank = Wmi.Get(module, "BankLabel");
            string locator = Wmi.Get(module, "DeviceLocator");
            string slot = bank.Length > 0 && locator.Length > 0 ? $"{bank} · {locator}"
                : bank.Length > 0 ? bank
                : locator.Length > 0 ? locator
                : "неизвестный слот";

            section.Rows.Add(new[]
            {
                slot,
                (Wmi.Get(module, "Manufacturer") + " " + Wmi.Get(module, "PartNumber")).Trim(),
                $"{capacity:0.#} ГБ",
                rated > 0 ? $"{rated} МГц" : "—",
                configured > 0 ? $"{configured} МГц" : "—",
                MemoryType(Wmi.Get(module, "SMBIOSMemoryType")),
                FormFactor(Wmi.Get(module, "FormFactor")),
                Wmi.Double(module, "ConfiguredVoltage") > 0
                    ? $"{Wmi.Double(module, "ConfiguredVoltage") / 1000:0.000} В"
                    : "—",
            });
        }

        Add(section, "Всего установлено", $"{modules.Count} модул(я), {Wmi.ToGb(Wmi.Long(array, "MaxCapacityEx")):0} ГБ максимум по плате");
        Add(section, "Заявленная скорость модулей (SMBIOS)", ratedMax > 0 ? $"до {ratedMax} МГц" : "нет данных");
        Add(section, "Текущая частота (SMBIOS/BIOS)", configuredMax > 0 ? $"{configuredMax} МГц" : "нет данных");
        Add(section, "Сопоставление скоростей", configuredMax > 0 && ratedMax > 0
            ? $"BIOS сообщает {configuredMax} МГц; SPD — до {ratedMax} МГц. По этим данным нельзя надёжно определить профиль XMP/EXPO."
            : "недостаточно данных SMBIOS");
        Add(section, "Каналы", DetectChannels(modules));
        return section;
    }

    /// <summary>Тайминги SPD по каждому модулю памяти.</summary>
    private static InfoSection SpdTimingSection(IReadOnlyList<SensorRow> rows)
    {
        // Модули SPD в дереве датчиков — это отдельные устройства памяти;
        // «Total Memory» и «Virtual Memory» родительскими именами не считаются.
        var modules = rows
            .Where(r => r.HardwareGroup == SummaryBuilder.Ram && r.HardwareType == HardwareType.Memory)
            .Where(r => !r.OwnName.Contains("Total Memory", StringComparison.OrdinalIgnoreCase)
                        && !r.OwnName.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.OwnName)
            .ToList();

        var section = new InfoSection("Тайминги памяти (SPD)", "читаются напрямую с модулей через SMBus");

        if (modules.Count == 0)
        {
            section.Columns.Add("Тайминг");
            section.Rows.Add(new[] { "нет данных — нужны права администратора и поддержка SPD" });
            return section;
        }

        section.Columns.Add("Тайминг");
        foreach (var module in modules)
            section.Columns.Add(ShortModuleName(module.Key));

        // SPD отдаёт тайминги, напряжения и частоту модуля: собираем все строки,
        // которые есть хотя бы у одного модуля.
        var timingRows = modules
            .SelectMany(m => m.Where(r => r.Type is SensorType.Timing or SensorType.Voltage
                or SensorType.Frequency or SensorType.Data or SensorType.SmallData))
            .Where(r => !r.IsThreshold)
            .GroupBy(r => r.Sensor)
            .OrderBy(g => g.Key, StringComparer.CurrentCulture)
            .ToList();

        if (timingRows.Count == 0)
        {
            section.Rows.Add(new[] { "модули найдены, но тайминги не отдаются" });
            return section;
        }

        foreach (var timing in timingRows)
        {
            var line = new string[modules.Count + 1];
            line[0] = timing.Key;
            for (int i = 0; i < modules.Count; i++)
            {
                SensorRow? match = timing.FirstOrDefault(r => r.OwnName == modules[i].Key);
                line[i + 1] = match?.ValueText ?? "—";
            }
            section.Rows.Add(line);
        }

        return section;
    }

    private static InfoSection GpuSection(IReadOnlyList<SensorRow> rows)
    {
        var section = new InfoSection("Видеокарты");
        section.Columns.AddRange(new[] { "Устройство", "Драйвер", "Дата драйвера", "Видеопамять", "Разрешение" });
        IReadOnlyList<GpuAdapterInfo> dxgiAdapters = GpuEngine.EnumerateAdapters();

        foreach (Dictionary<string, object?> gpu in Wmi.All("Win32_VideoController"))
        {
            string gpuName = Wmi.Get(gpu, "Name");
            GpuAdapterInfo? dxgi = dxgiAdapters.FirstOrDefault(a =>
                string.Equals(a.Name, gpuName, StringComparison.OrdinalIgnoreCase)) ??
                dxgiAdapters.FirstOrDefault(a => gpuName.Contains(a.Name, StringComparison.OrdinalIgnoreCase) ||
                                                  a.Name.Contains(gpuName, StringComparison.OrdinalIgnoreCase));
            long dxgiBytes = dxgi?.DedicatedVideoMemoryBytes ?? 0;
            long wmiBytes = Wmi.Long(gpu, "AdapterRAM");
            long driverBytes = GpuDriverMemoryBytes(gpuName);
            string vram = dxgiBytes > 0
                ? $"{dxgiBytes / 1024.0 / 1024 / 1024:0.#} ГБ (DXGI)"
                : driverBytes > 0
                    ? $"{driverBytes / 1024.0 / 1024 / 1024:0.#} ГБ (драйвер)"
                // Win32_VideoController.AdapterRAM is a 32-bit field and can saturate
                // near 4 GiB; values near that ceiling are not reliable totals.
                : wmiBytes > 0 && wmiBytes < 4_000_000_000L
                    ? $"{Wmi.ToGb(wmiBytes):0.#} ГБ (WMI)"
                    : wmiBytes > 0
                        ? "≥4 ГБ (WMI ограничен; точный объём неизвестен без DXGI)"
                        : "объём выделенной памяти не сообщён";

            section.Rows.Add(new[]
            {
                gpuName,
                Wmi.Get(gpu, "DriverVersion"),
                Wmi.Date(gpu, "DriverDate"),
                vram,
                $"{Wmi.Get(gpu, "CurrentHorizontalResolution")}×{Wmi.Get(gpu, "CurrentVerticalResolution")}",
            });
        }

        var gpuRows = rows.Where(r => r.HardwareGroup == SummaryBuilder.Gpu && r.IsMeasurement).ToList();
        double? temp = gpuRows.Where(r => r.Type == SensorType.Temperature).Select(r => r.DisplayValue).Max();
        double? power = gpuRows.Where(r => r.Type == SensorType.Power).Select(r => r.DisplayValue).Max();
        if (temp.HasValue) Add(section, "Температура сейчас", $"{temp.Value:0.0} °C");
        if (power.HasValue) Add(section, "Потребление сейчас", $"{power.Value:0.0} Вт");
        return section;
    }

    private static long GpuDriverMemoryBytes(string gpuName)
    {
        const string classPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        try
        {
            using RegistryKey? adapters = Registry.LocalMachine.OpenSubKey(classPath);
            if (adapters is null) return 0;

            foreach (string subKeyName in adapters.GetSubKeyNames())
            {
                using RegistryKey? adapter = adapters.OpenSubKey(subKeyName);
                if (adapter is null || !string.Equals(adapter.GetValue("DriverDesc") as string, gpuName, StringComparison.OrdinalIgnoreCase))
                    continue;

                object? value = adapter.GetValue("HardwareInformation.qwMemorySize");
                return value is null ? 0 : Convert.ToInt64(value);
            }
        }
        catch
        {
            // Registry data is optional; retain DXGI/WMI fallback behavior.
        }

        return 0;
    }

    private static InfoSection StorageSection(
        IReadOnlyList<SensorRow> rows,
        string language,
        IReadOnlyList<StorageDisk> disks)
    {
        var section = new InfoSection("Накопители", "все физические диски с их томами");
        section.Columns.AddRange(new[] { "Модель", "Шина", "Объём", "Тома", "Состояние", "Температура", "Серийный номер" });

        foreach (StorageDisk disk in disks)
        {
            SensorRow? temperature = rows.FirstOrDefault(r => r.Type == SensorType.Temperature && r.IsMeasurement
                && r.HardwareGroup == SummaryBuilder.Storage && !r.IsThreshold && ModelMatches(r.Hardware, disk.Model));

            string volumes = disk.Volumes.Count == 0
                ? "нет томов"
                : string.Join(", ", disk.Volumes.Select(v =>
                    $"{v.Display} {LocalizationManager.FormatNumber(v.SizeGb, language)} ГБ " +
                    $"({LocalizationManager.FormatNumber(v.FreeGb, language)} ГБ свободно)"));

            section.Rows.Add(new[]
            {
                disk.Model,
                disk.BusType,
                $"{LocalizationManager.FormatNumber(disk.SizeGb, language)} ГБ" + (disk.IsSystem ? " (системный)" : string.Empty),
                volumes,
                disk.HealthStatus,
                temperature?.ValueText ?? "—",
                disk.SerialNumber,
            });
        }

        if (disks.Count == 0)
            section.Rows.Add(new[] { "накопители не найдены", "—", "—", "—", "—", "—", "—" });

        return section;
    }

    /// <summary>Логические тома всех накопителей с занятым и свободным местом.</summary>
    private static InfoSection VolumesSection(string language, IReadOnlyList<StorageDisk> disks)
    {
        var volumes = new InfoSection("Тома", "все логические диски системы");
        volumes.Columns.AddRange(new[] { "Том", "Накопитель", "Метка", "Файловая система", "Всего", "Свободно", "Занято" });

        foreach (StorageDisk disk in disks)
        {
            foreach (StorageVolume volume in disk.Volumes)
            {
                volumes.Rows.Add(new[]
                {
                    volume.Letter,
                    disk.Model,
                    volume.Label.Length > 0 ? volume.Label : "—",
                    volume.FileSystem.Length > 0 ? volume.FileSystem : "—",
                    $"{LocalizationManager.FormatNumber(volume.SizeGb, language)} ГБ",
                    $"{LocalizationManager.FormatNumber(volume.FreeGb, language)} ГБ",
                    $"{LocalizationManager.FormatNumber(volume.UsedPercent, language)} %",
                });
            }
        }

        // Тома, которые не привязались к физическим дискам, показываем отдельно.
        var shown = disks.SelectMany(d => d.Volumes).Select(v => v.Letter).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (Dictionary<string, object?> volume in Wmi.All("Win32_LogicalDisk", "DriveType=3"))
        {
            string letter = Wmi.Get(volume, "DeviceID");
            if (shown.Contains(letter)) continue;

            double sizeGb = Wmi.ToGb(Wmi.Long(volume, "Size"));
            double freeGb = Wmi.ToGb(Wmi.Long(volume, "FreeSpace"));
            volumes.Rows.Add(new[]
            {
                letter,
                "не определён",
                Wmi.Get(volume, "VolumeName"),
                Wmi.Get(volume, "FileSystem"),
                $"{sizeGb:0.#} ГБ",
                $"{freeGb:0.#} ГБ",
                $"{(sizeGb > 0 ? (sizeGb - freeGb) / sizeGb * 100 : 0):0.#} %",
            });
        }

        return volumes;
    }

    private static InfoSection NetworkSection()
    {
        var section = new InfoSection("Сеть");
        section.Columns.AddRange(new[] { "Адаптер", "MAC", "IPv4", "Шлюз", "DHCP" });

        foreach (Dictionary<string, object?> adapter in Wmi.All("Win32_NetworkAdapterConfiguration", "IPEnabled=True"))
        {
            section.Rows.Add(new[]
            {
                Wmi.Get(adapter, "Description"),
                Wmi.Get(adapter, "MACAddress"),
                Wmi.StringArray(adapter, "IPAddress"),
                Wmi.StringArray(adapter, "DefaultIPGateway"),
                Wmi.Get(adapter, "DHCPEnabled") == "True" ? "да" : "нет",
            });
        }

        return section;
    }

    // ============================ Вспомогательное ============================

    private static void Add(InfoSection section, string name, string value) =>
        section.Items.Add(new InfoItem(name, string.IsNullOrWhiteSpace(value) ? "—" : value));

    private static bool ModelMatches(string hardwareName, string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return false;
        string shortModel = model.Length > 12 ? model[..12] : model;
        return hardwareName.Contains(shortModel, StringComparison.OrdinalIgnoreCase);
    }

    private static string ShortModuleName(string hardware)
    {
        int dot = hardware.IndexOf(" · ", StringComparison.Ordinal);
        return dot > 0 ? hardware[(dot + 3)..] : hardware;
    }

    private static string DetectChannels(IReadOnlyList<Dictionary<string, object?>> modules)
    {
        if (modules.Count == 0) return "—";
        var banks = modules.Select(m => Wmi.Get(m, "BankLabel")).Distinct().Count();
        return banks > 1
            ? $"{banks} метки банков SMBIOS (число каналов памяти этим способом не подтверждается)"
            : "число каналов по SMBIOS не подтверждено";
    }

    private static string MemoryType(string raw) => raw switch
    {
        "20" => "DDR",
        "21" => "DDR2",
        "24" => "DDR3",
        "26" => "DDR4",
        "34" => "DDR5",
        _ => raw.Length > 0 && raw != "0" ? $"тип {raw}" : "—",
    };

    private static string FormFactor(string raw) => raw switch
    {
        "8" => "DIMM",
        "12" => "SODIMM",
        _ => "—",
    };

    private static string ChassisType(string raw)
    {
        string first = raw.Trim('[', ']').Split(',')[0].Trim();
        return first switch
        {
            "3" => "настольный",
            "6" => "мини-корпус",
            "7" => "тонкий корпус",
            "9" => "ноутбук",
            "10" => "ноутбук",
            "13" => "моноблок",
            _ => raw.Length > 0 ? $"тип {first}" : "—",
        };
    }

    private static string Uptime()
    {
        TimeSpan uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        return $"{(int)uptime.TotalDays} д {uptime.Hours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}";
    }

    private static string SwapInfo()
    {
        var page = Wmi.One("Win32_PageFileUsage");
        return page is null ? "не используется" : $"{Wmi.Get(page, "Name")} — {Wmi.ToGb(Wmi.Long(page, "AllocatedBaseSize") * 1024 * 1024):0.#} ГБ";
    }

    private static string Firmware()
    {
        try
        {
            if (GetFirmwareType(out int type))
                return type switch { 1 => "BIOS (legacy)", 2 => "UEFI", _ => "неизвестно" };
        }
        catch
        {
            // Игнорируем — ниже вернём значение по умолчанию.
        }

        return "—";
    }

    private static string SecureBoot()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            object? value = key?.GetValue("UEFISecureBootEnabled");
            return value is int state ? state == 1 ? "включена" : "выключена" : "—";
        }
        catch
        {
            return "—";
        }
    }
}

/// <summary>Мини-обёртка над WMI: устойчивые запросы и безопасное чтение свойств.</summary>
internal static class Wmi
{
    public static List<Dictionary<string, object?>> All(string className, string condition = "") =>
        All(className, condition, null, null);

    /// <summary>Запрос WMI с указанием пространства имён (например, root\Microsoft\Windows\Storage).</summary>
    public static List<Dictionary<string, object?>> All(string className, string condition, string? scope)
        => All(className, condition, scope, null);

    /// <summary>Запрос WMI с необязательным ограничением списка свойств.</summary>
    public static List<Dictionary<string, object?>> All(
        string className,
        string condition,
        string? scope,
        IReadOnlyCollection<string>? properties)
    {
        var result = new List<Dictionary<string, object?>>();
        string projection = properties is not { Count: > 0 } ? "*" : string.Join(", ", properties);
        if (properties is not null && properties.Any(property => !IsWmiIdentifier(property)))
            throw new ArgumentException("The WMI projection contains an invalid property name.", nameof(properties));
        string wql = $"SELECT {projection} FROM {className}" + (condition.Length > 0 ? " WHERE " + condition : string.Empty);

        try
        {
            using var searcher = scope is null
                ? new ManagementObjectSearcher(wql)
                : new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql));

            foreach (ManagementBaseObject item in searcher.Get())
            {
                var bag = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (PropertyData property in item.Properties)
                    bag[property.Name] = property.Value;

                result.Add(bag);
                item.Dispose();
            }
        }
        catch
        {
            // Некоторые классы WMI недоступны — возвращаем то, что удалось прочитать.
        }

        return result;
    }

    public static Dictionary<string, object?>? One(
        string className,
        string condition = "",
        IReadOnlyCollection<string>? properties = null) =>
        All(className, condition, null, properties).FirstOrDefault();

    private static bool IsWmiIdentifier(string property) =>
        property.Length > 0 &&
        (char.IsAsciiLetter(property[0]) || property[0] == '_') &&
        property.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    public static string Get(Dictionary<string, object?>? bag, string property)
    {
        if (bag is null || !bag.TryGetValue(property, out object? value) || value is null) return string.Empty;

        return value switch
        {
            string text => text.Trim(),
            string[] array => string.Join(", ", array.Where(s => !string.IsNullOrWhiteSpace(s))),
            Array array => string.Join(", ", array.Cast<object>().Select(o => o.ToString())),
            DateTime time => time.ToString("dd.MM.yyyy HH:mm"),
            _ => value.ToString()?.Trim() ?? string.Empty,
        };
    }

    public static string StringArray(Dictionary<string, object?>? bag, string property)
    {
        if (bag is null || !bag.TryGetValue(property, out object? value) || value is null) return "—";
        if (value is string[] array) return array.Length == 0 ? "—" : string.Join(", ", array);
        string text = value.ToString() ?? string.Empty;
        return text.Length == 0 ? "—" : text;
    }

    public static long Long(Dictionary<string, object?>? bag, string property) =>
        bag is not null && bag.TryGetValue(property, out object? value) && value is not null &&
        long.TryParse(value.ToString(), out long parsed) ? parsed : 0;

    public static double Double(Dictionary<string, object?>? bag, string property) =>
        bag is not null && bag.TryGetValue(property, out object? value) && value is not null &&
        double.TryParse(value.ToString(), out double parsed) ? parsed : 0;

    public static double ToGb(long bytes) => bytes / 1024.0 / 1024 / 1024;

    public static string Date(Dictionary<string, object?>? bag, string property)
    {
        if (bag is null || !bag.TryGetValue(property, out object? value) || value is null) return "—";
        try
        {
            return ManagementDateTimeConverter.ToDateTime(value.ToString()).ToString("dd.MM.yyyy");
        }
        catch
        {
            return value.ToString() ?? "—";
        }
    }
}
