using System;
using System.Collections.Generic;
using System.Threading;

namespace PcMonitor.Core.Stress;

/// <summary>
/// Тесты оперативной памяти в духе шаблонного теста памяти: собственный адрес, moving inversions 8/16/32/64 бит,
/// walking ones/zeros, шахматные шаблоны, случайные данные, блочное копирование, затухание битов,
/// «modulo 20», случайный доступ и сравнение двух буферов.
/// </summary>
/// <remarks>
/// Каждый тест пишет шаблон во все ячейки, затем читает и сверяет их. Любое расхождение
/// увеличивает счётчик ошибок этапа, а ожидаемое и фактическое значения показываются в hex.
/// Тесты памяти полностью потокобезопасны: каждый рабочий поток получает свою часть буфера
/// и собственный генератор случайных чисел.
/// </remarks>
internal static class MemoryStressTests
{
    private const int MaxSampleErrors = 64;

    /// <summary>Собирает список тестов памяти.</summary>
    internal static IReadOnlyList<IStressTest> Build()
    {
        const StressCategory C = StressCategory.Memory;
        var list = new List<IStressTest>
        {
            StressTestFactory.Create("mem.own-address", C,
                "Собственный адрес (однопоточный)",
                "В каждую ячейку записывается её собственный адрес и инверсный адрес — обнаруживает ошибки адресации.",
                TimeSpan.FromMilliseconds(300), MemOwnAddress, throughputUnit: "МБ/с"),

            StressTestFactory.Create("mem.own-address-mt", C,
                "Собственный адрес (многопоточный)",
                "То же самое, но все процессоры работают каждый со своей частью буфера.",
                TimeSpan.FromMilliseconds(600), MemOwnAddress, useManyThreads: true),

            StressTestFactory.Create("mem.moving-inv-8", C,
                "Moving inversion 8 бит",
                "Классика шаблонного теста памяти: инверсия байтового шаблона при движении по всей памяти.",
                TimeSpan.FromMilliseconds(600), MemMovingInversion8),

            StressTestFactory.Create("mem.moving-inv-16", C,
                "Moving inversion 16 бит",
                "Инверсия 16-битных слов — ловит парные (coupling) ошибки соседних ячеек.",
                TimeSpan.FromMilliseconds(900), MemMovingInversion16),

            StressTestFactory.Create("mem.moving-inv-32", C,
                "Moving inversion 32 бита",
                "Инверсия 32-битных слов с чередованием прямого и обратного прохода.",
                TimeSpan.FromMilliseconds(1200), MemMovingInversion32),

            StressTestFactory.Create("mem.moving-inv-64", C,
                "Moving inversion 64 бита",
                "Инверсия 64-битных слов; самая тяжёлая разновидность moving inversion.",
                TimeSpan.FromMilliseconds(1800), MemMovingInversion64),

            StressTestFactory.Create("mem.walking-ones", C,
                "Walking ones",
                "Единичный бит последовательно проходит по всем 32 позициям слова — проверка залипания нулей.",
                TimeSpan.FromMilliseconds(900), MemWalkingOnes),

            StressTestFactory.Create("mem.walking-zeros", C,
                "Walking zeros",
                "Единственный нулевой бит проходит по всем позициям — проверка залипания единиц.",
                TimeSpan.FromMilliseconds(900), MemWalkingZeros),

            StressTestFactory.Create("mem.random-seed", C,
                "Случайный шаблон (зерно)",
                "Буфер заполняется псевдослучайными 32-битными словами от заданного зерна и проверяется.",
                TimeSpan.FromMilliseconds(600), MemRandomPattern),

            StressTestFactory.Create("mem.random-seed-mt", C,
                "Случайный шаблон (многопоточный)",
                "Случайные шаблоны, каждый поток работает со своим диапазоном и своим зерном.",
                TimeSpan.FromMilliseconds(1200), MemRandomPattern, useManyThreads: true),

            StressTestFactory.Create("mem.chess-8", C,
                "Шахматный шаблон 8 бит",
                "Шахматное чередование 0x55/0xAA по байтам — проверка взаимного влияния соседей.",
                TimeSpan.FromMilliseconds(300), MemChess8),

            StressTestFactory.Create("mem.chess-16", C,
                "Шахматный шаблон 16 бит",
                "Шахматное чередование 16-битных слов 0x5555/0xAAAA.",
                TimeSpan.FromMilliseconds(600), c => MemChess(c, 2)),

            StressTestFactory.Create("mem.chess-32", C,
                "Шахматный шаблон 32 бита",
                "Шахматное чередование 32-битных слов 0x55555555/0xAAAAAAAA.",
                TimeSpan.FromMilliseconds(1200), c => MemChess(c, 4)),

            StressTestFactory.Create("mem.checkerboard", C,
                "Шахматная доска 64 байта",
                "Инверсия целых 64-байтных строк кэша (шахматная доска по строкам).",
                TimeSpan.FromMilliseconds(600), MemCheckerboard),

            StressTestFactory.Create("mem.block-copy", C,
                "Блочное копирование с проверкой",
                "Буфер копируется блоками от 64 байт, каждая копия сверяется с источником.",
                TimeSpan.FromMilliseconds(600), MemBlockCopy),

            StressTestFactory.Create("mem.bit-fade", C,
                "Затухание битов (bit fade)",
                "Записанный шаблон выдерживается во времени и затем проверяется — выявляет «уставшие» ячейки.",
                TimeSpan.FromMilliseconds(1200), MemBitFade),

            StressTestFactory.Create("mem.modulo-20", C,
                "Modulo 20 (20 шаблонов)",
                "Позиция ячейки определяет, какой из 20 шаблонов в неё записан — ловит ошибки дешифратора адреса.",
                TimeSpan.FromMilliseconds(1200), MemModulo20),

            StressTestFactory.Create("mem.random-access", C,
                "Случайный доступ",
                "Случайные смещения по всей памяти: запись, чтение и немедленная сверка значения.",
                TimeSpan.FromMilliseconds(1200), MemRandomAccess),

            StressTestFactory.Create("mem.interleaved-rw", C,
                "Чередование чтения и записи",
                "Для каждой ячейки сначала читается старое значение, затем пишется инверсное и проверяется.",
                TimeSpan.FromMilliseconds(600), MemInterleavedReadWrite),

            StressTestFactory.Create("mem.dual-buffer", C,
                "Два буфера со сравнением",
                "Данные пишутся в один буфер и зеркалятся во второй; далее буферы сравниваются байт за байтом.",
                TimeSpan.FromMilliseconds(1800), MemDualBuffer),

            StressTestFactory.Create("mem.word-64", C,
                "Шаблон 64 бита (multi-thread)",
                "Запись и проверка 64-битных слов во всех потоках — максимальная нагрузка на шину памяти.",
                TimeSpan.FromMilliseconds(1800), MemWord64, useManyThreads: true),

            StressTestFactory.Create("mem.page-crossing", C,
                "Переход через границу страниц",
                "Шаблон пишется блоками по 4096 байт с выравниванием на границу страницы виртуальной памяти.",
                TimeSpan.FromMilliseconds(600), MemPageCrossing),

            StressTestFactory.Create("mem.cacheline-burst", C,
                "Пакетная запись по строкам кэша",
                "64-байтные строки кэша заполняются пакетами по 8 строк с последующей проверкой.",
                TimeSpan.FromMilliseconds(300), MemCacheLineBurst),

            StressTestFactory.Create("mem.inversion-full", C,
                "Полная инверсия буфера",
                "Весь буфер инвертируется байт за байтом и проверяется — простой, но очень быстрый стресс.",
                TimeSpan.FromMilliseconds(600), MemInversionFull),
        };

        return list;
    }

