using System.Globalization;
using LibreHardwareMonitor.Hardware;

namespace PcMonitor.Core.Testing;

/// <summary>
/// Правила оценки: превращает собранные метрики в понятные выводы
/// «хорошо», «обратить внимание», «проблема» с объяснением и советом.
/// </summary>
internal static class VerdictEngine
{
    public static void Evaluate(AutoTestReport report, IReadOnlyList<SensorRow> rows, AutoTestOptions options)
    {
        AutoTestPhase? idle = Phase(report, "Простой");
        AutoTestPhase? cpu = Phase(report, "Процессор");
        AutoTestPhase? gpu = Phase(report, "Видеокарта");
        AutoTestPhase? ram = Phase(report, "Память");
        AutoTestPhase? storage = Phase(report, "Накопители");
        AutoTestPhase? combined = Phase(report, "Совмещённая нагрузка");

        // Проверяем только то, что выбрал пользователь: если отмечена одна
        // комплектующая, выводов по остальным в отчёте не будет.
        HashSet<ComponentKind> selected = report.SelectedComponents;
        bool Want(ComponentKind kind) => selected.Count == 0 || selected.Contains(kind);

        if (Want(ComponentKind.Cpu)) EvaluateCpu(report, cpu, combined, idle);
        if (Want(ComponentKind.Gpu)) EvaluateGpu(report, gpu, combined);
        if (Want(ComponentKind.Ram)) EvaluateRam(report, ram, rows);
        if (report.DiskResults.Count > 0 || (Want(ComponentKind.StorageNvme) || Want(ComponentKind.StorageSata) || Want(ComponentKind.StorageHdd)))
            EvaluateStorage(report, storage, rows);
        if (Want(ComponentKind.Fans)) EvaluateCooling(report, combined, cpu, gpu);
        if (Want(ComponentKind.Motherboard)) EvaluateMotherboard(report, combined);
        if (Want(ComponentKind.Npu)) EvaluateNpu(report);
        if (Want(ComponentKind.Network)) EvaluateNetwork(report, combined);

        if (combined is not null && cpu is not null && Want(ComponentKind.Cpu) && Want(ComponentKind.Gpu))
        {
            double? isolated = cpu.MaxOf("Процессор: температура");
            double? together = combined.MaxOf("Процессор: температура");
            if (isolated.HasValue && together.HasValue && together.Value - isolated.Value > 8)
            {
                report.Verdicts.Add(new Verdict(VerdictLevel.Attention, "Охлаждение",
                    "В корпусе хуже продув, чем при изолированной нагрузке",
                    $"В нагрузке только на процессор максимум {isolated.Value:0.0} °C, " +
                    $"при одновременной нагрузке на процессор и видеокарту — {together.Value:0.0} °C.",
                    "Проверьте продув корпуса: корпусные вентиляторы, расположение кабелей, пыль на радиаторах."));
            }
        }

        report.Score = Math.Clamp(100 - report.AttentionCount * 12 - report.BadCount * 30, 0, 100);

        string scope = selected.Count == 0 || selected.Count == Enum.GetValues<ComponentKind>().Length
            ? "по всему компьютеру"
            : "по выбранным комплектующим";

        report.Summary = report.BadCount > 0
            ? $"Обнаружены проблемы: {report.BadCount} ({scope}). Замечаний: {report.AttentionCount}. Оценка {report.Score}/100."
            : report.AttentionCount > 0
                ? $"Критических проблем нет, но есть замечания: {report.AttentionCount} ({scope}). Оценка {report.Score}/100."
                : $"Всё в порядке: замечаний нет ({scope}). Оценка {report.Score}/100.";

        report.Verdicts.Add(new Verdict(VerdictLevel.Info, "Итог", "Общая оценка", report.Summary,
            report.BadCount > 0 ? "Разберите пункты со статусом «проблема»." : "Плановое обслуживание не требуется."));
    }

    // ============================ Процессор ============================

