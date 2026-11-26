using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PcMonitor.Core.Stress;

/// <summary>
/// Тесты кэш-подсистемы памяти: обход большого буфера с разными шагами, чтобы попадать
/// в L1, L2, L3 и оперативную память, а также проверять работу аппаратного предвыборщика.
/// </summary>
/// <remarks>
/// Основная измеряемая величина — пропускная способность (МБ/с). Каждый тест сначала
/// подготавливает буфер, а затем многократно обходит его выбранным шаблоном; полезная работа
/// каждой итерации сворачивается в контрольную сумму, поэтому JIT не может выбросить цикл.
/// Итерации прогрева (JIT и кэш) в отчёт не включаются.
/// </remarks>
internal static class CacheStressTests
{
    private const int WarmupIterations = 4;

    /// <summary>Собирает список тестов кэша.</summary>
    internal static IReadOnlyList<IStressTest> Build()
    {
        const StressCategory C = StressCategory.Cache;
        return new List<IStressTest>
        {
            StressTestFactory.Create("cache.stride-64b", C,
                "Шаг 64 байта (строка кэша)",
                "Последовательный обход с шагом 64 байта — работа внутри строки кэша L1.",
                TimeSpan.FromMilliseconds(300), c => Strided(c, 64, reads: false)),

            StressTestFactory.Create("cache.stride-256b", C,
                "Шаг 256 байт",
                "Обход с шагом 256 байт: несколько обращений подряд остаются внутри L1.",
                TimeSpan.FromMilliseconds(450), c => Strided(c, 256, reads: false)),

            StressTestFactory.Create("cache.stride-4k", C,
                "Шаг 4 КБ (страница)",
                "Обход с шагом 4096 байт — типичный «прыжок» между страницами виртуальной памяти.",
                TimeSpan.FromMilliseconds(450), c => Strided(c, 4096, reads: false)),

            StressTestFactory.Create("cache.stride-64k", C,
                "Шаг 64 КБ",
                "Обход с шагом 64 КБ — рабочий набор быстро перестаёт помещаться в L2.",
                TimeSpan.FromMilliseconds(600), c => Strided(c, 64 * 1024, reads: false)),

            StressTestFactory.Create("cache.stride-1m", C,
                "Шаг 1 МБ",
                "Обход с шагом 1 МБ — проверка L3 и таблиц страниц второго уровня.",
                TimeSpan.FromMilliseconds(600), c => Strided(c, 1024 * 1024, reads: true)),

            StressTestFactory.Create("cache.stride-8m", C,
                "Шаг 8 МБ",
                "Обращения с шагом 8 МБ почти всегда уходят в оперативную память.",
                TimeSpan.FromMilliseconds(1200), c => Strided(c, 8 * 1024 * 1024, reads: true)),

            StressTestFactory.Create("cache.stride-32m", C,
                "Шаг 32 МБ (RAM)",
                "Самый «дальний» шаг: 32 МБ между обращениями — чистая пропускная способность памяти.",
                TimeSpan.FromMilliseconds(1200), c => Strided(c, 32 * 1024 * 1024, reads: true)),

            StressTestFactory.Create("cache.random-64b", C,
                "Случайные обращения по строкам кэша",
                "Случайные смещения с выравниванием на 64 байта: проверка ассоциативности и банков памяти.",
                TimeSpan.FromMilliseconds(1200), c => RandomLines(c, 64)),

            StressTestFactory.Create("cache.random-4k", C,
                "Случайные обращения по 4 КБ",
                "Случайные смещения с выравниванием на 4096 байт — промахи TLB вместе с промахами кэша.",
                TimeSpan.FromMilliseconds(1200), c => RandomLines(c, 4096)),

            StressTestFactory.Create("cache.read-modify-write", C,
                "Чтение-изменение-запись (64 Б)",
                "Каждая строка кэша читается, изменяется и записывается обратно — нагрузка на когерентность.",
                TimeSpan.FromMilliseconds(900), c => ReadModifyWrite(c, 64)),

            StressTestFactory.Create("cache.full-scan", C,
                "Полный проход по буферу",
                "Последовательный потоковый обход всего буфера с вычислением контрольной суммы.",
                TimeSpan.FromMilliseconds(1200), c => Strided(c, 8, reads: true)),

            StressTestFactory.Create("cache.alternating-scan", C,
                "Прямой и обратный проход",
                "Буфер проходится поочерёдно вперёд и назад — проверка предвыборщика на смене направления.",
                TimeSpan.FromMilliseconds(300), c => Alternating(c)),
        };
    }

    /// <summary>Буфер для тестов кэша: не меньше 128 МБ и не меньше запрошенной памяти.</summary>
    private static BufferPool Pool(WorkerView w) =>
        w.Stage.CachePool ??= BufferPool.Allocate(
            Math.Max(128L * 1024 * 1024, w.Stage.Options.MemoryBytes),
            w.Stage.Options.Seed ^ 0x5CA1E,
            w.Stage.Options.EffectiveThreads);