    // ---------------------------------------------------------------- работа с буферами

    private static int ChunkCount(WorkerView w) => w.Stage.Pool.Chunks.Count;

    private static byte[] Chunk(WorkerView w, int index) => w.Stage.Pool.Chunks[index];

    /// <summary>Вычисляет диапазон кусков для текущего потока, чтобы потоки не пересекались.</summary>
    private static (int Start, int End) Range(WorkerView w)
    {
        int total = ChunkCount(w);
        int threads = Math.Max(1, w.Stage.Options.EffectiveThreads);
        int index = Math.Min(w.Index, threads - 1);
        int start = (int)((long)total * index / threads);
        int end = (int)((long)total * (index + 1) / threads);
        return (Math.Min(start, total - 1), Math.Max(start + 1, end));
    }

    /// <summary>Смещение последнего полного 8-байтного слова в буфере.</summary>
    private static int LastWordOffset(ReadOnlySpan<byte> span) => ((span.Length - 8) / 8) * 8;

    /// <summary>Записывает значение шаблона в 8 байт по указанному смещению.</summary>
    private static void WriteWord(Span<byte> span, int offset, ulong value)
    {
        if (offset + 8 <= span.Length)
            BitConverter.TryWriteBytes(span.Slice(offset, 8), value);
        else
            for (int i = 0; i < 8; i++)
                span[offset + i] = (byte)(value >> (8 * i));
    }

    /// <summary>Читает 8 байт как 64-битное слово.</summary>
    private static ulong ReadWord(ReadOnlySpan<byte> span, int offset)
    {
        if (offset + 8 <= span.Length)
            return BitConverter.ToUInt64(span.Slice(offset, 8));

        ulong value = 0;
        for (int i = 0; i < 8; i++)
            value |= (ulong)span[offset + i] << (8 * i);
        return value;
    }

    // ---------------------------------------------------------------- общие шаблоны

    private static ulong Pattern(int kind, int stage, int pass, long address, ulong seed)
    {
        ulong a = unchecked((ulong)address);
        switch (kind)
        {
            // 0/1: шаблон зависит от собственного адреса ячейки — базовая проверка адресации.
            case 0: return a ^ ~seed;
            case 1: return ~(a ^ seed);

            // 2: случайные данные — каждое слово имеет своё псевдослучайное значение.
            case 2: return Mix(a ^ seed, (ulong)stage + (ulong)pass * 0x9E3779B97F4A7C15UL);

            // 9: «modulo 20» — позиция ячейки выбирает один из 20 шаблонов.
            case 9:
                {
                    ulong m = (a >> 3) % 20;
                    return Mix(m, 0xC2B2AE3D27D4EB4FUL) ^ Mix(m + 1, 0x165667B19E3779F9UL);
                }

            default: return Mix(a, (ulong)stage);
        }
    }

