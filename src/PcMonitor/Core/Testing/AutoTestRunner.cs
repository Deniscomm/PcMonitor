using System.Diagnostics;
using LibreHardwareMonitor.Hardware;
using PcMonitor.Core.Stress;

namespace PcMonitor.Core.Testing;

/// <summary>Текущее состояние авто-теста для интерфейса.</summary>
public sealed record AutoTestProgress(string Phase, int PhaseIndex, int PhaseCount, double Percent, string Status);

/// <summary>
/// Авто-тест компьютера: инвентаризация, замер в простое, нагрузочные фазы
/// (процессор, память, видеокарта, накопители, комбинированная нагрузка),
/// сбор мин/сред/макс по датчикам и вывод вердиктов.
/// </summary>
public sealed class AutoTestRunner
{
    private readonly MonitorContext _context;
    private readonly AutoTestOptions _options;
    private AutoTestReport _report = new();
    private AutoTestPhase? _active;

    public AutoTestRunner(MonitorContext context, AutoTestOptions options)
    {
        _context = context;
        _options = options;
    }

    /// <summary>Внешняя нагрузка на видеокарту (реализуется интерфейсом или Direct3D-движком).</summary>
    public Func<TimeSpan, CancellationToken, (long Errors, string Details)>? GpuWorkload { get; set; }

    /// <summary>Сообщения для журнала интерфейса.</summary>
    public event Action<string>? LogLine;

    /// <summary>Фаза завершена (для отображения в интерфейсе).</summary>
    public event Action<AutoTestPhase>? PhaseCompleted;

    public async Task<AutoTestReport> RunAsync(IProgress<AutoTestProgress>? progress, CancellationToken token)
    {
        _report = new AutoTestReport
        {
            Started = DateTime.Now,
            Operator = Environment.UserName,
            Elevated = _context.Service.IsElevated,
            Machine = $"{Environment.MachineName} · {Environment.OSVersion.VersionString}",
            SelectedComponents = _options.Components,
        };

        _context.Updated += OnSensorsUpdated;
        var total = Stopwatch.StartNew();

        try
        {
            Log("Авто-тест запущен.");

            // 1. Инвентаризация
            var inventory = new AutoTestPhase("Инвентаризация", "Определение состава комплектующих");
            _active = inventory;
            Report(inventory, "Собираю сведения о железе…");
            List<ComponentInfo> components = await Task.Run(() => ComponentInventory.Collect(_context.Rows.ToList()), token);
            _report.Components.AddRange(components);
            foreach (ComponentInfo component in components)
            {
                Report(inventory, $"{component.Name}: {component.Details}" + (component.Present ? string.Empty : " (не обнаружено)"));
            }

            inventory.Duration = TimeSpan.Zero;
            Finish(inventory);
            progress?.Report(new AutoTestProgress(inventory.Name, 1, 1, 0.05, "состав определён"));

            var phases = BuildPhasePlan();
            int index = 0;
            foreach ((string name, string description, Func<CancellationToken, Task<string>> workload) in phases)
            {
                if (token.IsCancellationRequested) break;

                var phase = new AutoTestPhase(name, description);
                _active = phase;
                double phaseStart = (double)index / phases.Count;
                double phaseSpan = 1.0 / phases.Count;

                Report(phase, $"Фаза «{name}»: {description}");
                progress?.Report(new AutoTestProgress(name, index + 1, phases.Count, phaseStart,
                    $"выполняется: {description}"));

                var watch = Stopwatch.StartNew();
                string notes = string.Empty;
                try
                {
                    notes = await workload(token);
                }
                catch (OperationCanceledException)
                {
                    notes = "фаза прервана";
                }
                catch (Exception ex)
                {
                    notes = "ошибка фазы: " + ex.Message;
                    phase.Errors++;
                }

                watch.Stop();
                phase.Duration = watch.Elapsed;
                phase.Notes = notes;
                Finish(phase);

                index++;
                progress?.Report(new AutoTestProgress(name, index, phases.Count, phaseStart + phaseSpan,
                    "готово"));
            }

            _active = null;
            total.Stop();
            _report.Duration = total.Elapsed;

            Log("Формирую выводы…");
            VerdictEngine.Evaluate(_report, _context.Rows.ToList(), _options);
            Log($"Итог: {_report.Summary}");

            if (_options.SaveReport)
            {
                try
                {
                    string path = HtmlReportWriter.Save(_report);
                    Log("Отчёт сохранён: " + path);
                }
                catch (Exception ex)
                {
                    Log("Не удалось сохранить отчёт: " + ex.Message);
                }
            }

            return _report;
        }
        finally
        {
            _active = null;
            _context.Updated -= OnSensorsUpdated;
        }
    }

