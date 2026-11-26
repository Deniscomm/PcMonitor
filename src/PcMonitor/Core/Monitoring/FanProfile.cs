using System.Globalization;

namespace PcMonitor.Core;

public sealed class FanProfile
{
    public const string Bios = "BIOS";
    public const string Manual = "Ручной";
    public const string Linear = "Линейная";
    public const string Graph = "График";
    public const string Mix = "Смешанная";
    public string Name { get; set; } = string.Empty;
    public string Mode { get; set; } = Bios;
    public double ManualPercent { get; set; } = 50;
    public string SensorId { get; set; } = "@cpu";
    public string SecondSensorId { get; set; } = "@gpu";
    public string MixFunction { get; set; } = "Максимум";
    public string RpmSensorId { get; set; } = string.Empty;
    public double MinimumPercent { get; set; } = 30;
    public double MaximumPercent { get; set; } = 100;
    public double LowTemperature { get; set; } = 35;
    public double HighTemperature { get; set; } = 80;
    public double CriticalTemperature { get; set; } = 90;
    public double Hysteresis { get; set; } = 2;
    public double StepUpPerSecond { get; set; } = 25;
    public double StepDownPerSecond { get; set; } = 8;
    public double DownDelaySeconds { get; set; } = 3;
    public bool AllowStop { get; set; }
    public string Points { get; set; } = "30:30; 45:40; 60:60; 75:85; 85:100";

    public FanProfile Copy() => (FanProfile)MemberwiseClone();

    public void Normalize()
    {
        if (Mode is not (Bios or Manual or Linear or Graph or Mix)) Mode = Bios;
        MinimumPercent = FiniteClamp(MinimumPercent, 0, 100, 30);
        MaximumPercent = FiniteClamp(MaximumPercent, MinimumPercent, 100, 100);
        ManualPercent = FiniteClamp(ManualPercent, 0, 100, 50);
        LowTemperature = FiniteClamp(LowTemperature, 0, 95, 35);
        HighTemperature = FiniteClamp(HighTemperature, LowTemperature + 1, 120, 80);
        CriticalTemperature = FiniteClamp(CriticalTemperature, 60, 100, 90);
        Hysteresis = FiniteClamp(Hysteresis, 0, 15, 2);
        StepUpPerSecond = FiniteClamp(StepUpPerSecond, 1, 100, 25);
        StepDownPerSecond = FiniteClamp(StepDownPerSecond, 1, 100, 8);
        DownDelaySeconds = FiniteClamp(DownDelaySeconds, 0, 60, 3);
    }

    internal static double FiniteClamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

public readonly record struct FanPoint(double Temperature, double Percent);

public static class FanCurve
{
    public static bool TryParse(string? text, out FanPoint[] points)
    {
        var result = new List<FanPoint>();
        foreach (string part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] pair = part.Split(':', StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !TryNumber(pair[0], out double t) || !TryNumber(pair[1], out double p)
                || t < 0 || t > 130 || p < 0 || p > 100) { points = []; return false; }
            result.Add(new FanPoint(t, p));
        }
        points = result.OrderBy(p => p.Temperature).ToArray();
        return points.Length >= 2 && points.Select(p => p.Temperature).Distinct().Count() == points.Length;
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    public static double Interpolate(IReadOnlyList<FanPoint> points, double temperature)
    {
        if (points.Count < 2 || !double.IsFinite(temperature)) throw new ArgumentException("Invalid fan curve input.");
        if (temperature <= points[0].Temperature) return points[0].Percent;
        for (int i = 1; i < points.Count; i++)
        {
            if (temperature > points[i].Temperature) continue;
            FanPoint a = points[i - 1], b = points[i];
            return a.Percent + (b.Percent - a.Percent) * (temperature - a.Temperature) / (b.Temperature - a.Temperature);
        }
        return points[^1].Percent;
    }

    public static double Demand(FanProfile profile, double temperature)
    {
        double desired = profile.Mode is FanProfile.Graph or FanProfile.Mix && TryParse(profile.Points, out FanPoint[] points)
            ? Interpolate(points, temperature)
            : profile.MinimumPercent + (profile.MaximumPercent - profile.MinimumPercent) *
              Math.Clamp((temperature - profile.LowTemperature) / (profile.HighTemperature - profile.LowTemperature), 0, 1);
        return ClampOutput(profile, desired);
    }

    public static double ClampOutput(FanProfile profile, double desired)
    {
        // Zero-RPM is explicit opt-in, otherwise avoid accidental stalls, including manual mode.
        if (profile.AllowStop && desired <= 0) return 0;
        return Math.Clamp(desired, Math.Max(profile.AllowStop ? 0 : 20, profile.MinimumPercent),
            Math.Max(profile.AllowStop ? 0 : 20, profile.MaximumPercent));
    }
}

internal sealed class FanRuntime
{
    public double? LastPercent;
    public double? LastTemperature;
    public DateTime LastAppliedUtc;
    public DateTime? FallingSinceUtc;
    public string Status = "BIOS";
}