    private static ulong Mix(ulong x, ulong seed)
    {
        ulong h = x + seed + 0x9E3779B97F4A7C15UL;
        h ^= h >> 30;
        h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 27;
        h *= 0x94D049BB133111EBUL;
        return h ^ (h >> 31);
    }

    /// <summary>
    /// Записывает шаблон во весь диапазон, читает его и сверяет. Все найденные расхождения
    /// попадают в счётчик ошибок этапа, а объём и число проверок возвращаются в результате.
    /// </summary>
    private static StageOutcome RunPass(WorkerView w, int kind, int pass, long seed, long addressSalt, ushort[]? extra, string title)
    {
        (int start, int end) = Range(w);
        double bytes = 0;
        double operations = 0;
        long errors = 0;
        long checks = 0;
        string expectedText = title;
        string actualText = "совпадает";

        for (int index = start; index < end && !w.Stop && !w.Stage.Token.IsCancellationRequested; index++)
        {
            byte[] chunk = Chunk(w, index);
            ulong chunkSeed = unchecked((ulong)seed + (ulong)index * 0x9E3779B97F4A7C15UL);
            Span<byte> span = chunk.AsSpan();
            int offset = 0;
            while (offset + 8 <= span.Length && !w.Stage.Token.IsCancellationRequested)
            {
                ulong address = (ulong)(addressSalt + (long)index * chunk.Length + offset);
                ulong expected = Pattern(kind, w.Stage.Stage, pass, (long)address, chunkSeed);
                if (extra is not null)
                    expected ^= extra[(int)((address >> 3) % (ulong)extra.Length)];

                WriteWord(span, offset, expected);
                offset += 8;
            }

            offset = 0;
            while (offset + 8 <= span.Length && !w.Stage.Token.IsCancellationRequested)
            {
                ulong address = (ulong)(addressSalt + (long)index * chunk.Length + offset);
                ulong expected = Pattern(kind, w.Stage.Stage, pass, (long)address, chunkSeed);
                if (extra is not null)
                    expected ^= extra[(int)((address >> 3) % (ulong)extra.Length)];

                ulong actual = ReadWord(span, offset);
                checks++;
                if (actual != expected)
                {
                    errors++;
                    if (errors <= MaxSampleErrors)
                    {
                        expectedText = "0x" + expected.ToString("X16");
                        actualText = "0x" + actual.ToString("X16");
                    }
                    else if (errors > 5_000_000)
                    {
                        // Защита от бесконечного потока ошибок: прекращаем проверку этого диапазона.
                        break;
                    }
                }

                offset += 8;
            }

            bytes += chunk.Length * 2.0;
            operations += checks;
        }

        if (errors > 0)
            w.FailFast(errors);
        else
            w.Check(title, $"совпадает ({checks} проверок)");

        return new StageOutcome
        {
            Bytes = bytes,
            Operations = operations,
            Expected = expectedText,
            Actual = actualText,
        };
    }

    /// <summary>Складывает результаты двух проходов одного этапа.</summary>
    private static StageOutcome Combine(StageOutcome first, StageOutcome second) => new()
    {
        Bytes = first.Bytes + second.Bytes,
        Operations = first.Operations + second.Operations,
        Expected = first.Expected,
        Actual = first.Actual == second.Actual ? first.Actual : $"{first.Actual}; {second.Actual}",
    };

    // ---------------------------------------------------------------- конкретные тесты

    private static StageOutcome MemOwnAddress(WorkerView w)
    {
        StageOutcome forward = RunPass(w, 0, 0, w.Seed, 0, null, "ячейка содержит собственный адрес");
        StageOutcome backward = RunPass(w, 1, 1, w.Seed, 0x1000, null, "ячейка содержит инверсный адрес");
        return Combine(forward, backward);
    }

    private static StageOutcome MemMovingInversion8(WorkerView w) => MovingInversion(w, 8);

    private static StageOutcome MemMovingInversion16(WorkerView w) => MovingInversion(w, 16);

    private static StageOutcome MemMovingInversion32(WorkerView w) => MovingInversion(w, 32);

    private static StageOutcome MemMovingInversion64(WorkerView w) => MovingInversion(w, 64);