    private static void EvaluateCpu(AutoTestReport report, AutoTestPhase? cpu, AutoTestPhase? combined, AutoTestPhase? idle)
    {
        double? maximum = MaxOf(cpu, combined, "Процессор: температура");
        if (maximum is null)
        {
            report.Verdicts.Add(new Verdict(VerdictLevel.Info, "Процессор", "Температура недоступна",
                "Датчики процессора не отдали значения — вероятно, приложение запущено без прав администратора.",
                "Запустите приложение от имени администратора и повторите тест."));
            return;
        }

        VerdictLevel level = maximum.Value < 75 ? VerdictLevel.Good
            : maximum.Value < 88 ? VerdictLevel.Attention
            : VerdictLevel.Bad;

        string text = maximum.Value < 75
            ? $"Под нагрузкой максимум {maximum.Value:0.0} °C — отличный результат."
            : maximum.Value < 88
                ? $"Под нагрузкой максимум {maximum.Value:0.0} °C — допустимо, но близко к пределу."
                : $"Под нагрузкой максимум {maximum.Value:0.0} °C — это перегрев.";

        string advice = maximum.Value < 75
            ? "Ничего делать не нужно."
            : maximum.Value < 88
                ? "Проверьте термопасту, чистоту радиатора и кривую вентилятора."
                : "Срочно проверьте охлаждение: термопаста, крепление кулера, пыль, лимиты питания.";

        report.Verdicts.Add(new Verdict(level, "Процессор", "Температура под нагрузкой", text, advice));

        double? power = MaxOf(cpu, combined, "Процессор: мощность");
        double? load = MaxOf(cpu, combined, "Процессор: загрузка");
        if (load.HasValue && power.HasValue)
        {
            report.Verdicts.Add(new Verdict(VerdictLevel.Info, "Процессор", "Потребление",
                $"Максимальная загрузка {load.Value:0} %, потребление пакета до {power.Value:0.0} Вт.",
                "Сравнивайте с паспортным TDP: если потребление выше, проверьте лимиты в BIOS."));
        }

        double? idleClock = idle?.AverageOf("Процессор: частота");
        double? loadClock = cpu?.AverageOf("Процессор: частота");
        if (idleClock is > 0 && loadClock is > 0 && loadClock.Value < idleClock.Value * 0.92)
        {
            report.Verdicts.Add(new Verdict(VerdictLevel.Attention, "Процессор", "Возможен троттлинг",
                $"Под нагрузкой частота в среднем {loadClock.Value:0} МГц, в простое {idleClock.Value:0} МГц.",
                "Проверьте температуру, лимиты PPT/TDC/EDC и настройки питания Windows."));
        }
    }

    // ============================ Видеокарта ============================

    private static void EvaluateGpu(AutoTestReport report, AutoTestPhase? gpu, AutoTestPhase? combined)
    {
        double? temperature = MaxOf(gpu, combined, "Видеокарта: температура");
        if (temperature is null)
        {
            report.Verdicts.Add(new Verdict(VerdictLevel.Info, "Видеокарта", "Температура недоступна",
                "Датчики видеокарты не отдали значения.", "Проверьте драйвер видеокарты."));
            return;
        }

        VerdictLevel level = temperature.Value < 75 ? VerdictLevel.Good
            : temperature.Value < 83 ? VerdictLevel.Attention
            : VerdictLevel.Bad;

        report.Verdicts.Add(new Verdict(level, "Видеокарта", "Температура под нагрузкой",
            $"Максимум {temperature.Value:0.0} °C." +
            (level == VerdictLevel.Good ? " Это норма." : level == VerdictLevel.Attention ? " Тепловато." : " Это перегрев."),
            level == VerdictLevel.Good
                ? "Ничего делать не нужно."
                : "Проверьте пыль в радиаторе, термопрокладки и кривую вентиляторов."));

        double? hotspot = MaxOf(gpu, combined, "Видеокарта: hot spot");
        if (hotspot.HasValue)
        {
            double delta = hotspot.Value - temperature.Value;
            report.Verdicts.Add(new Verdict(delta > 20 ? VerdictLevel.Attention : VerdictLevel.Good,
                "Видеокарта", "Разница ядро / hot spot",
                $"Hot spot {hotspot.Value:0.0} °C при ядре {temperature.Value:0.0} °C — разница {delta:0.0} °C.",
                delta > 20
                    ? "Большая разница говорит о высохшей термопасте или плохом прижиме — стоит обслужить карту."
                    : "Разница в норме."));
        }

        double? power = MaxOf(gpu, combined, "Видеокарта: мощность");
        double? fan = MaxOf(gpu, combined, "Видеокарта: вентилятор");
        if (power.HasValue)
        {
            report.Verdicts.Add(new Verdict(VerdictLevel.Info, "Видеокарта", "Питание и вентиляторы",
                $"Потребление до {power.Value:0.0} Вт" + (fan.HasValue ? $", вентиляторы до {fan.Value:0} об/мин." : "."),
                "Сравните с паспортным TGP видеокарты."));
        }

        long errors = (gpu?.Errors ?? 0) + (combined?.Errors ?? 0);
        report.Verdicts.Add(new Verdict(errors == 0 ? VerdictLevel.Good : VerdictLevel.Bad,
            "Видеокарта", "Проверка стабильности",
            errors == 0
                ? "Артефактов и расхождений кадров не обнаружено."
                : $"Обнаружены расхождения изображения: {errors} пикселей.",
            errors == 0
                ? "Видеокарта стабильна."
                : "Снизьте разгон/undervolt, проверьте питание и охлаждение, повторите тест."));
    }

