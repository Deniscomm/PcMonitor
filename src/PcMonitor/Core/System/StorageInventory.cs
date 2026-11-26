namespace PcMonitor.Core;

/// <summary>Классификация физического накопителя; USB-флешка назначается только вручную.</summary>
public enum StorageDeviceKind { Auto, Nvme, Sata, Hdd, UsbFlash, Other }

/// <summary>Том (логический диск) на накопителе.</summary>
public sealed record StorageVolume(
    string Letter,
    string Label,
    string FileSystem,
    double SizeGb,
    double FreeGb)
{
    public string Display => Letter + (Label.Length > 0 ? $" ({Label})" : string.Empty);

    public double UsedPercent => SizeGb > 0 ? (SizeGb - FreeGb) / SizeGb * 100 : 0;
}

/// <summary>Физический накопитель со своими томами.</summary>
public sealed record StorageDisk(
    int Index,
    string Model,
    string BusType,
    double SizeGb,
    string SerialNumber,
    bool IsSystem,
    string HealthStatus,
    IReadOnlyList<StorageVolume> Volumes)
{
    public string Display => $"{Model} · {SizeGb:0.#} ГБ · {BusType}";

    public string StableId => !string.IsNullOrWhiteSpace(SerialNumber)
        ? "serial:" + SerialNumber.Trim()
        : $"disk:{Index}:{Model.Trim()}";

    public StorageDeviceKind DetectedKind
    {
        get
        {
            if (BusType.Contains("NVMe", StringComparison.OrdinalIgnoreCase)) return StorageDeviceKind.Nvme;
            if (BusType.Contains("USB", StringComparison.OrdinalIgnoreCase)) return StorageDeviceKind.Other;
            if (BusType.Contains("SATA", StringComparison.OrdinalIgnoreCase) ||
                BusType.Contains("ATA", StringComparison.OrdinalIgnoreCase))
            {
                if (Model.Contains("SSD", StringComparison.OrdinalIgnoreCase)) return StorageDeviceKind.Sata;
                return StorageDeviceKind.Hdd;
            }
            return StorageDeviceKind.Other;
        }
    }

    public StorageDeviceKind KindFor(IReadOnlyDictionary<string, string> overrides)
    {
        if (overrides.TryGetValue(StableId, out string? value) &&
            Enum.TryParse(value, true, out StorageDeviceKind kind) && kind != StorageDeviceKind.Auto)
            return kind;
        return DetectedKind;
    }

    /// <summary>Буква тома, доступного для тестов (предпочитаем не системный).</summary>
    public string? WritableVolumeLetter =>
        Volumes.Where(v => v.Letter.Length > 0)
            .OrderByDescending(v => v.FreeGb)
            .Select(v => v.Letter)
            .FirstOrDefault();
}