    /// <summary>
    /// Классический moving inversion: буфер заполняется известным шаблоном, затем каждая ячейка
    /// (8/16/32/64 бита) инвертируется со сдвигом прохода; после записи выполняется обратный
    /// проход, который читает данные и сверяет их с эталоном, сообщая о любом расхождении.
    /// </summary>
    private static StageOutcome MovingInversion(WorkerView w, int bits)
    {
        int wordsPerCell = 8 / (bits / 8);
        ulong mask = bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;
        long errors = 0;
        long checks = 0;
        double bytes = 0;
        string expectedText = "маска 0x" + mask.ToString("X");
        string actualText = "0x0";
        TimeSpan lastIteration = TimeSpan.Zero;

        for (int pass = 0; pass < 64 && w.Stage.HasTimeFor(lastIteration) && !w.Stage.Token.IsCancellationRequested; pass++)
        {
                if ((pass & 16383) == 0) w.ThrowIfCancellationRequested();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            (int start, int end) = Range(w);
            ulong shift = unchecked((ulong)pass * 0x9E3779B97F4A7C15UL) ^ unchecked((ulong)w.Seed);

            // Эталон: инверсия одного и того же исходного значения. Блок специально заполняется
            // постоянным шаблоном, поэтому после инверсии все ячейки обязаны быть равны эталону.
            ulong sourceWord = Mix(0, unchecked((ulong)w.Seed + (ulong)pass));
            ulong expectedWord = Transform(sourceWord, wordsPerCell, mask, bits, shift);
            expectedText = "0x" + expectedWord.ToString("X16");

            for (int index = start; index < end && !w.Stop && !w.Stage.Token.IsCancellationRequested; index++)
            {
                if ((index & 16383) == 0) w.ThrowIfCancellationRequested();
                byte[] chunk = Chunk(w, index);
                Span<byte> span = chunk.AsSpan();

                // Проход вперёд: постоянный шаблон, затем инверсия каждой ячейки на месте.
                for (int pos = 0; pos + 8 <= span.Length && !w.Stage.Token.IsCancellationRequested; pos += 8)
                    WriteWord(span, pos, sourceWord);

                for (int pos = 0; pos + 8 <= span.Length && !w.Stage.Token.IsCancellationRequested; pos += 8)
                    WriteWord(span, pos, Transform(ReadWord(span, pos), wordsPerCell, mask, bits, shift));

                // Проход назад: строгая сверка того, что записано. Проверку начинаем только
                // целиком — иначе обрыв по времени дал бы «ложные» расхождения.
                if (!w.Stage.HasTimeFor(lastIteration))
                    break;

                for (int pos = LastWordOffset(span); pos >= 0 && !w.Stage.Token.IsCancellationRequested; pos -= 8)
                {
                if ((pos & 16383) == 0) w.ThrowIfCancellationRequested();
                    ulong actual = ReadWord(span, pos);
                    checks++;
                    if (actual != expectedWord)
                    {
                        errors++;
                        if (errors <= MaxSampleErrors)
                            actualText = "0x" + actual.ToString("X16");
                    }
                }

                bytes += chunk.Length * 2.0;
            }

            lastIteration = watch.Elapsed;
        }

        if (errors > 0)
        {
            w.FailFast(errors);
            w.Check(expectedText, actualText);
        }
        else
        {
            w.Check(expectedText, "инверсии применены и проверены");
        }

        return new StageOutcome
        {
            Bytes = bytes,
            Operations = checks,
            Expected = expectedText,
            Actual = errors > 0 ? actualText : "инверсии применены и проверены",
        };
    }

    /// <summary>Инвертирует каждую часть слова (8/16/32/64 бита) по отдельности со сдвигом прохода.</summary>
    private static ulong Transform(ulong value, int wordsPerCell, ulong mask, int bits, ulong shift)
    {
        ulong result = value;
        for (int i = 0; i < wordsPerCell; i++)
        {
            int bitOffset = i * bits;
            ulong piece = (value >> bitOffset) & mask;
            ulong inverted = (~piece + shift) & mask;
            result = (result & ~(mask << bitOffset)) | (inverted << bitOffset);
        }

        return result;
    }

    private static StageOutcome MemWalkingOnes(WorkerView w) => WalkingBits(w, ones: true);

    private static StageOutcome MemWalkingZeros(WorkerView w) => WalkingBits(w, ones: false);

    /// <summary>Walking ones/zeros: единственный выделенный бит (или единственный ноль) идёт по всем 32 позициям.</summary>
    private static StageOutcome WalkingBits(WorkerView w, bool ones)
    {
        long errors = 0;
        long checks = 0;
        double bytes = 0;
        string expectedText = "0x00000001";
        string actualText = "0x00000000";
        TimeSpan lastIteration = TimeSpan.Zero;

        for (int pass = 0; pass < 32 && w.Stage.HasTimeFor(lastIteration) && !w.Stage.Token.IsCancellationRequested; pass++)
        {
                if ((pass & 16383) == 0) w.ThrowIfCancellationRequested();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            ulong pattern = ones ? 1UL << pass : ~(1UL << pass);
            ulong value = pattern | (pattern << 32);

            (int start, int end) = Range(w);
            for (int index = start; index < end && !w.Stop && !w.Stage.Token.IsCancellationRequested; index++)
            {
                if ((index & 16383) == 0) w.ThrowIfCancellationRequested();
                Span<byte> span = Chunk(w, index).AsSpan();
                for (int pos = 0; pos + 8 <= span.Length && !w.Stage.Token.IsCancellationRequested; pos += 8)
                    WriteWord(span, pos, value);

                for (int pos = 0; pos + 8 <= span.Length && !w.Stage.Token.IsCancellationRequested; pos += 8)
                {
                if ((pos & 16383) == 0) w.ThrowIfCancellationRequested();
                    ulong actual = ReadWord(span, pos);
                    checks++;
                    if (actual != value)
                    {
                        errors++;
                        if (errors <= MaxSampleErrors)
                        {
                            expectedText = "0x" + value.ToString("X16");
                            actualText = "0x" + actual.ToString("X16");
                        }
                    }
                }

                bytes += Chunk(w, index).Length * 2.0;
            }

            lastIteration = watch.Elapsed;
        }

        if (errors > 0)
        {
            w.FailFast(errors);
            w.Check(expectedText, actualText);
        }
        else
        {
            w.Check(ones ? "шаблон walking ones" : "шаблон walking zeros", "все биты сохраняются");
        }

        return new StageOutcome
        {
            Bytes = bytes,
            Operations = checks,
            Expected = ones ? "walking ones (32 позиции)" : "walking zeros (32 позиции)",
            Actual = errors > 0 ? actualText : "все биты сохраняются",
        };
    }