    // ============================ План фаз ============================

    private List<(string Name, string Description, Func<CancellationToken, Task<string>> Workload)> BuildPhasePlan()
    {
        var plan = new List<(string, string, Func<CancellationToken, Task<string>>)>();

        plan.Add(("Простой", "базовые температуры и обороты без нагрузки",
            async ct =>
            {
                await Task.Delay(_options.IdleDuration, ct);
                return $"замер в простое {_options.IdleDuration.TotalSeconds:0} с";
            }));

        if (_options.Has(ComponentKind.Cpu))
        {
            plan.Add(("Процессор", "максимальная вычислительная нагрузка на все ядра",
                async ct =>
                {
                    CpuRenderResult result = await Task.Run(
                        () => CpuRenderBenchmark.RunMulti(_options.PhaseDuration, null, null, ct), ct);
                    _report.Log.Add($"Рендер-бенчмарк (все ядра): балл {result.ScoreText}, {result.Details}");
                    return $"рендер: балл {result.ScoreText} ({result.Details})";
                }));
        }

        if (_options.Has(ComponentKind.Ram))
        {
            plan.Add(("Память", "полный набор шаблонов проверки памяти с поиском ошибок данных",
                async ct =>
                {
                    List<IStressTest> patterns = StressTestCatalog.ByCategory(StressCategory.Memory).ToList();
                    if (patterns.Count == 0) return "тесты памяти недоступны";

                    var options = new StressOptions
                    {
                        MemoryMegabytes = _options.MemoryMegabytes,
                        DiskPath = _options.DiskPath,
                        DiskBytes = (long)_options.DiskMegabytes * 1024 * 1024,
                        Seed = Environment.TickCount,
                    };

                    var runner = new StressTestRunner();
                    long totalErrors = 0;
                    var notes = new List<string>();

                    foreach (IStressTest test in patterns)
                    {
                        if (ct.IsCancellationRequested) break;

                        // Каждый шаблон идёт своим ходом до истечения отведённого времени,
                        // поэтому проверяются все схемы, а не одна (как было раньше).
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(3, _options.MemorySecondsPerPattern)));

                        StressTestResult result = await Task.Run(
                            () => runner.RunAsync(test, options, null, cts.Token), CancellationToken.None);

                        totalErrors += result.TotalErrors;
                        _report.MemoryPatterns.Add(new MemoryPatternResult(
                            test.Name, result.TotalErrors, result.AverageThroughput, result.Duration,
                            result.TotalErrors == 0 ? "чисто" : "расхождения"));

                        Report(_active!, $"{test.Name}: ошибок {result.TotalErrors}, " +
                                          $"скорость {result.AverageThroughput:0.0}");
                    }

                    notes.Add($"шаблонов {_report.MemoryPatterns.Count}");
                    AutoTestPhase? phase = _active;
                    if (phase is not null) phase.Errors = totalErrors;

                    return $"проверено шаблонов: {_report.MemoryPatterns.Count}, " +
                           $"всего ошибок {totalErrors} ({string.Join(", ", notes)})";
                }));
        }

        if (_options.Has(ComponentKind.Gpu))
        {
            plan.Add(("Видеокарта", "графическая нагрузка с проверкой артефактов",
                async ct =>
                {
                    if (GpuWorkload is null) return "GPU-нагрузка недоступна";

                    (long errors, string details) = await Task.Run(
                        () => GpuWorkload(_options.PhaseDuration, ct), ct);

                    AutoTestPhase? phase = _active;
                    if (phase is not null) phase.Errors += errors;
                    return details;
                }));
        }

        if (_options.HasAnyStorage)
        {
            plan.Add(("Накопители", "каждый диск отдельно: последовательные и произвольные операции",
                async ct =>
                {
                    List<StorageDisk> disks = StorageInventory.Collect().ToList();
                    if (_options.RestrictStorageToSelectedDisks)
                        disks = disks.Where(d => _options.StorageDiskIndexes.Contains(d.Index)).ToList();
                    disks = disks.Where(d => d.KindFor(_options.StorageTypeOverrides) switch
                    {
                        StorageDeviceKind.Nvme => _options.Has(ComponentKind.StorageNvme),
                        StorageDeviceKind.Sata => _options.Has(ComponentKind.StorageSata),
                        StorageDeviceKind.Hdd => _options.Has(ComponentKind.StorageHdd),
                        StorageDeviceKind.UsbFlash => _options.Has(ComponentKind.StorageUsb),
                        _ => _options.Has(ComponentKind.StorageOther),
                    }).ToList();

                    if (disks.Count == 0) return "нет накопителей, подходящих под выбранные типы и диски";

                    int megabytes = Math.Clamp(_options.DiskMegabytes, 32, 4096);
                    var summary = new List<string>();

                    foreach (StorageDisk disk in disks)
                    {
                        if (ct.IsCancellationRequested) break;

                        StorageDeviceKind kind = disk.KindFor(_options.StorageTypeOverrides);
                        string deviceLabel = kind switch
                        {
                            StorageDeviceKind.Nvme => "NVMe SSD",
                            StorageDeviceKind.Sata => "SATA SSD",
                            StorageDeviceKind.Hdd => "HDD",
                            StorageDeviceKind.UsbFlash => "USB-флешка",
                            _ => "прочее",
                        };

                        string? folder = StorageInventory.TestPathFor(disk);
                        if (folder is null)
                        {
                            _report.DiskResults.Add(new StorageDiskResult($"{disk.Model} [{deviceLabel}]", "—", 0, 0, 0, 0, null, 0,
                                "нет доступного тома для теста"));
                            continue;
                        }

                        string file = BenchmarkSuite.CreateTempFile(folder, "autotest");
                        var notes = new List<string>();
                        double write = 0, read = 0, randomRead = 0, randomWrite = 0;
                        long errors = 0;

                        try
                        {
                            BenchmarkResult writeResult = await Task.Run(
                                () => BenchmarkSuite.DiskSequentialWrite(file, megabytes, ct), ct);
                            write = writeResult.Value;
                            notes.Add($"запись {writeResult.ValueText} {writeResult.Unit}");

                            BenchmarkResult readResult = await Task.Run(
                                () => BenchmarkSuite.DiskSequentialRead(file, megabytes, ct), ct);
                            read = readResult.Value;
                            notes.Add($"чтение {readResult.ValueText} {readResult.Unit}");

                            BenchmarkResult randomReadResult = await Task.Run(
                                () => BenchmarkSuite.DiskRandom(file, write: false, operations: 2000, ct), ct);
                            randomRead = randomReadResult.Value;
                            notes.Add($"4К чтение {randomReadResult.ValueText} {randomReadResult.Unit}");

                            BenchmarkResult randomWriteResult = await Task.Run(
                                () => BenchmarkSuite.DiskRandom(file, write: true, operations: 1000, ct), ct);
                            randomWrite = randomWriteResult.Value;
                            notes.Add($"4К запись {randomWriteResult.ValueText} {randomWriteResult.Unit}");
                        }
                        catch (Exception ex)
                        {
                            errors = 1;
                            notes.Add("ошибка теста: " + ex.Message);
                        }
                        finally
                        {
                            try
                            {
                                if (File.Exists(file)) File.Delete(file);
                            }
                            catch
                            {
                                // Временный файл мог остаться — не критично.
                            }
                        }

                        double? temperature = TemperatureFor(disk);
                        _report.DiskResults.Add(new StorageDiskResult($"{disk.Model} [{deviceLabel}]",
                            disk.WritableVolumeLetter ?? "—", write, read, randomRead, randomWrite,
                            temperature, errors, string.Join(", ", notes)));

                        summary.Add($"{disk.Model}: {string.Join(", ", notes)}");
                        Report(_active!, $"{disk.Model}: {string.Join(", ", notes)}");

                        AutoTestPhase? phase = _active;
                        if (phase is not null) phase.Errors += errors;
                    }

                    return string.Join(" | ", summary);
                }));
        }

        if (_options.IncludeCombinedLoad && _options.Has(ComponentKind.Cpu) && _options.Has(ComponentKind.Gpu))
        {
            plan.Add(("Совмещённая нагрузка", "процессор и видеокарта одновременно — самый тяжёлый тепловой режим",
                async ct =>
                {
                    Task<string> cpuTask = Task.Run(() =>
                    {
                        CpuRenderResult result = CpuRenderBenchmark.RunMulti(_options.PhaseDuration, null, null, ct);
                        return $"рендер: балл {result.ScoreText}";
                    }, ct);

                    Task<string> gpuTask = Task.Run(() =>
                    {
                        if (GpuWorkload is null) return "GPU-нагрузка недоступна";
                        (long errors, string details) = GpuWorkload(_options.PhaseDuration, ct);
                        AutoTestPhase? phase = _active;
                        if (phase is not null) phase.Errors += errors;
                        return details;
                    }, ct);

                    await Task.WhenAll(cpuTask, gpuTask);
                    return $"{cpuTask.Result}; {gpuTask.Result}";
                }));
        }

        if (_options.Has(ComponentKind.Npu))
        {
            plan.Add(("Нейромодуль (NPU)", "проверка наличия и простая вычислительная нагрузка",
                async ct =>
                {
                    bool present = _report.Components.Any(c => c.Kind == ComponentKind.Npu && c.Present);
                    if (!present) return "нейромодуль в системе не обнаружен — проверка пропущена";

                    double gflops = await Task.Run(() => NpuBenchmark.Run(TimeSpan.FromSeconds(10), ct), ct);
                    return $"матричные вычисления: {gflops:0.0} Гфлоп/с";
                }));
        }

        if (_options.Has(ComponentKind.Fans))
        {
            plan.Add(("Вентиляторы", "ступени нагрузки: реагируют ли обороты",
                async ct =>
                {
                    Dictionary<string, double> idle = FanSnapshot();
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(8, _options.FanLoadSeconds)));

                    Task<string> load = Task.Run(() =>
                    {
                        CpuRenderResult result = CpuRenderBenchmark.RunMulti(
                            TimeSpan.FromSeconds(Math.Max(8, _options.FanLoadSeconds)), null, null, cts.Token);
                        return result.ScoreText;
                    }, cts.Token);

                    var underLoad = new Dictionary<string, double>(StringComparer.Ordinal);
                    while (!load.IsCompleted && !ct.IsCancellationRequested)
                    {
                        foreach ((string fan, double rpm) in FanSnapshot())
                        {
                            underLoad[fan] = underLoad.TryGetValue(fan, out double current)
                                ? Math.Max(current, rpm)
                                : rpm;
                        }

                        await Task.Delay(1000, CancellationToken.None);
                    }

                    try
                    {
                        await load;
                    }
                    catch (OperationCanceledException)
                    {
                        // Нагрузка прервана — используем то, что успели собрать.
                    }

                    var fans = idle.Keys.Union(underLoad.Keys, StringComparer.Ordinal).ToList();
                    int responding = 0;
                    int applicable = 0;

                    foreach (string fan in fans)
                    {
                        double idleRpm = idle.TryGetValue(fan, out double i) ? i : 0;
                        double loadRpm = underLoad.TryGetValue(fan, out double l) ? l : 0;
                        double maximum = Math.Max(idleRpm, loadRpm);

                        bool spins = maximum > 30;
                        bool gpuFan = fan.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
                                      fan.Contains("Видеокарта", StringComparison.OrdinalIgnoreCase);
                        bool responds = loadRpm > Math.Max(30, idleRpm * 1.05) || (idleRpm <= 1 && loadRpm > 30);

                        // Неподключённые разъёмы и остановленные вентиляторы видеокарты
                        // (режим «нулевых оборотов») — это не неисправность.
                        bool expected = spins || !gpuFan;
                        if (responds) responding++;
                        if (expected) applicable++;

                        string note = !spins
                            ? gpuFan
                                ? "вентиляторы видеокарты стоят: это режим остановки при низкой температуре"
                                : "вентилятор не вращается — вероятно, к этому разъёму ничего не подключено"
                            : responds
                                ? $"обороты выросли с {idleRpm:0} до {loadRpm:0} об/мин"
                                : $"обороты почти не изменились ({idleRpm:0} → {loadRpm:0} об/мин)";

                        _report.FanResponses.Add(new FanResponse(fan, gpuFan ? "Видеокарта" : "Плата",
                            idleRpm, loadRpm, maximum, responds, expected, note));
                        Report(_active!, $"{fan}: {note}");
                    }

                    return fans.Count == 0
                        ? "датчики оборотов не отдали значения"
                        : $"вентиляторов {fans.Count}, вращаются {applicable}, реагируют на нагрузку {responding}";
                }));
        }

        plan.Add(("Остывание", "возврат температур к исходным значениям",
            async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, _options.IdleDuration.TotalSeconds / 2)), ct);
                return "контрольный замер после нагрузки";
            }));

        return plan;
    }

    // ============================ Сбор метрик ============================

    /// <summary>Текущие обороты всех вентиляторов: имя датчика — обороты.</summary>
    private Dictionary<string, double> FanSnapshot()
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (SensorRow row in _context.Rows)
        {
            if (row.Type != SensorType.Fan || !row.DisplayValue.HasValue) continue;

            string name = $"{row.Hardware} · {row.DisplayName}";
            result[name] = result.TryGetValue(name, out double current)
                ? Math.Max(current, row.DisplayValue.Value)
                : row.DisplayValue.Value;
        }

        return result;
    }

    /// <summary>Температура накопителя по модели диска.</summary>
    private double? TemperatureFor(StorageDisk disk)
    {
        string shortModel = disk.Model.Length > 10 ? disk.Model[..10] : disk.Model;

        foreach (SensorRow row in _context.Rows)
        {
            if (row.Type != SensorType.Temperature || !row.IsMeasurement || !row.DisplayValue.HasValue) continue;
            if (row.HardwareGroup != SummaryBuilder.Storage) continue;
            if (row.Hardware.Contains(shortModel, StringComparison.OrdinalIgnoreCase))
                return row.DisplayValue.Value;
        }

        return null;
    }

    private void OnSensorsUpdated()
    {
        if (_active is null) return;
        SampleInto(_active, _context.Rows.ToList());
    }

    private static void SampleInto(AutoTestPhase phase, IReadOnlyList<SensorRow> rows)
    {
        Add(phase, "Процессор: температура", "°C", Max(rows, SummaryBuilder.Cpu, SensorType.Temperature));
        Add(phase, "Процессор: загрузка", "%", First(rows, SummaryBuilder.Cpu, SensorType.Load, "CPU Total"));
        Add(phase, "Процессор: частота", "МГц", First(rows, SummaryBuilder.Cpu, SensorType.Clock, "Cores (Average)")
            ?? Max(rows, SummaryBuilder.Cpu, SensorType.Clock));
        Add(phase, "Процессор: мощность", "Вт", First(rows, SummaryBuilder.Cpu, SensorType.Power, "Package"));
        Add(phase, "Процессор: напряжение", "В", Max(rows, SummaryBuilder.Cpu, SensorType.Voltage));

        Add(phase, "Видеокарта: температура", "°C", First(rows, SummaryBuilder.Gpu, SensorType.Temperature, "GPU Core")
            ?? Max(rows, SummaryBuilder.Gpu, SensorType.Temperature));
        Add(phase, "Видеокарта: hot spot", "°C", First(rows, SummaryBuilder.Gpu, SensorType.Temperature, "Hot Spot"));
        Add(phase, "Видеокарта: загрузка", "%", First(rows, SummaryBuilder.Gpu, SensorType.Load, "GPU Core"));
        Add(phase, "Видеокарта: мощность", "Вт", First(rows, SummaryBuilder.Gpu, SensorType.Power, "GPU Package"));
        Add(phase, "Видеокарта: частота", "МГц", First(rows, SummaryBuilder.Gpu, SensorType.Clock, "GPU Core"));
        Add(phase, "Видеокарта: вентилятор", "об/мин", Max(rows, SummaryBuilder.Gpu, SensorType.Fan));

        Add(phase, "Память: загрузка", "%", First(rows, SummaryBuilder.Ram, SensorType.Load, "Memory"));
        Add(phase, "Память: занято", "ГБ", First(rows, SummaryBuilder.Ram, SensorType.Data, "Memory Used"));

        Add(phase, "Накопители: температура", "°C", Max(rows, SummaryBuilder.Storage, SensorType.Temperature));
        Add(phase, "Накопители: чтение", "МБ/с", Divide(Max(rows, SummaryBuilder.Storage, SensorType.Throughput, "Read"), 1024 * 1024));
        Add(phase, "Накопители: запись", "МБ/с", Divide(Max(rows, SummaryBuilder.Storage, SensorType.Throughput, "Write"), 1024 * 1024));

        Add(phase, "Плата: температура", "°C", Max(rows, SummaryBuilder.Board, SensorType.Temperature));
        Add(phase, "Вентиляторы: максимум", "об/мин", Max(rows, null, SensorType.Fan));
        Add(phase, "Плата: +12 В", "В", Voltage(rows, 12.0, "+12", "12V"));
        Add(phase, "Плата: +5 В", "В", Voltage(rows, 5.0, "+5V", "5VSB"));
        Add(phase, "Плата: +3.3 В", "В", Voltage(rows, 3.3, "+3.3", "3.3V", "3VCC"));

        Add(phase, "Сеть: приём", "КБ/с", Divide(MaxThroughput(rows, "Download"), 1024));
        Add(phase, "Сеть: отдача", "КБ/с", Divide(MaxThroughput(rows, "Upload"), 1024));
    }

    private static void Add(AutoTestPhase phase, string name, string unit, double? value)
    {
        if (value is { } v) phase.Metric(name, unit).Add(v);
    }

    private static double? Divide(double? value, double divisor) =>
        value.HasValue ? value.Value / divisor : null;

    private static double? Max(IReadOnlyList<SensorRow> rows, string? group, SensorType type, string? nameContains = null)
    {
        double? max = null;
        foreach (SensorRow row in rows)
        {
            if (!row.IsMeasurement || row.Type != type || !row.DisplayValue.HasValue) continue;
            if (group is not null && row.HardwareGroup != group) continue;
            if (nameContains is not null && !row.Sensor.Contains(nameContains, StringComparison.OrdinalIgnoreCase)) continue;
            if (max is null || row.DisplayValue.Value > max) max = row.DisplayValue.Value;
        }

        return max;
    }

    private static double? First(IReadOnlyList<SensorRow> rows, string group, SensorType type, string nameContains)
    {
        foreach (SensorRow row in rows)
        {
            if (!row.IsMeasurement || row.Type != type || row.HardwareGroup != group) continue;
            if (row.DisplayValue.HasValue && row.Sensor.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                return row.DisplayValue.Value;
        }

        return null;
    }

    private static double? MaxThroughput(IReadOnlyList<SensorRow> rows, string nameContains)
    {
        double max = 0;
        bool any = false;
        foreach (SensorRow row in rows)
        {
            if (!row.IsMeasurement || row.Type != SensorType.Throughput || !row.DisplayValue.HasValue) continue;
            if (!row.Sensor.Contains(nameContains, StringComparison.OrdinalIgnoreCase)) continue;
            max = Math.Max(max, row.DisplayValue.Value);
            any = true;
        }

        return any ? max : null;
    }

    /// <summary>
    /// Ищет напряжение платы. Сначала по величине (датчики часто называются AVCC,
    /// Voltage #3 и подобным), и только потом по названию — при этом значение
    /// по названию тоже должно быть правдоподобным для этой шины.
    /// </summary>
    private static double? Voltage(IReadOnlyList<SensorRow> rows, double nominal, params string[] nameHints)
    {
        SensorRow? byValue = null;
        SensorRow? byName = null;
        double lower = nominal * 0.82;
        double upper = nominal * 1.18;

        foreach (SensorRow row in rows)
        {
            if (!row.IsMeasurement || row.Type != SensorType.Voltage || !row.DisplayValue.HasValue) continue;
            if (row.HardwareGroup != SummaryBuilder.Board) continue;

            double value = row.DisplayValue.Value;
            if (value >= lower && value <= upper) byValue ??= row;

            string name = row.Sensor.Replace(" ", string.Empty).ToUpperInvariant();
            foreach (string hint in nameHints)
            {
                string clean = hint.Replace(" ", string.Empty).ToUpperInvariant();
                if (name.Contains(clean) && value >= nominal * 0.6 && value <= nominal * 1.4) byName ??= row;
            }
        }

        return (byName ?? byValue)?.DisplayValue;
    }

    // ============================ Служебное ============================

    private void Finish(AutoTestPhase phase)
    {
        _report.Phases.Add(phase);
        _active = null;
        PhaseCompleted?.Invoke(phase);
        Log($"Фаза «{phase.Name}» завершена за {phase.Duration.TotalSeconds:0} с" +
            (phase.Errors > 0 ? $", ошибок {phase.Errors}" : ", ошибок нет"));
    }

    private void Report(AutoTestPhase phase, string message)
    {
        phase.Log.Add(message);
        Log($"[{phase.Name}] {message}");
    }

    private void Log(string message)
    {
        _report.Log.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        LogLine?.Invoke(message);
    }
}