    // ============================ Память ============================

    private static void EvaluateRam(AutoTestReport report, AutoTestPhase? ram, IReadOnlyList<SensorRow> rows)
    {
        ComponentInfo? info = report.Components.FirstOrDefault(c => c.Kind == ComponentKind.Ram);

        if (report.MemoryPatterns.Count > 0)
        {
            long errors = report.MemoryPatterns.Sum(p => p.Errors);
            int clean = report.MemoryPatterns.Count(p => p.Errors == 0);
            string failed = string.Join(", ", report.MemoryPatterns.Where(p => p.Errors > 0).Select(p => p.Pattern));

            report.Verdicts.Add(new Verdict(errors == 0 ? VerdictLevel.Good : VerdictLevel.Bad,
                "Память", "Тест памяти по шаблонам",
                errors == 0
                    ? $"Проверено шаблонов: {report.MemoryPatterns.Count} (свой адрес, инверсии, " +
                      $"шахматные и случайные шаблоны, блочное копирование и другие) — расхождений нет."
                    : $"Из {report.MemoryPatterns.Count} шаблонов ошибки нашли: {failed}. Всего расхождений {errors}.",
                errors == 0
                    ? "Память стабильна на всех проверенных схемах."
                    : "Отключите XMP/DOCP, проверьте модули по одному, при необходимости снизьте частоту."));

            double average = report.MemoryPatterns.Average(p => p.Throughput);
            report.Verdicts.Add(new Verdict(VerdictLevel.Info, "Память", "Скорость проверки",
                $"Чистых шаблонов: {clean} из {report.MemoryPatterns.Count}, средняя пропускная способность {average:0.0}.",
                "Чем больше чистых шаблонов, тем надёжнее вывод о стабильности памяти."));
        }
        else if (ram is not null)
        {
            long errors = ram.Errors;
            report.Verdicts.Add(new Verdict(errors == 0 ? VerdictLevel.Good : VerdictLevel.Bad,
                "Память", "Тест памяти",
                errors == 0 ? $"{info?.Details ?? "Модули памяти"}. Ошибок не найдено." : $"Расхождений данных: {errors}.",
                errors == 0 ? "Проверка пройдена." : "Проверьте модули по одному и настройки XMP/DOCP."));
        }

        if (info is { Units: > 1 })
        {
            report.Verdicts.Add(new Verdict(VerdictLevel.Info, "Память", "Количество модулей",
                $"Установлено модулей: {info.Units}.",
                "Чтобы проверить конкретный модуль, оставьте его один и повторите тест памяти."));
        }
    }

    // ============================ Накопители ============================

