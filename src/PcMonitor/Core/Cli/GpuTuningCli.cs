using PcMonitor.Core.Gpu;

namespace PcMonitor.Core;

/// <summary>
/// Режим командной строки для тонкой настройки видеокарты (--gpu-oc).
/// Нужен и для проверки: видно, что именно принимает драйвер.
/// </summary>
internal static class GpuTuningCli
{
    public static int Run(string[] args)
    {
        ConsoleHelper.Attach();

        // Весь вывод собираем: при --out он сохраняется в файл.
        var log = new System.Text.StringBuilder();
        void Write(string line)
        {
            Console.WriteLine(line);
            log.AppendLine(line);
        }

        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            Console.WriteLine(
                """
                Тонкая настройка видеокарты (NVAPI) — Мониторинг ПК.

                  PcMonitor.exe --gpu-oc                 показать состояние
                  PcMonitor.exe --gpu-oc --probe         подобрать версии структур NVAPI
                  PcMonitor.exe --gpu-oc --power 110     лимит мощности в процентах
                  PcMonitor.exe --gpu-oc --fan 70        обороты вентилятора в процентах
                  PcMonitor.exe --gpu-oc --fan-auto      вернуть автоматический режим
                  PcMonitor.exe --gpu-oc --core 100 --mem 400    смещения частот, МГц
                  PcMonitor.exe --gpu-oc --reset         сбросить смещения частот
                """);
            return 0;
        }

        if (args.Any(a => a is "--probe"))
        {
            foreach (string line in GpuTuning.Probe()) Write(line);
            return 0;
        }

        if (args.Any(a => a is "--scan"))
        {
            foreach (string line in GpuTuning.Scan()) Write(line);
            return 0;
        }

        string? dump = Value(args, "--dump");
        if (dump is not null)
        {
            foreach (string line in GpuTuning.Dump(dump)) Write(line);
            return 0;
        }

        bool changed = false;
        string? limit = Value(args, "--limit");
        string? lockCore = Value(args, "--lock-core");

        if (args.Any(a => a is "--reset-clocks"))
        {
            Write(NvidiaSmi.ResetClocks());
            changed = true;
        }

        if (limit is not null && double.TryParse(limit, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double watts))
        {
            Write(NvidiaSmi.SetPowerLimit(watts));
            changed = true;
        }

        if (lockCore is not null)
        {
            string[] parts = lockCore.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out int min) && int.TryParse(parts[1], out int max))
            {
                Write(NvidiaSmi.LockCoreClocks(min, max));
                changed = true;
            }
        }

        string? power = Value(args, "--power");
        string? fan = Value(args, "--fan");
        string? core = Value(args, "--core");
        string? memory = Value(args, "--mem");

        if (power is not null && int.TryParse(power, out int powerPercent))
        {
            Write(GpuTuning.SetPowerLimitPercent(powerPercent));
            changed = true;
        }

        if (fan is not null && int.TryParse(fan, out int fanPercent))
        {
            Write(GpuTuning.SetFanPercent(fanPercent));
            changed = true;
        }

        if (args.Any(a => a is "--fan-auto"))
        {
            Write(GpuTuning.ResetFan());
            changed = true;
        }

        if (core is not null || memory is not null)
        {
            int coreMhz = core is not null && int.TryParse(core, out int c) ? c : 0;
            int memoryMhz = memory is not null && int.TryParse(memory, out int m) ? m : 0;
            Write(GpuTuning.SetOffsets(coreMhz, memoryMhz));
            changed = true;
        }

        if (args.Any(a => a is "--reset"))
        {
            Write(GpuTuning.SetOffsets(0, 0));
            changed = true;
        }

        NvidiaGpuStatus smi = NvidiaSmi.Read();
        Write(string.Empty);
        Write("Питание и частоты (официальный интерфейс драйвера)");
        Write(new string('-', 90));
        Write($"Доступно: {(smi.Available ? "да" : "нет")} — {smi.Diagnostics}");
        Write($"Лимит мощности: {smi.PowerLimitWatts:0} Вт из диапазона {smi.PowerRangeText}, заводское {smi.PowerDefaultWatts:0} Вт");
        Write($"Частоты: ядро {smi.CoreClockMhz} МГц, память {smi.MemoryClockMhz} МГц; температура {smi.TemperatureC} °C, вентилятор {smi.FanPercent} %");
        Write(new string('-', 90));
        Write(string.Empty);

        GpuTuningState state = GpuTuning.Read();
        Write(string.Empty);
        Write("Состояние видеокарты — Мониторинг ПК");
        Write(new string('=', 90));
        Write($"Доступно управление: {(state.Available ? "да" : "нет")} ({state.Diagnostics})");
        Write($"Видеокарта: {state.Gpu}");
        Write($"Лимит мощности: {state.PowerText} (по умолчанию {state.PowerDefaultMw / 1000.0:0} Вт, " +
              $"диапазон {state.PowerRangeText})");
        Write($"Температурный предел: {(state.TemperatureLimitC > 0 ? state.TemperatureLimitC + " °C" : "—")}, " +
              $"сейчас {(state.TemperatureCurrentC > 0 ? state.TemperatureCurrentC + " °C" : "—")}");
        Write($"Вентилятор: {state.FanPercent} %, {state.FanRpm} об/мин");
        Write(string.Empty);

        foreach (string detail in state.Details) Write("  • " + detail);

        string? outPath = Value(args, "--out");
        if (outPath is not null)
        {
            try
            {
                string full = Path.GetFullPath(outPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, log.ToString(), new System.Text.UTF8Encoding(false));
                Console.WriteLine("Отчёт сохранён: " + full);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Не удалось сохранить отчёт: " + ex.Message);
            }
        }

        if (changed)
        {
            Console.WriteLine();
            Console.WriteLine("Изменения применены. Проверить температуру и стабильность можно авто-тестом " +
                              "или стресс-тестом во вкладке «Тесты и бенчмарки».");
        }

        return 0;
    }

    private static string? Value(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        }

        return null;
    }
}