/// <summary>
/// Список всех накопителей и их томов: нужен и вкладке «Система»,
/// и авто-тесту (чтобы проверять каждый диск), и бенчмаркам.
/// </summary>
public static class StorageInventory
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";

    public static IReadOnlyList<StorageDisk> Collect()
    {
        List<StorageDisk> disks = CollectModern();
        return disks.Count > 0 ? disks : CollectLegacy();
    }

    // ============================ Современный путь (MSFT_*) ============================

    private static List<StorageDisk> CollectModern()
    {
        var result = new List<StorageDisk>();

        List<Dictionary<string, object?>> storageDisks = Wmi.All("MSFT_Disk", string.Empty, StorageNamespace);
        if (storageDisks.Count == 0) return result;

        List<Dictionary<string, object?>> partitions = Wmi.All("MSFT_Partition", string.Empty, StorageNamespace);
        List<Dictionary<string, object?>> volumes = Wmi.All("MSFT_Volume", string.Empty, StorageNamespace);

        foreach (Dictionary<string, object?> disk in storageDisks)
        {
            int number = (int)Wmi.Double(disk, "Number");
            string model = Wmi.Get(disk, "FriendlyName");
            if (model.Length == 0) model = "Накопитель " + number;

            var diskVolumes = new List<StorageVolume>();
            foreach (Dictionary<string, object?> partition in partitions)
            {
                if ((int)Wmi.Double(partition, "DiskNumber") != number) continue;

                string letter = NormalizeDriveLetter(Wmi.Get(partition, "DriveLetter"));
                if (letter.Length == 0) continue;   // служебные разделы без буквы не показываем

                Dictionary<string, object?>? volume = volumes.FirstOrDefault(v =>
                    NormalizeDriveLetter(Wmi.Get(v, "DriveLetter")) == letter);

                double sizeGb = Wmi.ToGb(Wmi.Long(volume, "Size"));
                double freeGb = Wmi.ToGb(Wmi.Long(volume, "SizeRemaining"));
                if (sizeGb <= 0) sizeGb = Wmi.ToGb(Wmi.Long(partition, "Size"));

                diskVolumes.Add(new StorageVolume(
                    letter + ":",
                    Wmi.Get(volume, "FileSystemLabel"),
                    Wmi.Get(volume, "FileSystem"),
                    sizeGb,
                    freeGb));
            }

            result.Add(new StorageDisk(
                number,
                model,
                BusTypeName(Wmi.Get(disk, "BusType")),
                Wmi.ToGb(Wmi.Long(disk, "Size")),
                Wmi.Get(disk, "SerialNumber"),
                Wmi.Double(disk, "IsSystem") > 0.5 || Wmi.Double(disk, "IsBoot") > 0.5,
                HealthName(Wmi.Get(disk, "HealthStatus")),
                diskVolumes.OrderBy(v => v.Letter, StringComparer.OrdinalIgnoreCase).ToList()));
        }

        return result;
    }

    // ============================ Запасной путь (Win32_*) ============================

    private static List<StorageDisk> CollectLegacy()
    {
        var result = new List<StorageDisk>();
        List<Dictionary<string, object?>> logical = Wmi.All("Win32_LogicalDisk", "DriveType=3");

        var volumes = logical.Select(volume => new StorageVolume(
            Wmi.Get(volume, "DeviceID"),
            Wmi.Get(volume, "VolumeName"),
            Wmi.Get(volume, "FileSystem"),
            Wmi.ToGb(Wmi.Long(volume, "Size")),
            Wmi.ToGb(Wmi.Long(volume, "FreeSpace")))).ToList();

        int index = 0;
        foreach (Dictionary<string, object?> disk in Wmi.All("Win32_DiskDrive"))
        {
            string model = Wmi.Get(disk, "Model");
            bool isSystem = model.Contains("NVMe", StringComparison.OrdinalIgnoreCase) &&
                            volumes.Any(v => v.Letter.StartsWith("C", StringComparison.OrdinalIgnoreCase));

            result.Add(new StorageDisk(
                index,
                model.Length > 0 ? model : "Накопитель " + index,
                Wmi.Get(disk, "InterfaceType"),
                Wmi.ToGb(Wmi.Long(disk, "Size")),
                Wmi.Get(disk, "SerialNumber"),
                isSystem,
                "неизвестно",
                Array.Empty<StorageVolume>()));

            index++;
        }

        // Тома, которые не удалось привязать к диску, добавляем к системному.
        if (result.Count > 0 && volumes.Count > 0 && result.All(d => d.Volumes.Count == 0))
        {
            StorageDisk first = result[0];
            result[0] = first with { Volumes = volumes };
        }

        return result;
    }

    // ============================ Вспомогательное ============================

    /// <summary>Свободная буква тома для файлов теста на выбранном диске.</summary>
    public static string? TestPathFor(StorageDisk disk, string testFolderName = "PcMonitorTest")
    {
        string? letter = disk.WritableVolumeLetter;
        return letter is null ? null : Path.Combine(letter + Path.DirectorySeparatorChar, testFolderName);
    }

    private static string BusTypeName(string raw) => raw switch
    {
        "17" => "NVMe",
        "11" => "SATA",
        "8" => "RAID",
        "7" => "USB",
        "10" => "SAS",
        "3" => "ATA",
        "0" => "неизвестно",
        _ => raw.Length > 0 ? $"шина {raw}" : "неизвестно",
    };

    private static string HealthName(string raw) => raw switch
    {
        "0" => "здоров",
        "1" => "внимание",
        "2" => "нездоров",
        "5" => "неизвестно",
        _ => raw.Length > 0 ? raw : "неизвестно",
    };

    private static string NormalizeDriveLetter(string value)
    {
        string letter = value.Trim();
        if (letter.Length == 2 && letter[1] == ':') letter = letter[..1];
        return letter.Length == 1 && char.IsAsciiLetter(letter[0])
            ? letter.ToUpperInvariant()
            : string.Empty;
    }
}