    private static void EvaluateStorage(AutoTestReport report, AutoTestPhase? storage, IReadOnlyList<SensorRow> rows)
    {
        // По одному выводу на каждый проверенный диск: два SSD дают два результата.
        foreach (StorageDiskResult result in report.DiskResults)
        {
            double expectedRead = 500;
            VerdictLevel speedLevel = result.ReadMegabytesPerSecond >= expectedRead * 1.2 ? VerdictLevel.Good
                : result.ReadMegabytesPerSecond >= expectedRead * 0.6 ? VerdictLevel.Good
                : result.ReadMegabytesPerSecond > 0 ? VerdictLevel.Attention
                : VerdictLevel.Info;

            string speedText = result.ReadMegabytesPerSecond > 0
                ? result.SpeedsText
                : result.Note;

            report.Verdicts.Add(new Verdict(speedLevel, "Накопители", $"Скорости: {result.Disk}",
                speedText + (result.Volume != "—" ? $" (том {result.Volume})" : string.Empty),
                result.ReadMegabytesPerSecond >= expectedRead * 1.2
                    ? "Скорости соответствуют NVMe-накопителю."
                    : result.ReadMegabytesPerSecond >= expectedRead * 0.6
                        ? "Скорости соответствуют SATA-накопителю."
                        : "Проверьте, не заполнен ли диск и не мешает ли что-то обмену (драйверы чипсета, режим SATA)."));

            if (result.TemperatureCelsius.HasValue)
            {
                double temperature = result.TemperatureCelsius.Value;
                VerdictLevel level = temperature < 55 ? VerdictLevel.Good
                    : temperature < 70 ? VerdictLevel.Attention
                    : VerdictLevel.Bad;

                report.Verdicts.Add(new Verdict(level, "Накопители", $"Температура: {result.Disk}",
                    $"Максимум {temperature:0.0} °C.",
                    level == VerdictLevel.Good ? "Температура в норме."
                        : "Обеспечьте обдув: радиатор M.2 или корпусный вентилятор рядом."));
            }
        }

        List<Dictionary<string, object?>> disks = Wmi.All("MSFT_PhysicalDisk", string.Empty, @"root\Microsoft\Windows\Storage");
        List<Dictionary<string, object?>> counters = Wmi.All("MSFT_StorageReliabilityCounter", string.Empty, @"root\Microsoft\Windows\Storage");

        foreach (Dictionary<string, object?> disk in disks)
        {
            string name = Wmi.Get(disk, "FriendlyName");
            string health = Wmi.Get(disk, "HealthStatus");
            string media = Wmi.Get(disk, "MediaType");
            double wear = counters.Count > 0 ? counters.Max(c => Wmi.Double(c, "Wear")) : 0;
            double readErrors = counters.Count > 0 ? counters.Max(c => Wmi.Double(c, "ReadErrorsTotal")) : 0;
            double writeErrors = counters.Count > 0 ? counters.Max(c => Wmi.Double(c, "WriteErrorsTotal")) : 0;

            bool healthy = health.Length == 0 || health == "0" || health.Equals("Healthy", StringComparison.OrdinalIgnoreCase);
            bool errors = readErrors > 0 || writeErrors > 0;

            report.Verdicts.Add(new Verdict(!healthy || errors ? VerdictLevel.Bad : VerdictLevel.Good,
                "Накопители", $"Состояние: {name}",
                $"Тип носителя {MediaText(media)}, состояние {(healthy ? "в норме" : health)}, " +
                $"износ {wear:0} %, ошибок чтения {readErrors:0}, записи {writeErrors:0}.",
                !healthy || errors
                    ? "Сделайте резервную копию важных данных и проверьте диск утилитой производителя."
                    : "Диск здоров; полезно периодически наблюдать за износом."));
        }
    }

    // ============================ Охлаждение ============================