    private static StageOutcome MemRandomPattern(WorkerView w)
    {
        return RunPass(w, 2, 0, w.Seed, 0x2000, null, "псевдослучайные 64-битные слова");
    }

    private static StageOutcome MemChess8(WorkerView w) => MemChess(w, 1);

    /// <summary>Шахматный шаблон шириной 1, 2 или 4 байта.</summary>
    private static StageOutcome MemChess(WorkerView w, int width)
    {
        ulong a = width switch
        {
            1 => 0x5555555555555555UL,   // шахматы по байтам: 0101…
            2 => 0x5555555500000000UL,   // шахматы по 16-битным словам
            _ => 0xAAAAAAAA00000000UL,   // шахматы по 32-битным словам
        };
        ulong b = ~a;
        long errors = 0;
        long checks = 0;
        double bytes = 0;

        foreach (byte[] chunk in Chunks(w))
        {
            if (w.Stop)
                break;

            Span<byte> span = chunk.AsSpan();
            for (int pos = 0; pos + 8 <= span.Length && !w.Stage.Token.IsCancellationRequested; pos += 8)
                WriteWord(span, pos, (((pos >> 3) & 1) == 0) ? a : b);

            for (int pos = 0; pos + 8 <= span.Length && !w.Stage.Token.IsCancellationRequested; pos += 8)
            {
                if ((pos & 16383) == 0) w.ThrowIfCancellationRequested();
                ulong expected = (((pos >> 3) & 1) == 0) ? a : b;
                ulong actual = ReadWord(span, pos);
                checks++;
                if (actual != expected)
                {
                    errors++;
                    if (errors <= MaxSampleErrors)
                        w.Check("0x" + expected.ToString("X16"), "0x" + actual.ToString("X16"));
                }
            }

            bytes += chunk.Length * 2.0;
        }

        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Bytes = bytes,
            Operations = checks,
            Expected = $"шахматный шаблон {width * 8} бит",
            Actual = errors == 0 ? "структура сохранена" : "структура нарушена",
        };
    }

    private static StageOutcome MemCheckerboard(WorkerView w)
    {
        long errors = 0;
        long checks = 0;
        double bytes = 0;
        ulong low = 0x5555555555555555UL;
        ulong high = 0xAAAAAAAAAAAAAAAAUL;

        foreach (byte[] chunk in Chunks(w))
        {
            if (w.Stop)
                break;

            Span<byte> span = chunk.AsSpan();
            for (int pos = 0; pos + 8 <= span.Length && !w.Stage.Token.IsCancellationRequested; pos += 8)
                WriteWord(span, pos, (((pos >> 6) & 1) == 0) ? low : high);

            for (int pos = 0; pos + 8 <= span.Length && !w.Stage.Token.IsCancellationRequested; pos += 8)
            {
                if ((pos & 16383) == 0) w.ThrowIfCancellationRequested();
                ulong expected = (((pos >> 6) & 1) == 0) ? low : high;
                ulong actual = ReadWord(span, pos);
                checks++;
                if (actual != expected)
                {
                    errors++;
                    if (errors <= MaxSampleErrors)
                        w.Check("0x" + expected.ToString("X16"), "0x" + actual.ToString("X16"));
                }
            }

            bytes += chunk.Length * 2.0;
        }

        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Bytes = bytes,
            Operations = checks,
            Expected = "чередование строк кэша 0x5555…/0xAAAA…",
            Actual = errors == 0 ? "чередование сохранено" : "чередование нарушено",
        };
    }

    private static StageOutcome MemBlockCopy(WorkerView w)
    {
        StageOutcome outcome = MemBlockCopyCore(w);
        return outcome;
    }

    /// <summary>Копирование блоками разного размера (64 Б … 1 МБ) со сверкой каждой копии.</summary>
    private static StageOutcome MemBlockCopyCore(WorkerView w)
    {
        byte[] primary = w.Stage.Pool.Primary;
        long errors = 0;
        long checks = 0;
        double bytes = 0;
        int[] sizes = { 64, 256, 1024, 4096, 65536, 1024 * 1024 };
        ulong marker = unchecked((ulong)w.Seed * 0x9E3779B97F4A7C15UL);

        for (int i = 0; i + 8 <= primary.Length && !w.Stage.Token.IsCancellationRequested; i += 8)
            WriteWord(primary, i, Mix((ulong)i, marker));

        foreach (int size in sizes)
        {
            if (w.Stop)
                break;

            int blockSize = Math.Min(size, primary.Length);
            byte[] copy = new byte[blockSize];
            for (int offset = 0; offset + blockSize <= primary.Length && !w.Stop && !w.Stage.Token.IsCancellationRequested; offset += blockSize)
            {
                if ((offset & 16383) == 0) w.ThrowIfCancellationRequested();
                primary.AsSpan(offset, blockSize).CopyTo(copy);
                for (int i = 0; i + 8 <= blockSize && !w.Stage.Token.IsCancellationRequested; i += 8)
                {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
                    checks++;
                    if (ReadWord(copy, i) != ReadWord(primary.AsSpan(offset), i))
                    {
                        errors++;
                        if (errors <= MaxSampleErrors)
                            w.Check("копия равна источнику", $"расхождение в блоке {blockSize} Б, смещение {offset + i}");
                    }
                }

                bytes += blockSize * 2.0;
            }

            // Дополнительный проход копированием через Span.CopyTo — нагрузка на контроллер памяти.
            bytes += primary.Length;
        }

        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Bytes = bytes,
            Operations = checks,
            Expected = "скопированные блоки идентичны источнику",
            Actual = errors == 0 ? "копии совпадают" : "копии повреждены",
        };
    }

    /// <summary>
    /// Bit fade: в буфер записывается шаблон с единственным «слабым» битом, затем данные
    /// выдерживаются во времени под нагрузкой на соседние строки и полностью сверяются.
    /// Если ячейки «теряют» записанное, сверка это покажет.
    /// </summary>
    private static StageOutcome MemBitFade(WorkerView w)
    {
        const ulong Written = 0xAAAA_AAAA_AAAA_AAAAUL;
        long errors = 0;
        long checks = 0;
        double bytes = 0;
        foreach (byte[] chunk in Chunks(w))
        {
            if (w.Stop)
                break;

            Span<byte> span = chunk.AsSpan();
            for (int pos = 0; pos + 8 <= span.Length && !w.Stage.Token.IsCancellationRequested; pos += 8)
                WriteWord(span, pos, Written);

            // «Выдержка»: данные лежат в памяти, пока идёт нагрузка на соседние строки.
            long holdStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            while (System.Diagnostics.Stopwatch.GetElapsedTime(holdStarted) < w.Stage.StageDuration * 0.2 && !w.Stop)
            {
                for (int pos = 0; pos + 8 <= span.Length && !w.Stage.Token.IsCancellationRequested; pos += 8)
                {
                if ((pos & 16383) == 0) w.ThrowIfCancellationRequested();
                    // Инвертируем и сразу возвращаем обратно: память работает, данные не меняются.
                    ulong value = ReadWord(span, pos);
                    WriteWord(span, pos, ~value);
                    WriteWord(span, pos, value);
                }
            }

            for (int pos = 0; pos + 8 <= span.Length && !w.Stage.Token.IsCancellationRequested; pos += 8)
            {
                if ((pos & 16383) == 0) w.ThrowIfCancellationRequested();
                checks++;
                ulong actual = ReadWord(span, pos);
                if (actual != Written)
                {
                    errors++;
                    if (errors <= MaxSampleErrors)
                        w.Check("0x" + Written.ToString("X16"), "0x" + actual.ToString("X16"));
                }
            }

            bytes += chunk.Length * 3.0;
        }

        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Bytes = bytes,
            Operations = checks,
            Expected = "0xAAAAAAAAAAAAAAAA после выдержки",
            Actual = errors == 0 ? "данные стабильны" : "обнаружено затухание битов",
        };
    }

    private static StageOutcome MemModulo20(WorkerView w)
    {
        return RunPass(w, 9, 0, w.Seed, 0x3000, null, "20 шаблонов по позиции ячейки");
    }

    private static StageOutcome MemRandomAccess(WorkerView w)
    {
        long errors = 0;
        long checks = 0;
        double bytes = 0;
        Random random = w.Random;

        foreach (byte[] chunk in Chunks(w))
        {
            Span<byte> span = chunk.AsSpan();
            int slots = (span.Length - 8) / 8;
            if (slots <= 0)
                continue;

            int probes = Math.Min(slots, 250_000);
            for (int i = 0; i < probes && !w.Stop && !w.Stage.Token.IsCancellationRequested; i++)
            {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
                int slot = random.Next(slots);
                int pos = slot * 8;
                ulong expected = Mix((ulong)pos, unchecked((ulong)w.Seed + (ulong)i));
                WriteWord(span, pos, expected);
                ulong actual = ReadWord(span, pos);
                checks++;
                bytes += 16;
                if (actual != expected)
                {
                    errors++;
                    if (errors <= MaxSampleErrors)
                        w.Check("0x" + expected.ToString("X16"), "0x" + actual.ToString("X16"));
                }
            }
        }

        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Bytes = bytes,
            Operations = checks,
            Expected = "значение читается сразу после записи",
            Actual = errors == 0 ? "чтение совпало с записью" : "расхождения при произвольном доступе",
        };
    }

    private static StageOutcome MemInterleavedReadWrite(WorkerView w)
    {
        long errors = 0;
        long checks = 0;
        double bytes = 0;
        ulong seed = unchecked((ulong)w.Seed);

        foreach (byte[] chunk in Chunks(w))
        {
            Span<byte> span = chunk.AsSpan();
            for (int pos = 0; pos + 8 <= span.Length && !w.Stop && !w.Stage.Token.IsCancellationRequested; pos += 16)
            {
                if ((pos & 16383) == 0) w.ThrowIfCancellationRequested();
                ulong written = Mix((ulong)pos, seed ^ (ulong)w.Stage.Stage);
                WriteWord(span, pos, written);
                ulong readBack = ReadWord(span, pos);
                checks++;
                bytes += 16;
                if (readBack != written)
                {
                    errors++;
                    if (errors <= MaxSampleErrors)
                        w.Check("0x" + written.ToString("X16"), "0x" + readBack.ToString("X16"));
                    continue;
                }

                // Читаем «старое» значение на следующей строке и записываем инверсное ему.
                if (pos + 24 <= span.Length)
                {
                    ulong neighbour = ReadWord(span, pos + 8);
                    ulong inverted = ~neighbour;
                    WriteWord(span, pos + 8, inverted);
                    ulong verify = ReadWord(span, pos + 8);
                    checks++;
                    bytes += 16;
                    if (verify != inverted)
                    {
                        errors++;
                        if (errors <= MaxSampleErrors)
                            w.Check("0x" + inverted.ToString("X16"), "0x" + verify.ToString("X16"));
                    }
                }
            }
        }

        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Bytes = bytes,
            Operations = checks,
            Expected = "чередование чтения и записи без потерь",
            Actual = errors == 0 ? "данные не теряются" : "потеря данных при чередовании",
        };
    }

    private static StageOutcome MemDualBuffer(WorkerView w)
    {
        BufferPool pool = w.Stage.Pool;
        long budget = pool.IndividualBudget;
        int size = (int)Math.Clamp(budget, 8L * 1024 * 1024, 32L * 1024 * 1024);
        byte[] first = new byte[size];
        byte[] second = new byte[size];

        // Заполняем оба буфера одинаковыми данными и сверяем их между собой.
        for (int i = 0; i + 8 <= size && !w.Stage.Token.IsCancellationRequested; i += 8)
        {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
            ulong value = Mix((ulong)i, unchecked((ulong)w.Seed));
            WriteWord(first, i, value);
            WriteWord(second, i, value);
        }

        long errors = 0;
        long checks = 0;
        for (int i = 0; i + 8 <= size && !w.Stop && !w.Stage.Token.IsCancellationRequested; i += 8)
        {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
            checks++;
            if (ReadWord(first, i) != ReadWord(second, i))
            {
                errors++;
                if (errors <= MaxSampleErrors)
                    w.Check("буферы равны", $"расхождение на смещении {i}");
            }
        }

        // Дополнительно: зеркальное копирование половины буфера во второй.
        byte[] mirror = new byte[size / 2];
        double bytes = size * 3.0;
        for (int pass = 0; pass < 3 && !w.Stop && !w.Stage.Token.IsCancellationRequested; pass++)
        {
                if ((pass & 16383) == 0) w.ThrowIfCancellationRequested();
            second.AsSpan(0, size / 2).CopyTo(mirror);
            mirror.CopyTo(second.AsSpan(size / 2));
            for (int i = 0; i + 8 <= size / 2 && !w.Stage.Token.IsCancellationRequested; i += 8)
            {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
                checks++;
                if (ReadWord(first, i) != ReadWord(second, i + (size / 2)))
                {
                    errors++;
                    break;
                }
            }

            bytes += size;
        }

        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Bytes = bytes,
            Operations = checks,
            Expected = "два буфера содержат одинаковые данные",
            Actual = errors == 0 ? "буферы совпадают" : "буферы различаются",
        };
    }

    private static StageOutcome MemWord64(WorkerView w)
    {
        return RunPass(w, 2, 0, w.Seed, 0x4000, null, "64-битные слова во всех потоках");
    }

    private static StageOutcome MemPageCrossing(WorkerView w)
    {
        long errors = 0;
        long checks = 0;
        double bytes = 0;
        foreach (byte[] chunk in Chunks(w))
        {
            Span<byte> span = chunk.AsSpan();
            for (int page = 0; page + 4096 <= span.Length && !w.Stop && !w.Stage.Token.IsCancellationRequested; page += 4096)
            {
                if ((page & 16383) == 0) w.ThrowIfCancellationRequested();
                Span<byte> block = span.Slice(page, 4096);
                ulong pattern = Mix((ulong)page, unchecked((ulong)w.Seed));
                for (int i = 0; i + 8 <= block.Length && !w.Stage.Token.IsCancellationRequested; i += 8)
                    WriteWord(block, i, pattern ^ (ulong)i);

                for (int i = 0; i + 8 <= block.Length && !w.Stage.Token.IsCancellationRequested; i += 8)
                {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
                    checks++;
                    ulong expected = pattern ^ (ulong)i;
                    if (ReadWord(block, i) != expected)
                    {
                        errors++;
                        if (errors <= MaxSampleErrors)
                            w.Check("0x" + expected.ToString("X16"), "0x" + ReadWord(block, i).ToString("X16"));
                    }
                }

                bytes += 8192;
            }
        }

        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Bytes = bytes,
            Operations = checks,
            Expected = "шаблон сохраняется на границах страниц",
            Actual = errors == 0 ? "границы пройдены" : "расхождения на границах страниц",
        };
    }

    private static StageOutcome MemCacheLineBurst(WorkerView w)
    {
        long errors = 0;
        long checks = 0;
        double bytes = 0;
        const int Burst = 512; // 8 строк кэша по 64 байта

        foreach (byte[] chunk in Chunks(w))
        {
            Span<byte> span = chunk.AsSpan();
            for (int offset = 0; offset + Burst <= span.Length && !w.Stop && !w.Stage.Token.IsCancellationRequested; offset += Burst)
            {
                if ((offset & 16383) == 0) w.ThrowIfCancellationRequested();
                ulong pattern = Mix((ulong)offset, unchecked((ulong)w.Seed));
                for (int line = 0; line < Burst && !w.Stage.Token.IsCancellationRequested; line += 64)
                    for (int i = 0; i + 8 <= 64 && !w.Stage.Token.IsCancellationRequested; i += 8)
                        WriteWord(span, offset + line + i, pattern + (ulong)line);

                for (int line = 0; line < Burst && !w.Stage.Token.IsCancellationRequested; line += 64)
                    for (int i = 0; i + 8 <= 64 && !w.Stage.Token.IsCancellationRequested; i += 8)
                    {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
                        checks++;
                        ulong expected = pattern + (ulong)line;
                        if (ReadWord(span, offset + line + i) != expected)
                        {
                            errors++;
                            if (errors <= MaxSampleErrors)
                                w.Check("0x" + expected.ToString("X16"), "0x" + ReadWord(span, offset + line + i).ToString("X16"));
                        }
                    }

                bytes += Burst * 2.0;
            }
        }

        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Bytes = bytes,
            Operations = checks,
            Expected = "пакеты по 8 строк кэша сохраняются",
            Actual = errors == 0 ? "пакеты целы" : "повреждение внутри пакета",
        };
    }

    /// <summary>
    /// Полная инверсия: буфер заполняется известным шаблоном, все байты инвертируются,
    /// затем сверяется, что получилось побитовое дополнение исходного шаблона.
    /// </summary>
    private static StageOutcome MemInversionFull(WorkerView w)
    {
        long errors = 0;
        long checks = 0;
        double bytes = 0;
        long blockOffset = 0;
        foreach (byte[] chunk in Chunks(w))
        {
            if (w.Stop)
                break;

            Span<byte> span = chunk.AsSpan();
            ulong seed = unchecked((ulong)w.Seed + (ulong)w.Stage.Stage);

            // Исходный шаблон и ожидаемое дополнение считаются заранее и независимо.
            ulong[] source = new ulong[(span.Length + 7) / 8];
            ulong[] expected = new ulong[source.Length];
            for (int index = 0; index < source.Length && !w.Stage.Token.IsCancellationRequested; index++)
            {
                if ((index & 16383) == 0) w.ThrowIfCancellationRequested();
                ulong value = Mix(unchecked((ulong)(blockOffset + (index * 8L))), seed);
                source[index] = value;
                expected[index] = ~value;
            }

            for (int index = 0; index < source.Length && (index * 8) + 8 <= span.Length && !w.Stage.Token.IsCancellationRequested; index++)
                WriteWord(span, index * 8, source[index]);

            for (int i = 0; i < span.Length && !w.Stage.Token.IsCancellationRequested; i++)
                span[i] = (byte)~span[i];

            for (int index = 0; index < source.Length && (index * 8) + 8 <= span.Length && !w.Stage.Token.IsCancellationRequested; index++)
            {
                if ((index & 16383) == 0) w.ThrowIfCancellationRequested();
                checks++;
                ulong actual = ReadWord(span, index * 8);
                if (actual != expected[index])
                {
                    errors++;
                    if (errors <= MaxSampleErrors)
                        w.Check("0x" + expected[index].ToString("X16"), "0x" + actual.ToString("X16"));
                }
            }

            blockOffset += span.Length;
            bytes += chunk.Length * 2.0;
        }

        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Bytes = bytes,
            Operations = checks,
            Expected = "инверсия байтов даёт 0x00",
            Actual = errors == 0 ? "инверсия корректна" : "ошибки инверсии",
        };
    }

    private static IEnumerable<byte[]> Chunks(WorkerView w) => w.Stage.Pool.Chunks;
}
