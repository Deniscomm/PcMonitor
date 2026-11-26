using System.Diagnostics;
using System.Text;

namespace PcMonitor.Core.Diagnostics;

/// <summary>Минимальный неблокирующий журнал времени запуска и инициализации.</summary>
public static class StartupDiagnostics
{
    private static readonly object Sync = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static string? _path;

    public static void Begin()
    {
        try
        {
            string directory = Path.Combine(AppSettings.DataDirectory, "logs");
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, "startup.log");
            File.AppendAllText(_path, $"\r\n=== START {DateTime.Now:O} pid={Environment.ProcessId} ===\r\n", Encoding.UTF8);
        }
        catch { }
    }

    public static void Mark(string stage)
    {
        string line = $"{DateTime.Now:O} +{Clock.Elapsed.TotalMilliseconds,9:0} ms | {stage}{Environment.NewLine}";
        try
        {
            lock (Sync)
            {
                if (_path is not null) File.AppendAllText(_path, line, Encoding.UTF8);
            }
        }
        catch { }
    }

    public static void Error(string stage, Exception exception) =>
        Mark($"ERROR {stage}: {exception.GetType().Name}: {exception.Message}");
}