    private static void EvaluateCooling(AutoTestReport report, AutoTestPhase? combined, AutoTestPhase? cpu, AutoTestPhase? gpu)
    {
        // Если снималась реакция каждого вентилятора — выводим по каждому отдельно.
        if (report.FanResponses.Count > 0)
        {
            List<FanResponse> spinning = report.FanResponses.Where(f => f.MaximumRpm > 30).ToList();
            List<FanResponse> idleFans = report.FanResponses.Where(f => f.MaximumRpm <= 30).ToList();
            int responding = spinning.Count(f => f.Responds);

            // Проблемой считаем только ситуацию, когда вентилятор вращался и остановился.
            bool stoppedSuddenly = spinning.Any(f => f.IdleRpm > 30 && f.LoadRpm <= 30);

            report.Verdicts.Add(new Verdict(stoppedSuddenly ? VerdictLevel.Bad : VerdictLevel.Good,
                "Вентиляторы", "Реакция на нагрузку",
                $"Датчиков оборотов: {report.FanResponses.Count}, вращаются: {spinning.Count}, " +
                $"реагируют на нагрузку: {responding}" +
                (idleFans.Count > 0 ? $", не вращаются: {idleFans.Count} (нерабочие разъёмы и режим остановки видеокарты)" : string.Empty) + ".",
                stoppedSuddenly
                    ? "Вентилятор остановился под нагрузкой — проверьте подключение и кривые в BIOS/FanControl."
                    : spinning.Count == 0
                        ? "Ни один вентилятор не вращается — проверьте подключение к плате."
                        : "Кривые вентиляторов работают; часть разъёмов свободна или вентиляторы видеокарты стоят при низкой температуре."));

            foreach (FanResponse fan in report.FanResponses)
            {
                VerdictLevel level = !fan.Applicable ? VerdictLevel.Info
                    : fan.Responds ? VerdictLevel.Good
                    : VerdictLevel.Info;

                report.Verdicts.Add(new Verdict(level, "Вентиляторы", fan.Fan,
                    $"В простое {fan.IdleRpm:0} об/мин, под нагрузкой — {fan.LoadRpm:0} об/мин " +
                    $"(максимум за тест {fan.MaximumRpm:0}). Группа: {fan.Group}.",
                    fan.Note));
            }

            return;
        }

        double? maxFan = MaxOf(combined, cpu, gpu, "Вентиляторы: максимум");
        if (maxFan is null)
        {
            report.Verdicts.Add(new Verdict(VerdictLevel.Info, "Вентиляторы", "Обороты недоступны",
                "Датчики оборотов не отдали значения.", "Проверьте подключение вентиляторов к плате и права администратора."));
            return;
        }

        bool fanSpinning = maxFan.Value > 300;
        report.Verdicts.Add(new Verdict(fanSpinning ? VerdictLevel.Good : VerdictLevel.Attention,
            "Вентиляторы", "Работа под нагрузкой",
            $"Максимальные обороты {maxFan.Value:0} об/мин.",
            fanSpinning
                ? "Вентиляторы реагируют на нагрузку."
                : "Под нагрузкой обороты почти не выросли — проверьте кривые вентиляторов в BIOS/FanControl."));
    }

    // ============================ Мат. плата ============================

    private static void EvaluateMotherboard(AutoTestReport report, AutoTestPhase? combined)
    {
        double? v12 = combined?.MaxOf("Плата: +12 В");
        double? v5 = combined?.MaxOf("Плата: +5 В");
        double? v33 = combined?.MaxOf("Плата: +3.3 В");

        var details = new List<string>();
        var problems = new List<string>();

        Check(v12, 11.4, 12.6, "+12 В");
        Check(v5, 4.75, 5.25, "+5 В");
        Check(v33, 3.14, 3.47, "+3,3 В");

        void Check(double? value, double min, double max, string name)
        {
            if (!value.HasValue) return;
            details.Add($"{name}: {value.Value:0.00} В");
            if (value.Value < min || value.Value > max) problems.Add(name);
        }

        if (details.Count > 0)
        {
            report.Verdicts.Add(new Verdict(problems.Count == 0 ? VerdictLevel.Good : VerdictLevel.Attention,
                "Мат. плата", "Напряжения питания",
                string.Join(", ", details) + (problems.Count > 0 ? $". Отклонение: {string.Join(", ", problems)}." : ". Отклонений нет."),
                problems.Count == 0
                    ? "Блок питания выдаёт корректные напряжения."
                    : "Проверьте блок питания и контакты разъёмов 24-pin / EPS."));
        }

        double? boardTemp = combined?.MaxOf("Плата: температура");
        if (boardTemp.HasValue)
        {
            VerdictLevel level = boardTemp.Value < 60 ? VerdictLevel.Good
                : boardTemp.Value < 75 ? VerdictLevel.Attention
                : VerdictLevel.Bad;

            report.Verdicts.Add(new Verdict(level, "Мат. плата", "Температура платы",
                $"Максимум {boardTemp.Value:0.0} °C.",
                level == VerdictLevel.Good ? "В норме." : "Проверьте продув зоны VRM и радиаторов чипсета."));
        }

        Dictionary<string, object?>? bios = Wmi.One("Win32_BIOS");
        string biosDate = Wmi.Date(bios, "ReleaseDate");
        if (DateTime.TryParseExact(biosDate, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date))
        {
            double years = (DateTime.Now - date).TotalDays / 365.25;
            report.Verdicts.Add(new Verdict(years > 3 ? VerdictLevel.Attention : VerdictLevel.Good,
                "Мат. плата", "Версия BIOS",
                $"BIOS {Wmi.Get(bios, "SMBIOSBIOSVersion")} от {biosDate} ({years:0.#} года назад).",
                years > 3
                    ? "Обновление BIOS может улучшить совместимость памяти и стабильность; обновляйтесь осторожно."
                    : "Обновление не требуется."));
        }
    }

