using LibreHardwareMonitor.Hardware;

namespace PcMonitor.Core;

public sealed partial class MonitorService
{
    private readonly Dictionary<string, FanProfile> _fanProfiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FanRuntime> _fanStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SensorReading> _latestFanReadings = new(StringComparer.Ordinal);
    private DateTime _lastFanSnapshotUtc;
    private System.Threading.Timer? _fanWatchdog;

    public string ConfigureFan(string id, FanProfile requested)
    {
        var profile = requested.Copy();
        profile.Normalize();
        if (profile.Mode is FanProfile.Graph or FanProfile.Mix && !FanCurve.TryParse(profile.Points, out _))
            return "Ошибка кривой: нужны минимум две разные точки температура:процент, разделённые ;";
        lock (_controlSync)
        {
            if (!_controls.ContainsKey(id)) return "Контроллер недоступен: проверьте подключение и права администратора.";
            if (profile.Mode == FanProfile.Bios) return RestoreFan(id);
            if (!IsRunning || IsPaused) return "Сначала возобновите мониторинг для безопасного управления.";
            _fanProfiles[id] = profile;
            _fanStates.Remove(id);
            ApplyFanProfile(id, profile, DateTime.UtcNow);
            return _fanStates[id].Status;
        }
    }

    private string RpmIdForControl(string id)
    {
        if (_fanProfiles.TryGetValue(id, out FanProfile? profile) && !string.IsNullOrWhiteSpace(profile.RpmSensorId))
            return profile.RpmSensorId;
        // Same channel is matched by the hardware identifier/index, not a guessed RPM value.
        string candidate = id.Replace("/control/", "/fan/", StringComparison.Ordinal);
        return candidate != id && _latestFanReadings.TryGetValue(candidate, out SensorReading? sensor)
            && sensor.SensorType == SensorType.Fan ? candidate : string.Empty;
    }

    private float? RpmForControl(string id)
    {
        string rpmId = RpmIdForControl(id);
        return _latestFanReadings.TryGetValue(rpmId, out SensorReading? row) && row.SensorType == SensorType.Fan
            && row.Value is >= 0 && row.Value.HasValue && float.IsFinite(row.Value.Value) ? row.Value : null;
    }

    private void ApplyFanProfiles(IReadOnlyList<SensorReading> readings)
    {
        _latestFanReadings.Clear();
        foreach (SensorReading reading in readings) _latestFanReadings[reading.Id] = reading;
        _lastFanSnapshotUtc = DateTime.UtcNow;
        foreach ((string id, FanProfile profile) in _fanProfiles)
            ApplyFanProfile(id, profile, _lastFanSnapshotUtc);
    }

    private double? TemperatureFor(string id)
    {
        IEnumerable<SensorReading> source = _latestFanReadings.Values;
        if (id == "@cpu") source = source.Where(r => r.HardwareType == HardwareType.Cpu);
        else if (id == "@gpu") source = source.Where(r => r.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel);
        else source = source.Where(r => r.Id == id);
        return source.Where(r => r.SensorType == SensorType.Temperature && r.Value.HasValue
                && float.IsFinite(r.Value.Value) && r.Value.Value > 0 && r.Value.Value <= 130
                && !SensorCatalog.IsThresholdSensor(r.SensorType, r.SensorName))
            .Select(r => (double?)r.Value!.Value).DefaultIfEmpty(null).Max();
    }

    private void ApplyFanProfile(string id, FanProfile profile, DateTime now)
    {
        if (!_controls.TryGetValue(id, out var control)) return;
        if (!_fanStates.TryGetValue(id, out FanRuntime? state)) _fanStates[id] = state = new FanRuntime();
        try
        {
            double? primary = TemperatureFor(profile.SensorId);
            double? second = profile.Mode == FanProfile.Mix ? TemperatureFor(profile.SecondSensorId) : null;
            double? cpu = TemperatureFor("@cpu"), gpu = TemperatureFor("@gpu");
            bool critical = (primary >= profile.CriticalTemperature) || (second >= profile.CriticalTemperature)
                || cpu >= 95 || gpu >= 90;
            bool stale = (now - _lastFanSnapshotUtc).TotalSeconds > 5;
            bool missing = profile.Mode != FanProfile.Manual && (!primary.HasValue || (profile.Mode == FanProfile.Mix && !second.HasValue));
            double desired;
            string reason;
            if (critical || stale || missing)
            {
                desired = 100;
                reason = critical ? "Перегрев: защита 100 %" : "Температура недоступна: защита 100 %";
                state.FallingSinceUtc = null;
            }
            else if (profile.Mode == FanProfile.Manual)
            {
                desired = FanCurve.ClampOutput(profile, profile.ManualPercent);
                reason = $"Ручной · {desired:0} %";
            }
            else
            {
                double temperature = primary!.Value;
                // Do not suppress a rising temperature; hysteresis damps falling/small fluctuations.
                if (state.LastTemperature is double previous && temperature < previous && previous - temperature < profile.Hysteresis)
                    temperature = previous;
                state.LastTemperature = temperature;
                desired = FanCurve.Demand(profile, temperature);
                if (profile.Mode == FanProfile.Mix)
                {
                    double other = FanCurve.Demand(profile, second!.Value);
                    desired = profile.MixFunction switch
                    {
                        "Минимум" => Math.Min(desired, other),
                        "Среднее" => (desired + other) / 2,
                        _ => Math.Max(desired, other),
                    };
                }
                reason = $"{profile.Mode} · {primary:0.#} °C → {desired:0} %";
                if (state.LastPercent is double last)
                {
                    double elapsed = Math.Clamp((now - state.LastAppliedUtc).TotalSeconds, 0, 3);
                    if (desired < last)
                    {
                        state.FallingSinceUtc ??= now;
                        desired = (now - state.FallingSinceUtc.Value).TotalSeconds < profile.DownDelaySeconds
                            ? last : Math.Max(desired, last - profile.StepDownPerSecond * elapsed);
                    }
                    else
                    {
                        state.FallingSinceUtc = null;
                        desired = Math.Min(desired, last + profile.StepUpPerSecond * elapsed);
                    }
                }
            }
            if (!state.LastPercent.HasValue || Math.Abs(desired - state.LastPercent.Value) >= 0.25)
                control.Control.SetSoftware((float)desired);
            state.LastPercent = desired;
            state.LastAppliedUtc = now;
            state.Status = reason;
        }
        catch (Exception ex) { state.Status = "Ошибка контроллера: " + ex.Message; }
    }

    private void CheckFanWatchdog()
    {
        if (!IsRunning || IsPaused || !Monitor.TryEnter(_controlSync)) return;
        try
        {
            if ((DateTime.UtcNow - _lastFanSnapshotUtc).TotalSeconds <= 5) return;
            foreach ((string id, FanProfile profile) in _fanProfiles)
                ApplyFanProfile(id, profile, DateTime.UtcNow);
        }
        finally { Monitor.Exit(_controlSync); }
    }

    private void ReleaseFanHardware()
    {
        lock (_controlSync)
        {
            foreach (string id in _fanStates.Keys.ToArray())
            {
                if (!_controls.TryGetValue(id, out var entry)) continue;
                try { entry.Control.SetDefault(); } catch { }
            }
            _fanStates.Clear();
        }
    }
}
