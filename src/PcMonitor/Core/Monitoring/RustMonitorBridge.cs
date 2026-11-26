using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LibreHardwareMonitor.Hardware;

namespace PcMonitor.Core;

/// <summary>Безопасный stdio-мост к длительно работающему Rust monitor sidecar.</summary>
internal static class RustMonitorBridge
{
    private static readonly object Sync = new();
    private static string? _latestLine;
    private static readonly AutoResetEvent OutputReady = new(false);
    private static Process? _process;
    private static string? _lastError;

    internal static IReadOnlyList<SensorReading> Collect(out string? error)
    {
        lock (Sync)
        {
            error = null;
            try
            {
                EnsureStarted();
                if (!TryReadLine(TimeSpan.FromSeconds(5), out string? json))
                {
                    error = _process is { HasExited: true }
                        ? $"Rust monitor exited with code {_process.ExitCode}. {_lastError}"
                        : "Timed out waiting for a Rust monitor snapshot.";
                    StopLocked();
                    return Array.Empty<SensorReading>();
                }

                using JsonDocument document = JsonDocument.Parse(json!);
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("error", out JsonElement nativeError))
                {
                    error = nativeError.GetString() ?? nativeError.ToString();
                    return Array.Empty<SensorReading>();
                }

                if (!root.TryGetProperty("sensors", out JsonElement sensors) || sensors.ValueKind != JsonValueKind.Array)
                {
                    error = "Rust monitor JSON line does not contain a sensors array.";
                    StopLocked();
                    return Array.Empty<SensorReading>();
                }

                var result = new List<SensorReading>(sensors.GetArrayLength());
                foreach (JsonElement item in sensors.EnumerateArray())
                {
                    string? sensorTypeText = Text(item, "sensor_type");
                    string? hardwareTypeText = Text(item, "hardware_type");
                    if (!TryEnum(sensorTypeText, out SensorType sensorType) ||
                        !TryEnum(hardwareTypeText, out HardwareType hardwareType))
                        continue;

                    result.Add(new SensorReading
                    {
                        Id = Text(item, "id") ?? throw new InvalidDataException("Rust sensor has no id."),
                        Hardware = NormalizeText(Text(item, "hardware"), "Устройство"),
                        OwnName = NormalizeText(Text(item, "own_name"), "Устройство"),
                        // hardware_group в старых версиях sidecar мог приходить как mojibake.
                        // hardware_type является машинным enum, поэтому используем его как
                        // единственный источник группы для агрегатов вкладки «Обзор».
                        HardwareGroup = SensorCatalog.HardwareGroupName(hardwareType),
                        HardwareType = hardwareType,
                        SensorType = sensorType,
                        SensorName = NormalizeText(Text(item, "sensor_name"), sensorType.ToString()),
                        Value = Number(item, "value"),
                        Min = Number(item, "min") ?? 0,
                        Max = Number(item, "max") ?? 0,
                    });
                }
                return result;
            }
            catch (Exception ex)
            {
                error = "Rust monitor failed: " + ex.Message;
                StopLocked();
                return Array.Empty<SensorReading>();
            }
        }
    }

    internal static void Stop()
    {
        lock (Sync) StopLocked();
    }

    private static void EnsureStarted()
    {
        if (_process is { HasExited: false }) return;
        StopLocked();

        string executable = Path.Combine(AppContext.BaseDirectory, "pcmonitor-native.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("Rust monitor executable is missing next to PcMonitor.exe.", executable);

        var start = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "--watch --interval-ms=1000",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        Process launched = _process;
        _process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null && ReferenceEquals(Volatile.Read(ref _process), launched))
            {
                Interlocked.Exchange(ref _latestLine, args.Data);
                OutputReady.Set();
            }
        };
        _process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data)) Volatile.Write(ref _lastError, args.Data);
        };
        if (!_process.Start()) throw new InvalidOperationException("Failed to start Rust monitor process.");
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private static bool TryReadLine(TimeSpan timeout, out string? line)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            line = Interlocked.Exchange(ref _latestLine, null);
            if (line is not null) return true;
            TimeSpan remaining = timeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero) break;
            OutputReady.WaitOne(remaining);
        } while (true);
        line = null;
        return false;
    }

    private static void StopLocked()
    {
        Process? process = _process;
        _process = null;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit(1_000);
            }
            catch { /* Освобождение процесса не должно прерывать опрос датчиков. */ }
            finally { process.Dispose(); }
        }
        Interlocked.Exchange(ref _latestLine, null);
        Volatile.Write(ref _lastError, null);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string NormalizeText(string? value, string fallback)
    {
        if (string.IsNullOrEmpty(value)) return fallback;
        if (!value.Contains('\u0420') && !value.Contains('\u0421')) return value;

        try
        {
            string repaired = Encoding.UTF8.GetString(Encoding.GetEncoding(1251).GetBytes(value));
            return repaired.Contains('\uFFFD') ? value : repaired;
        }
        catch
        {
            return value;
        }
    }

    private static float? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double number) || !double.IsFinite(number))
            return null;
        float result = (float)number;
        return float.IsFinite(result) ? result : null;
    }

    private static bool TryEnum<T>(string? value, out T result) where T : struct, Enum
    {
        result = default;
        return value is not null && Enum.TryParse(value.Replace("_", string.Empty, StringComparison.Ordinal), true, out result);
    }
}