    private static void EvaluateNpu(AutoTestReport report)
    {
        ComponentInfo? npu = report.Components.FirstOrDefault(c => c.Kind == ComponentKind.Npu);
        report.Verdicts.Add(npu is { Present: true }
            ? new Verdict(VerdictLevel.Good, "NPU", "Нейромодуль обнаружен", npu.Details,
                "Тест матричных вычислений выполнен; для ускорения ИИ-задач используйте драйверы производителя.")
            : new Verdict(VerdictLevel.Info, "NPU", "Нейромодуль отсутствует",
                "В этой системе NPU не найден (это нормально для настольных процессоров без ИИ-блока).",
                "Ничего делать не нужно."));
    }

    private static void EvaluateNetwork(AutoTestReport report, AutoTestPhase? combined)
    {
        double? download = combined?.MaxOf("Сеть: приём");
        double? upload = combined?.MaxOf("Сеть: отдача");
        if (download is null && upload is null) return;

        report.Verdicts.Add(new Verdict(VerdictLevel.Info, "Сеть", "Активность",
            $"Приём до {download.GetValueOrDefault():0} КБ/с, отдача до {upload.GetValueOrDefault():0} КБ/с.",
            "Для проверки скорости интернета используйте тест провайдера: занятость канала не влияет на железо."));
    }

    // ============================ Вспомогательное ============================

    private static AutoTestPhase? Phase(AutoTestReport report, string name) =>
        report.Phases.FirstOrDefault(p => p.Name == name);

    private static double? MaxOf(AutoTestPhase? phase, string metric) =>
        phase?.MaxOf(metric);

    private static double? MaxOf(AutoTestPhase? first, AutoTestPhase? second, string metric)
    {
        double? a = first?.MaxOf(metric);
        double? b = second?.MaxOf(metric);
        if (a is null) return b;
        if (b is null) return a;
        return Math.Max(a.Value, b.Value);
    }

    private static double? MaxOf(AutoTestPhase? a, AutoTestPhase? b, AutoTestPhase? c, string metric) =>
        MaxOf(a, b, metric) is { } first && c?.MaxOf(metric) is { } third
            ? Math.Max(first, third)
            : MaxOf(a, b, metric) ?? c?.MaxOf(metric);

    private static double? Max(IReadOnlyList<SensorRow> rows, string group, SensorType type)
    {
        double? max = null;
        foreach (SensorRow row in rows)
        {
            if (!row.IsMeasurement || row.Type != type || row.HardwareGroup != group || !row.DisplayValue.HasValue) continue;
            if (max is null || row.DisplayValue.Value > max) max = row.DisplayValue.Value;
        }

        return max;
    }

    private static string MediaText(string raw) => raw switch
    {
        "3" => "HDD",
        "4" => "SSD",
        "5" => "SCM",
        _ => "неизвестно",
    };
}
