using System.Windows;
using PcMonitor.Core;
using PcMonitor.Core.Stress;

namespace PcMonitor;

/// <summary>
/// Точка входа. Обычный запуск открывает окно мониторинга,
/// запуск с ключом --dump печатает отчёт по датчикам в консоль.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a is "--gpu-bench"))
            return GpuBenchmarkCli.Run(args);

        if (args.Any(a => a is "--bench"))
            return BenchmarkCli.Run(args);

        if (args.Any(a => a is "--stress"))
            return StressCli.Run(args);

        if (args.Any(a => a is "--autotest"))
            return AutoTestCli.Run(args);

        if (args.Any(a => a is "--sysinfo"))
            return SystemInfoCli.Run(args);

        if (args.Any(a => a is "--gpu-oc"))
            return GpuTuningCli.Run(args);

        if (args.Any(a => a is "--fans"))
            return FansCli.Run(args);

        if (args.Any(a => a is "--rgb"))
            return RgbCli.Run(args);

        if (args.Any(a => a is "--dump" or "--json" or "--help" or "-h" or "/?"))
            return CliDumper.Run(args);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