    /// <summary>Последовательный обход с заданным шагом и контрольной суммой по словам.</summary>
    private static StageOutcome Strided(WorkerView w, int stride, bool reads)
    {
        BufferPool pool = Pool(w);
        byte[] buffer = pool.Primary;
        long operations = 0;
        double bytes = 0;
        int iterations = 0;

        while (!w.Stop)
        {
            ulong checksum = 0x1BADB002UL;
            int index = 0;
            int words = 0;
            while (index + 8 <= buffer.Length)
            {
                ulong value = BitConverter.ToUInt64(buffer, index);
                if (!reads)
                    BitConverter.TryWriteBytes(buffer.AsSpan(index), value + 1);

                checksum ^= value;
                index += stride;
                words++;
            }

            iterations++;
            if (iterations > WarmupIterations)
            {
                operations += words;
                bytes += (double)words * 8 * (reads ? 1 : 2);
                w.RecordWork((double)words * 8 * (reads ? 1 : 2), words);
            }

            // Меняем маркер, чтобы результат итерации нельзя было предсказать заранее.
            BitConverter.TryWriteBytes(buffer.AsSpan(0, 8), checksum);
        }

        float marker = BitConverter.ToSingle(buffer, 0);
        return new StageOutcome
        {
            Bytes = bytes,
            Operations = operations,
            Expected = $"обход {buffer.Length / (1024 * 1024)} МБ с шагом {stride} Б",
            Actual = iterations > 0 ? $"итераций: {iterations}, маркер {marker:0.###}" : "буфер пуст",
        };
    }

    /// <summary>Случайные обращения, выровненные на строку кэша; полезная нагрузка — чтение и запись.</summary>
    private static StageOutcome RandomLines(WorkerView w, int lineSize)
    {
        BufferPool pool = Pool(w);
        byte[] buffer = pool.Primary;
        int lines = Math.Max(1, buffer.Length / lineSize);
        Random random = w.Random;
        long operations = 0;
        double bytes = 0;
        int probes = 0;

        while (!w.Stop)
        {
            ulong checksum = 0x9E3779B97F4A7C15UL;
            for (int i = 0; i < 65536; i++)
            {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
                int line = random.Next(lines);
                int offset = line * lineSize;
                ulong value = BitConverter.ToUInt64(buffer, offset);
                checksum ^= value + (ulong)offset;
                BitConverter.TryWriteBytes(buffer.AsSpan(offset), value + 1);
            }

            probes += 65536;
            operations += 65536L * (lineSize / 8);
            bytes += 65536.0 * lineSize * 2;
            w.RecordWork(65536.0 * lineSize * 2, 65536L * (lineSize / 8));
            BitConverter.TryWriteBytes(buffer.AsSpan(0, 8), checksum);
        }

        return new StageOutcome
        {
            Bytes = bytes,
            Operations = operations,
            Expected = $"случайные обращения по {lineSize} Б в буфере {buffer.Length / (1024 * 1024)} МБ",
            Actual = $"обращений: {probes}",
        };
    }

    /// <summary>Чтение-изменение-запись строк кэша: максимальная нагрузка на когерентность.</summary>
    private static StageOutcome ReadModifyWrite(WorkerView w, int lineSize)
    {
        BufferPool pool = Pool(w);
        byte[] buffer = pool.Primary;
        int lines = Math.Max(1, buffer.Length / lineSize);
        long operations = 0;
        double bytes = 0;
        int passes = 0;

        while (!w.Stop)
        {
            for (int line = 0; line < lines; line++)
            {
                if ((line & 16383) == 0) w.ThrowIfCancellationRequested();
                int offset = line * lineSize;
                for (int i = 0; i + 8 <= lineSize; i += 8)
                {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
                    ulong value = BitConverter.ToUInt64(buffer, offset + i);
                    BitConverter.TryWriteBytes(buffer.AsSpan(offset + i), value ^ 0x0101010101010101UL);
                }
            }

            passes++;
            operations += (long)lines * (lineSize / 8);
            bytes += (double)lines * lineSize * 2;
            w.RecordWork((double)lines * lineSize * 2, (long)lines * (lineSize / 8));
        }

        return new StageOutcome
        {
            Bytes = bytes,
            Operations = operations,
            Expected = $"чтение-изменение-запись строк по {lineSize} Б",
            Actual = $"проходов: {passes}",
        };
    }

    /// <summary>Прямой и обратный проходы по буферу с шагом 64 байта.</summary>
    private static StageOutcome Alternating(WorkerView w)
    {
        BufferPool pool = Pool(w);
        byte[] buffer = pool.Primary;
        long operations = 0;
        double bytes = 0;
        int iterations = 0;
        int direction = 1;

        while (!w.Stop)
        {
            ulong checksum = 0;
            if (direction > 0)
            {
                for (int i = 0; i + 8 <= buffer.Length; i += 64)
                    checksum ^= BitConverter.ToUInt64(buffer, i);
            }
            else
            {
                for (int i = ((buffer.Length - 8) / 64) * 64; i >= 0; i -= 64)
                    checksum ^= BitConverter.ToUInt64(buffer, i);
            }

            BitConverter.TryWriteBytes(buffer.AsSpan(0, 8), checksum);
            direction = -direction;
            iterations++;
            operations += buffer.Length / 64;
            bytes += buffer.Length;
            w.RecordWork(buffer.Length, buffer.Length / 64);
        }

        return new StageOutcome
        {
            Bytes = bytes,
            Operations = operations,
            Expected = $"прямой/обратный проход по буферу {buffer.Length / (1024 * 1024)} МБ",
            Actual = $"проходов: {iterations}",
        };
    }
}
