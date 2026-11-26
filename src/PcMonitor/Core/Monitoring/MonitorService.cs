using System.Diagnostics;
using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using PcMonitor.Core.Diagnostics;

namespace PcMonitor.Core;

/// <summary>
/// Опрашивает все доступные датчики компьютера в фоновом потоке
/// и передаёт снимки значений в интерфейс.
/// </summary>
public sealed partial class MonitorService : IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private volatile bool _paused;
    private int _rustBridgeWarningReported;
    private int _firstFullSnapshotLogged;

    /// <summary>Управляемые вентиляторы: идентификатор датчика — интерфейс управления.</summary>
    private readonly Dictionary<string, (IControl Control, string Hardware, string Name)> _controls = new(StringComparer.Ordinal);
    private readonly object _controlSync = new();

    /// <summary>Обновить список управляемых вентиляторов (вызывается при опросе).</summary>
    private void TrackControls(IEnumerable<(IHardware Hardware, ISensor Sensor)> sensors)
    {
        lock (_controlSync)
        {
            _controls.Clear();
            foreach ((IHardware hardware, ISensor sensor) in sensors)
            {
                if (sensor.Control is null) continue;
                string id = sensor.Identifier.ToString();
                _controls[id] = (sensor.Control, hardware.Name, string.IsNullOrWhiteSpace(sensor.Name) ? "Вентилятор" : sensor.Name);
            }
        }
    }

    /// <summary>Список управляемых вентиляторов: идентификатор, устройство, название, текущий режим.</summary>
    public IReadOnlyList<FanControlInfo> FanControls()
    {
        lock (_controlSync)
        {
            return _controls
                .Select(pair => new FanControlInfo(
                    pair.Key,
                    pair.Value.Hardware,
                    pair.Value.Name,
                    pair.Value.Control.ControlMode.ToString(),
                    pair.Value.Control.SoftwareValue,
                    RpmForControl(pair.Key),
                    RpmIdForControl(pair.Key),
                    _fanStates.TryGetValue(pair.Key, out FanRuntime? state) ? state.Status : string.Empty))
                .ToList();
        }
    }

    /// <summary>
    /// Прямое управление вентилятором: значение в процентах.
    /// Возвращает описание результата; исключения не выбрасываются.
    /// </summary>
    public string SetFanPercent(string id, float percent)
    {
        lock (_controlSync)
        {
            if (!_controls.TryGetValue(id, out (IControl Control, string Hardware, string Name) entry))
                return "Вентилятор недоступен: датчик не найден. Запустите приложение с правами администратора.";

            try
            {
                if (!float.IsFinite(percent)) return "Недопустимое значение PWM.";
                _fanProfiles.Remove(id);
                _fanStates.Remove(id);
                float applied = Math.Clamp(percent, 0f, 100f);
                entry.Control.SetSoftware(applied);
                return $"{entry.Name}: задано {applied:0} % (режим {entry.Control.ControlMode})";
            }
            catch (Exception ex)
            {
                return $"Не удалось задать обороты для {entry.Name}: {ex.Message}";
            }
        }
    }

    /// <summary>Возвращает управление вентилятором драйверу (автоматический режим).</summary>
    public string RestoreFan(string id)
    {
        lock (_controlSync)
        {
            if (!_controls.TryGetValue(id, out (IControl Control, string Hardware, string Name) entry))
                return "Вентилятор недоступен: датчик не найден.";

            try
            {
                _fanProfiles.Remove(id);
                _fanStates.Remove(id);
                entry.Control.SetDefault();
                return $"{entry.Name}: возвращён автоматический режим";
            }
            catch (Exception ex)
            {
                return $"Не удалось вернуть авторежим для {entry.Name}: {ex.Message}";
            }
        }
    }

    /// <summary>Возвращает автоматический режим всем управляемым вентиляторам.</summary>
    public string RestoreAllFans()
    {
        int count = 0;
        List<string> errors = new();

        lock (_controlSync)
        {
            _fanProfiles.Clear();
            _fanStates.Clear();
            foreach ((string id, (IControl Control, string Hardware, string Name) entry) in _controls)
            {
                try
                {
                    entry.Control.SetDefault();
                    count++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{entry.Name}: {ex.Message}");
                }
            }
        }

        return errors.Count == 0
            ? $"Автоматический режим возвращён: вентиляторов {count}"
            : $"Возвращено {count}, ошибок {errors.Count}: {string.Join("; ", errors)}";
    }

    /// <summary>Интервал опроса.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    public bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent())
        .IsInRole(WindowsBuiltInRole.Administrator);

    public bool IsRunning => _thread is { IsAlive: true };

    public bool IsPaused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>Железо перечислено, датчики доступны.</summary>
    public event Action? Started;

    /// <summary>Очередной снимок показаний (вызывается из фонового потока).</summary>
    public event Action<IReadOnlyList<SensorReading>, DateTime>? SnapshotReady;

    /// <summary>Ошибка инициализации или опроса.</summary>
    public event Action<string>? Failed;

    public void Start()
    {
        lock (_sync)
        {
            if (IsRunning) return;
            _cts = new CancellationTokenSource();
            _thread = new Thread(() => Loop(_cts.Token))
            {
                IsBackground = true,
                Name = "PcMonitor.Sensors",
                Priority = ThreadPriority.Normal,
            };
            _thread.Start();
            _fanWatchdog ??= new System.Threading.Timer(_ => CheckFanWatchdog(), null, 1000, 1000);
        }
    }

    public void Stop()
    {
        Thread? thread;
        lock (_sync)
        {
            _cts?.Cancel();
            thread = _thread;
            _thread = null;
        }

        if (thread is { IsAlive: true } && thread != Thread.CurrentThread)
            thread.Join(TimeSpan.FromSeconds(3));

        RustMonitorBridge.Stop();
        _fanWatchdog?.Dispose();
        _fanWatchdog = null;
        RestoreAllFans();
        _cts?.Dispose();
        _cts = null;
    }

    private void Loop(CancellationToken token)
    {
        StartupDiagnostics.Mark("monitor.loop.begin");
        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsNetworkEnabled = true,
            IsStorageEnabled = true,
            IsPsuEnabled = true,
            IsBatteryEnabled = true,
            IsPowerMonitorEnabled = true,
        };
        StartupDiagnostics.Mark("hardware.sources.configured: cpu,gpu,memory,motherboard,controller,network,storage,psu,battery,powermonitor");

        _ = Task.Run(() =>
        {
            try
            {
                IReadOnlyList<SensorReading> early = RustMonitorBridge.Collect(out _);
                if (!token.IsCancellationRequested && early.Count > 0)
                {
                    StartupDiagnostics.Mark($"rust.first.snapshot ({early.Count} sensors)");
                    SnapshotReady?.Invoke(early, DateTime.Now);
                }
            }
            catch
            {
                // Полный сбор через основной backend продолжится независимо.
            }
        });

        try
        {
            StartupDiagnostics.Mark("librehardwaremonitor.open.begin");
            computer.Open();
            StartupDiagnostics.Mark("librehardwaremonitor.open.end");
            StartupDiagnostics.Mark("monitor.ready");
        }
        catch (Exception ex)
        {
            Failed?.Invoke("Не удалось инициализировать датчики: " + ex.Message);
            return;
        }

        try
        {
            Started?.Invoke();
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex.Message);
        }

        bool extendedOpened = true;
        var stopwatch = new Stopwatch();
        while (!token.IsCancellationRequested)
        {
            if (_paused)
            {
                RustMonitorBridge.Stop();
                ReleaseFanHardware();
                if (token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(150))) break;
                continue;
            }

            stopwatch.Restart();
            IReadOnlyList<SensorReading> readings;
            try
            {
                readings = Collect(computer);
                if (extendedOpened && Interlocked.Exchange(ref _firstFullSnapshotLogged, 1) == 0)
                {
                    StartupDiagnostics.Mark($"first.full.hardware.snapshot ({readings.Count} sensors)");
                    int valued = readings.Count(static reading => reading.Value.HasValue);
                    int unique = readings.Select(static reading => reading.Id).Distinct(StringComparer.Ordinal).Count();
                    string groups = string.Join(",", readings
                        .GroupBy(static reading => reading.HardwareType)
                        .OrderBy(static group => group.Key.ToString(), StringComparer.Ordinal)
                        .Select(static group => $"{group.Key}={group.Count()}"));
                    StartupDiagnostics.Mark($"hardware.snapshot.stats: valued={valued};unique={unique};duplicates={readings.Count - unique};types={groups}");
                    string duplicates = string.Join(" | ", readings
                        .GroupBy(static reading => reading.Id, StringComparer.Ordinal)
                        .Where(static group => group.Count() > 1)
                        .Select(static group => $"{group.Key}=[{string.Join(" + ", group.Select(static reading => reading.Hardware))}]"));
                    if (duplicates.Length > 0)
                        StartupDiagnostics.Mark($"hardware.snapshot.duplicate.ids: {duplicates}");
                }
            }
            catch (Exception ex)
            {
                Failed?.Invoke("Ошибка чтения датчиков: " + ex.Message);
                break;
            }

            try
            {
                SnapshotReady?.Invoke(readings, DateTime.Now);
            }
            catch
            {
                // Ошибки подписчика не должны останавливать опрос.
            }

            TimeSpan wait = Interval - stopwatch.Elapsed;
            if (wait > TimeSpan.FromMilliseconds(10))
            {
                if (token.WaitHandle.WaitOne(wait)) break;
            }
            else
            {
                if (token.WaitHandle.WaitOne(5)) break;
            }
        }

        try
        {
            lock (_controlSync)
            {
                RestoreAllFans();
                computer.Close();
                _controls.Clear();
            }
        }
        catch
        {
            // Игнорируем ошибки закрытия драйвера.
        }
    }

    private List<SensorReading> Collect(Computer computer)
    {
        var list = new List<SensorReading>(512);
        var controls = new List<(IHardware Hardware, ISensor Sensor)>();

        lock (_controlSync)
        {
            foreach (IHardware hardware in computer.Hardware)
                CollectHardware(hardware, list, null, controls);
            TrackControls(controls);
            ApplyFanProfiles(list);
        }

        // Показания Rust добавляются к исходному драйверному источнику до переноса
        // полного набора аппаратных датчиков и управления вентиляторами.
        IReadOnlyList<SensorReading> rustReadings = RustMonitorBridge.Collect(out string? rustError);
        list.AddRange(rustReadings);
        if (rustError is not null && Interlocked.Exchange(ref _rustBridgeWarningReported, 1) == 0)
        {
            try { Failed?.Invoke("Источник Rust недоступен: " + rustError); }
            catch { /* Диагностический подписчик не должен останавливать сбор данных. */ }
        }

        // Список управляемых вентиляторов обновляем на каждом опросе: он нужен
        // для прямого управления оборотами из интерфейса и из авто-теста.
        return list;
    }

    private static void CollectHardware(IHardware hardware, List<SensorReading> list, string? parentName,
        List<(IHardware Hardware, ISensor Sensor)>? controls)
    {
        string hardwareName = string.IsNullOrWhiteSpace(hardware.Name) ? "Неизвестное устройство" : hardware.Name;
        string fullName = parentName is null ? hardwareName : parentName + " · " + hardwareName;
        string group = SensorCatalog.HardwareGroupName(hardware.HardwareType);

        try
        {
            hardware.Accept(UpdateVisitor.Instance);
        }
        catch
        {
            // Часть устройств может быть недоступна без прав администратора — продолжаем.
        }

        foreach (ISensor sensor in hardware.Sensors)
        {
            float? value = sensor.Value;
            if (value.HasValue && (float.IsNaN(value.Value) || float.IsInfinity(value.Value)))
                value = null;

            // Датчики с управлением (вентиляторы платы) запоминаем отдельно.
            if (sensor.Control is not null) controls?.Add((hardware, sensor));

            list.Add(new SensorReading
            {
                Id = sensor.Identifier.ToString(),
                Hardware = fullName,
                OwnName = hardwareName,
                HardwareGroup = group,
                HardwareType = hardware.HardwareType,
                SensorType = sensor.SensorType,
                SensorName = string.IsNullOrWhiteSpace(sensor.Name) ? sensor.SensorType.ToString() : sensor.Name,
                Value = value,
                Min = sensor.Min ?? 0,
                Max = sensor.Max ?? 0,
            });
        }

        foreach (IHardware sub in hardware.SubHardware)
            CollectHardware(sub, list, fullName, controls);
    }

    public void Dispose() => Stop();
}