/// <summary>Простая нагрузка на нейромодуль (или на процессор, если NPU нет).</summary>
internal static class NpuBenchmark
{
    public static double Run(TimeSpan duration, CancellationToken token)
    {
        const int size = 256;
        float[,] a = new float[size, size];
        float[,] b = new float[size, size];
        float[,] c = new float[size, size];
        var random = new Random(1234);

        for (int i = 0; i < size; i++)
        {
            for (int j = 0; j < size; j++)
            {
                a[i, j] = (float)random.NextDouble();
                b[i, j] = (float)random.NextDouble();
            }
        }

        var stopwatch = Stopwatch.StartNew();
        long operations = 0;
        while (stopwatch.Elapsed < duration && !token.IsCancellationRequested)
        {
            Multiply(a, b, c, size);
            operations += 2L * size * size * size;
        }

        stopwatch.Stop();
        double gflops = operations / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds) / 1e9;
        return gflops;
    }

    private static void Multiply(float[,] a, float[,] b, float[,] c, int size)
    {
        System.Threading.Tasks.Parallel.For(0, size, i =>
        {
            for (int j = 0; j < size; j++)
            {
                float sum = 0;
                for (int k = 0; k < size; k++) sum += a[i, k] * b[k, j];
                c[i, j] = sum;
            }
        });
    }
}
