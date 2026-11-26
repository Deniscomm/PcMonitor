using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PcMonitor.Core.Stress;

/// <summary>
/// Тесты накопителя: последовательная и произвольная запись/чтение, смешанный режим,
/// проверка целостности записанного, сброс кэшей (FlushFileBuffers) и большие блоки.
/// </summary>
/// <remarks>
/// Все файлы создаются в папке <see cref="StressOptions.DiskPath"/> (или во временной),
/// их размер жёстко ограничен <see cref="StressOptions.DiskBytes"/>, а после теста файл удаляется.
/// Если места на диске меньше запрошенного объёма, размер файла уменьшается — это попадает в отчёт.
/// </remarks>
internal static class DiskStressTests
{
    private const int BufferSize = 4 * 1024 * 1024;
    private const int RandomBlockSize = 4096;
    private const int VerifyBlockSize = 4096;
    private const long WriteThroughLimit = 256L * 1024 * 1024;

    /// <summary>Собирает список тестов диска.</summary>
    internal static IReadOnlyList<IStressTest> Build()
    {
        const StressCategory C = StressCategory.Disk;
        return new List<IStressTest>
        {
            StressTestFactory.Create("disk.seq-write", C,
                "Последовательная запись",
                "Потоковая запись шаблонных блоков с периодической проверкой прочитанного.",
                TimeSpan.FromMilliseconds(600), c => SequentialWrite(c, writeThrough: false)),

            StressTestFactory.Create("disk.seq-write-through", C,
                "Последовательная запись без кэша ОС",
                "То же самое, но файл открыт с FileOptions.WriteThrough — обход кэша операционной системы.",
                TimeSpan.FromMilliseconds(600), c => SequentialWrite(c, writeThrough: true)),

            StressTestFactory.Create("disk.seq-read", C,
                "Последовательное чтение",
                "Потоковое чтение заранее подготовленного файла с вычислением контрольной суммы.",
                TimeSpan.FromMilliseconds(600), c => SequentialRead(c)),

            StressTestFactory.Create("disk.random-read-4k", C,
                "Произвольное чтение блоками 4 КБ",
                "Случайные смещения по всему файлу блоками по 4096 байт.",
                TimeSpan.FromMilliseconds(600), c => RandomRead(c)),

            StressTestFactory.Create("disk.random-write-4k", C,
                "Произвольная запись блоками 4 КБ",
                "Случайные блоки по 4096 байт записываются со своим шаблоном и сразу проверяются.",
                TimeSpan.FromMilliseconds(600), c => RandomWrite(c)),

            StressTestFactory.Create("disk.mixed-rw", C,
                "Смешанная нагрузка чтение/запись",
                "Чередование чтения и записи по случайным блокам — типичная нагрузка рабочей станции.",
                TimeSpan.FromMilliseconds(600), c => MixedReadWrite(c)),

            StressTestFactory.Create("disk.integrity", C,
                "Целостность записанных данных",
                "Запись файла, полная проверка байт за байтом и сверка CRC32 всего файла.",
                TimeSpan.FromMilliseconds(900), c => Integrity(c)),

            StressTestFactory.Create("disk.flush-heavy", C,
                "Частые сбросы на диск (fsync)",
                "После каждого блока вызывается FileStream.Flush(true) — проверка честного сброса.",
                TimeSpan.FromMilliseconds(900), c => FlushHeavy(c)),

            StressTestFactory.Create("disk.append-only", C,
                "Только добавление в конец файла",
                "Файл дописывается блоками, проверяется рост размера и корректность «хвоста».",
                TimeSpan.FromMilliseconds(600), c => AppendOnly(c)),

            StressTestFactory.Create("disk.read-write-alternate", C,
                "Чередование чтения и записи (последовательное)",
                "Поочерёдные проходы: запись блока, чтение того же блока, сдвиг вперёд.",
                TimeSpan.FromMilliseconds(600), c => ReadWriteAlternate(c)),

            StressTestFactory.Create("disk.large-blocks", C,
                "Большие блоки (1 МБ и 4 МБ)",
                "Запись и чтение очень крупными блоками — предельная пропускная способность накопителя.",
                TimeSpan.FromMilliseconds(900), c => LargeBlocks(c)),
        };
    }

    // ---------------------------------------------------------------- вспомогательные средства

    /// <summary>Определяет размер файла: не больше запрошенного и не больше свободного места на диске.</summary>
    internal static long ResolveFileSize(StressOptions options, out string note)
    {
        long requested = options.EffectiveDiskBytes;
        long available = long.MaxValue;
        try
        {
            string directory = options.DiskDirectory;
            Directory.CreateDirectory(directory);
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory)) ?? directory);
            available = Math.Max(16L * 1024 * 1024, drive.AvailableFreeSpace / 8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            note = "не удалось определить свободное место, используется запрошенный размер";
            return requested;
        }

        if (requested <= available)
        {
            note = string.Empty;
            return requested;
        }

        note = $"объём уменьшен с {requested / (1024 * 1024)} МБ до {available / (1024 * 1024)} МБ (мало свободного места)";
        return Math.Max(4L * 1024 * 1024, available);
    }

    /// <summary>
    /// Подбирает размер файла так, чтобы запись (самая медленная операция тестов диска)
    /// успела завершиться внутри этапа. Скорость записи на разных накопителях отличается
    /// в разы, поэтому берётся осторожная оценка 150 МБ/с.
    /// </summary>
    /// <param name="w">Контекст рабочего потока.</param>
    /// <param name="budgetFraction">Какая доля этапа отводится подготовке файла (0..1).</param>
    private static long PreparedFileSize(WorkerView w, double budgetFraction)
    {
        long requested = ResolveFileSize(w.Stage.Options, out _);
        if (w.Stage.Options.RunUntilStopped) return requested;
        double budgetMilliseconds = w.Stage.StageDuration.TotalMilliseconds * budgetFraction;

        // 150 МБ/с ≈ 0.15 МБ за миллисекунду.
        long estimated = (long)(Math.Max(20, budgetMilliseconds) * 0.15 * 1024 * 1024);
        return Math.Clamp(Math.Min(requested, estimated), 4L * 1024 * 1024, requested);
    }

    /// <summary>Создаёт временный файл теста с указанными параметрами открытия.</summary>
    private static FileStream CreateStream(WorkerView w, string tag, FileOptions extra = FileOptions.None, long? sizeOverride = null)
    {
        string note = string.Empty;
        long size = sizeOverride ?? ResolveFileSize(w.Stage.Options, out note);
        string path = Path.Combine(
            w.Stage.Options.DiskDirectory,
            $"pcmon-stress-{tag}-{w.Stage.Stage}-{Environment.ProcessId}-{w.ThreadIndex}.tmp");

        FileOptions options = (extra & FileOptions.WriteThrough) != 0 && size <= WriteThroughLimit
            ? FileOptions.WriteThrough
            : FileOptions.None;

        var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 64 * 1024, options);
        w.Stage.TrackFile(path);
        try { stream.SetLength(size); }
        catch { stream.Dispose(); throw; }
        if (sizeOverride is null && note.Length > 0)
            w.Check("размер файла теста", note);

        return stream;
    }

    /// <summary>Заполняет буфер шаблоном, зависящим от смещения (повторяется каждые 4096 байт).</summary>
    private static byte[] CreatePatternBuffer(int size, int seed)
    {
        var buffer = new byte[size];
        FillPattern(buffer, 0, seed);
        return buffer;
    }

    /// <summary>Записывает шаблон, зависящий от локального смещения, — такую последовательность легко проверить.</summary>
    private static void FillPattern(Span<byte> destination, long offset, int seed)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            long absolute = offset + i;
            byte value = (byte)(absolute * 31 + seed);
            if ((absolute & 63) == 0)
                value ^= 0xA5;
            else if ((absolute & 255) == 0)
                value = 0x5A;

            destination[i] = value;
        }
    }

    /// <summary>Проверяет, что прочитанный блок совпадает с шаблоном для указанного смещения.</summary>
    private static bool VerifyPattern(ReadOnlySpan<byte> actual, long offset, int seed, out int mismatchOffset, out byte expected, out byte got)
    {
        for (int i = 0; i < actual.Length; i++)
        {
            long absolute = offset + i;
            byte value = (byte)(absolute * 31 + seed);
            if ((absolute & 63) == 0)
                value ^= 0xA5;
            else if ((absolute & 255) == 0)
                value = 0x5A;

            if (actual[i] != value)
            {
                mismatchOffset = i;
                expected = value;
                got = actual[i];
                return false;
            }
        }

        mismatchOffset = 0;
        expected = 0;
        got = 0;
        return true;
    }

    /// <summary>Читает блок по указанному смещению, при необходимости — вне кэша ОС.</summary>
    private static void ReadBlock(FileStream stream, SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        if (OperatingSystem.IsWindows() && offset + buffer.Length <= stream.Length)
        {
            int read = RandomAccess.Read(handle, buffer, offset);
            if (read == buffer.Length)
                return;
        }

        stream.Position = offset;
        stream.ReadExactly(buffer);
    }

    /// <summary>Перемешанный массив номеров блоков (перестановка Фишера — Йетса).</summary>
    private static int[] ShuffledBlocks(int count, Random random)
    {
        var order = new int[Math.Max(0, count)];
        for (int i = 0; i < order.Length; i++)
            order[i] = i;

        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        return order;
    }

    /// <summary>CRC32 для проверки целостности файла.</summary>
    private static uint ComputeCrc(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>Обход того же ограниченного файла; в бессрочном режиме смещение возвращается к нулю.</summary>
    private static IEnumerable<long> BlockOffsets(WorkerView w, long size, int blockSize)
    {
        do
        {
            for (long offset = 0; offset < size && !w.Stop; offset += blockSize)
                yield return offset;
        } while (w.Stage.Options.RunUntilStopped && size > 0 && !w.Stop);
    }

    // ---------------------------------------------------------------- последовательные тесты

    private static StageOutcome SequentialWrite(WorkerView w, bool writeThrough)
    {
        long size = PreparedFileSize(w, 0.8);
        using FileStream stream = CreateStream(w, writeThrough ? "wt" : "seqw", writeThrough ? FileOptions.WriteThrough : FileOptions.None, size);
        SafeFileHandle handle = stream.SafeFileHandle;
        int seed = w.Stage.Options.Seed ^ w.Stage.Stage;
        byte[] buffer = CreatePatternBuffer(BufferSize, seed);
        long errors = 0;
        long completed = 0;
        long started = Stopwatch.GetTimestamp();

        foreach (long offset in BlockOffsets(w, size, BufferSize))
        {
            int length = (int)Math.Min(BufferSize, size - offset);
            Span<byte> block = buffer.AsSpan(0, length);
            FillPattern(block, offset, seed);
            stream.Position = offset;
            stream.Write(block);
            completed += length;
            w.RecordWork(length, 1);

            if ((offset & 0xFFFFF) == 0 && offset > 0)
            {
                // Периодически проверяем то, что уже записано: запись обязана быть видна при чтении.
                int verifyLength = Math.Min(VerifyBlockSize, length);
                byte[] verify = new byte[verifyLength];
                ReadBlock(stream, handle, verify, offset);
                if (!VerifyPattern(verify, offset, seed, out _, out byte expected, out byte got))
                {
                    errors++;
                    w.Check("0x" + expected.ToString("X2"), "0x" + got.ToString("X2"));
                }
            }
        }

        if (writeThrough)
            stream.Flush(true);

        double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        w.Check("файл записан целиком", $"{completed / (1024 * 1024)} МБ за {seconds:0.###} с");
        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Bytes = completed,
            Operations = size / VerifyBlockSize,
            Expected = $"запись {size / (1024 * 1024)} МБ {(writeThrough ? "без кэша ОС" : "последовательно")}",
            Actual = errors == 0 ? "данные записаны и выборочно проверены" : "расхождение при проверке",
        };
    }

    private static StageOutcome SequentialRead(WorkerView w)
    {
        long size = PreparedFileSize(w, 0.35);
        using FileStream stream = CreateStream(w, "seqr", FileOptions.None, size);
        SafeFileHandle handle = stream.SafeFileHandle;
        int seed = w.Stage.Options.Seed ^ 0x1234;
        byte[] buffer = new byte[BufferSize];
        long errors = 0;
        long prepared = 0;

        // Готовим файл (в отчёт попадает только чтение).
        for (long offset = 0; offset < size && !w.Stop; offset += BufferSize)
        {
            int length = (int)Math.Min(BufferSize, size - offset);
            FillPattern(buffer.AsSpan(0, length), offset, seed);
            stream.Position = offset;
            stream.Write(buffer, 0, length);
            prepared += length;
        }

        stream.Flush();

        // Измеряем только чтение: таймер стартует после подготовки файла.
        long completed = 0;
        long started = Stopwatch.GetTimestamp();
        foreach (long offset in BlockOffsets(w, prepared, BufferSize))
        {
            int length = (int)Math.Min(BufferSize, prepared - offset);
            ReadBlock(stream, handle, buffer.AsSpan(0, length), offset);
            completed += length;
            w.RecordWork(length, 1);
            if (!VerifyPattern(buffer.AsSpan(0, length), offset, seed, out _, out byte expected, out byte got))
            {
                errors++;
                w.Check("0x" + expected.ToString("X2"), "0x" + got.ToString("X2"));
            }
        }

        double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        w.Check("файл прочитан и сверен", $"{completed / (1024 * 1024)} МБ из {prepared / (1024 * 1024)} МБ за {seconds:0.###} с");
        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Bytes = completed,
            Operations = completed / BufferSize,
            Expected = $"чтение {prepared / (1024 * 1024)} МБ последовательно",
            Actual = errors == 0 ? "контрольные суммы совпали" : "обнаружены расхождения",
        };
    }

    // ---------------------------------------------------------------- произвольный доступ

    private static StageOutcome RandomRead(WorkerView w)
    {
        long size = PreparedFileSize(w, 0.3);
        using FileStream stream = CreateStream(w, "randr", FileOptions.None, size);
        SafeFileHandle handle = stream.SafeFileHandle;
        int seed = w.Stage.Options.Seed ^ 0x4321;
        int blocks = (int)Math.Max(1, size / RandomBlockSize);
        byte[] buffer = CreatePatternBuffer(RandomBlockSize, seed);

        long prepared = 0;
        for (long offset = 0; offset < size && !w.Stop; offset += RandomBlockSize)
        {
            int length = (int)Math.Min(RandomBlockSize, size - offset);
            FillPattern(buffer.AsSpan(0, length), offset, seed);
            stream.Position = offset;
            stream.Write(buffer, 0, length);
            prepared += length;
        }

        stream.Flush();
        Random random = w.Random;
        long errors = 0;
        long completed = 0;
        byte[] verify = new byte[RandomBlockSize];
        long started = Stopwatch.GetTimestamp();

        // Читаем случайные блоки, пока хватает времени этапа.
        while (!w.Stop)
        {
            int block = random.Next(blocks);
            long offset = (long)block * RandomBlockSize;
            int length = (int)Math.Min(RandomBlockSize, prepared - offset);
            if (length <= 0)
                continue;

            ReadBlock(stream, handle, verify.AsSpan(0, length), offset);
            completed += length;
            w.RecordWork(length, 1);
            if (!VerifyPattern(verify.AsSpan(0, length), offset, seed, out _, out byte expected, out byte got))
            {
                errors++;
                w.Check("0x" + expected.ToString("X2"), "0x" + got.ToString("X2"));
            }
        }

        double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        w.Check("произвольное чтение завершено", $"{completed / (1024 * 1024)} МБ за {seconds:0.###} с");
        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Bytes = completed,
            Operations = completed / RandomBlockSize,
            Expected = $"произвольное чтение блоками {RandomBlockSize} Б по файлу {prepared / (1024 * 1024)} МБ",
            Actual = errors == 0 ? "все блоки совпали с шаблоном" : "расхождения в блоках",
        };
    }

    private static StageOutcome RandomWrite(WorkerView w)
    {
        long size = PreparedFileSize(w, 0.0);
        using FileStream stream = CreateStream(w, "randw", FileOptions.WriteThrough, size);
        SafeFileHandle handle = stream.SafeFileHandle;
        Random random = w.Random;
        long errors = 0;
        long written = 0;
        long blocks = Math.Max(1, size / RandomBlockSize);
        byte[] buffer = new byte[RandomBlockSize];
        byte[] verify = new byte[RandomBlockSize];
        long started = Stopwatch.GetTimestamp();

        // Случайные записи по всему файлу, пока хватает времени этапа.
        while (!w.Stop)
        {
            long block = random.NextInt64(blocks);
            long offset = block * RandomBlockSize;
            int length = (int)Math.Min(RandomBlockSize, size - offset);
            if (length <= 0)
                continue;

            Span<byte> span = buffer.AsSpan(0, length);
            for (int j = 0; j < length; j++)
                span[j] = (byte)((block * 131) + j + w.Stage.Stage);

            RandomAccess.Write(handle, span, offset);
            written += length;
            w.RecordWork(length, 1);
            if (w.Stage.Options.RunUntilStopped)
            {
                ReadBlock(stream, handle, verify.AsSpan(0, length), offset);
                if (!verify.AsSpan(0, length).SequenceEqual(span))
                    w.Fail("записанный блок", "расхождение после записи");
            }
        }

        double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;

        // Выборочно проверяем часть записанных блоков — данные обязаны читаться как записаны.
        int probes = 0;
        var checkRandom = new Random(w.Seed ^ 0x5A5A);
        while (probes < 32 && !w.Stop)
        {
            long block = checkRandom.NextInt64(blocks);
            long offset = block * RandomBlockSize;
            int length = (int)Math.Min(RandomBlockSize, size - offset);
            if (length <= 0)
                continue;

            ReadBlock(stream, handle, verify.AsSpan(0, length), offset);
            for (int j = 0; j < length; j++)
            {
                byte expected = (byte)((block * 131) + j + w.Stage.Stage);
                if (verify[j] != expected)
                {
                    errors++;
                    w.Check("0x" + expected.ToString("X2"), "0x" + verify[j].ToString("X2"));
                    break;
                }
            }

            probes++;
        }

        w.Check("произвольная запись завершена", $"{written / (1024 * 1024)} МБ за {seconds:0.###} с, проверено блоков: {probes}");
        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Bytes = written,
            Operations = written / RandomBlockSize,
            Expected = $"произвольная запись блоками {RandomBlockSize} Б без кэша ОС",
            Actual = errors == 0 ? $"записано и выборочно проверено {probes} блоков" : "расхождения после записи",
        };
    }

    private static StageOutcome MixedReadWrite(WorkerView w)
    {
        long size = PreparedFileSize(w, 0.3);
        using FileStream stream = CreateStream(w, "mixed", FileOptions.None, size);
        SafeFileHandle handle = stream.SafeFileHandle;
        int seed = w.Stage.Options.Seed ^ 0x7777;
        int blocks = (int)Math.Max(1, size / RandomBlockSize);
        byte[] buffer = CreatePatternBuffer(RandomBlockSize, seed);

        long prepared = 0;
        for (long offset = 0; offset < size && !w.Stop; offset += RandomBlockSize)
        {
            int length = (int)Math.Min(RandomBlockSize, size - offset);
            FillPattern(buffer.AsSpan(0, length), offset, seed);
            stream.Position = offset;
            stream.Write(buffer, 0, length);
            prepared += length;
        }

        stream.Flush();
        Random random = w.Random;
        long errors = 0;
        long readBytes = 0;
        long written = 0;
        long reads = 0;
        long writes = 0;
        byte[] block = new byte[RandomBlockSize];
        byte[] writeBuffer = new byte[RandomBlockSize];
        long started = Stopwatch.GetTimestamp();

        // Блоки заранее разделены на «читаемые» и «перезаписываемые»: чтение всегда видит
        // исходный шаблон, потому что записи идут в другой, непересекающийся набор блоков.
        int[] order = ShuffledBlocks(blocks, random);
        do
        {
        for (int i = 0; i < order.Length && !w.Stop; i++)
        {
            long offset = (long)order[i] * RandomBlockSize;
            int length = (int)Math.Min(RandomBlockSize, prepared - offset);
            if (length <= 0)
                continue;

            bool read = i < order.Length / 2;
            if (read)
            {
                ReadBlock(stream, handle, block.AsSpan(0, length), offset);
                readBytes += length;
                w.RecordWork(length, 1);
                reads++;
                if (!VerifyPattern(block.AsSpan(0, length), offset, seed, out _, out byte expected, out byte got))
                {
                    errors++;
                    w.Check("0x" + expected.ToString("X2"), "0x" + got.ToString("X2"));
                }
            }
            else
            {
                Span<byte> span = writeBuffer.AsSpan(0, length);
                for (int j = 0; j < length; j++)
                    span[j] = (byte)((order[i] * 37) + j + w.Stage.Stage);

                RandomAccess.Write(handle, span, offset);
                written += length;
                w.RecordWork(0, 1);
                writes++;
            }
        }

        } while (w.Stage.Options.RunUntilStopped && !w.Stop);
        double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        w.Check("смешанная нагрузка завершена",
            $"чтений {reads} ({readBytes / (1024 * 1024)} МБ), записей {writes} ({written / (1024 * 1024)} МБ), за {seconds:0.###} с");
        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Bytes = readBytes,
            Operations = reads + writes,
            Expected = "чередование чтения и записи по случайным блокам 4 КБ",
            Actual = errors == 0 ? $"чтений {reads}, записей {writes}" : "расхождения при чтении",
        };
    }

    // ---------------------------------------------------------------- целостность и сбросы

    private static StageOutcome Integrity(WorkerView w)
    {
        long size = PreparedFileSize(w, 0.5);
        using FileStream stream = CreateStream(w, "integrity", FileOptions.None, size);
        int seed = w.Stage.Options.Seed ^ 0x2468;
        byte[] buffer = new byte[BufferSize];
        long totalBytes = 0, totalOperations = 0;
        StageOutcome? last = null;
        do
        {
        stream.Position = 0;
        uint crc = 0xFFFFFFFFu;

        long written = 0;
        for (long offset = 0; offset < size && !w.Stop; offset += BufferSize)
        {
            int length = (int)Math.Min(BufferSize, size - offset);
            Span<byte> span = buffer.AsSpan(0, length);
            FillPattern(span, offset, seed);
            stream.Write(span);
            written += length;

            // Контрольная сумма считается по тем же данным, что ушли на диск.
            foreach (byte b in span)
                crc = CrcTableStep(crc, b);
        }

        stream.Flush(true);
        uint expectedCrc = crc ^ 0xFFFFFFFFu;

        // Читаем файл заново и считаем CRC32 прочитанного.
        stream.Position = 0;
        uint actualCrc = 0xFFFFFFFFu;
        long readTotal = 0;
        while (readTotal < written && !w.Stop)
        {
            int length = (int)Math.Min(BufferSize, written - readTotal);
            stream.ReadExactly(buffer, 0, length);
            foreach (byte b in buffer.AsSpan(0, length))
                actualCrc = CrcTableStep(actualCrc, b);

            readTotal += length;
        }

        actualCrc ^= 0xFFFFFFFFu;
        bool complete = written == size && readTotal == written;
        long errors = 0;
        if (!complete)
        {
            // Этап закончился раньше времени: сравнивать хеши неполного файла нельзя.
            w.Check("полный цикл запись-чтение", $"успело записаться и прочитаться {readTotal / (1024 * 1024)} МБ из {size / (1024 * 1024)} МБ");
        }
        else if (actualCrc != expectedCrc)
        {
            errors++;
            w.Check("CRC32 файла 0x" + expectedCrc.ToString("X8"), "0x" + actualCrc.ToString("X8"));
            w.FailFast();
        }
        else
        {
            w.Check("CRC32 файла 0x" + expectedCrc.ToString("X8"), "совпадает");
        }

        totalBytes += readTotal * 2;
        w.RecordWork(readTotal * 2, readTotal / BufferSize);
        totalOperations += readTotal / BufferSize;
        last = new StageOutcome
        {
            Bytes = readTotal * 2,
            Operations = readTotal / BufferSize,
            Expected = $"CRC32 записанного файла: 0x{expectedCrc:X8}",
            Actual = !complete
                ? "этап завершился до конца цикла, хеши не сравнивались"
                : errors == 0 ? "CRC32 прочитанного файла совпала" : "контрольные суммы различаются",
        };
        } while (w.Stage.Options.RunUntilStopped && !w.Stop);
        return new StageOutcome { Bytes = totalBytes, Operations = totalOperations,
            Expected = last?.Expected ?? "CRC32 файла", Actual = last?.Actual ?? "остановлено" };
    }

    private static StageOutcome FlushHeavy(WorkerView w)
    {
        long size = PreparedFileSize(w, 0.7);
        using FileStream stream = CreateStream(w, "flush", FileOptions.WriteThrough, size);
        int seed = w.Stage.Options.Seed ^ 0x1357;
        byte[] buffer = new byte[BufferSize];

        long errors = 0;
        long completed = 0;
        int flushes = 0;
        long started = Stopwatch.GetTimestamp();

        foreach (long offset in BlockOffsets(w, size, BufferSize))
        {
            int length = (int)Math.Min(BufferSize, size - offset);
            FillPattern(buffer.AsSpan(0, length), offset, seed);
            stream.Position = offset;
            stream.Write(buffer, 0, length);

            // Честный сброс буферов файловой системы на устройство.
            stream.Flush(true);
            flushes++;
            completed += length;
            w.RecordWork(length, 1);

            if ((flushes & 1) == 0)
            {
                byte[] verify = new byte[VerifyBlockSize];
                ReadBlock(stream, stream.SafeFileHandle, verify, offset);
                if (!VerifyPattern(verify, offset, seed, out _, out byte expected, out byte got))
                {
                    errors++;
                    w.Check("0x" + expected.ToString("X2"), "0x" + got.ToString("X2"));
                }
            }
        }

        double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        w.Check($"FlushFileBuffers вызван {flushes} раз", $"{completed / (1024 * 1024)} МБ за {seconds:0.###} с");
        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Bytes = completed,
            Operations = flushes,
            Expected = "каждый блок сброшен на устройство вызовом Flush(true)",
            Actual = errors == 0 ? $"{flushes} сбросов, данные целы" : "данные повреждены после сброса",
        };
    }

    private static StageOutcome AppendOnly(WorkerView w)
    {
        using FileStream stream = CreateStream(w, "append");
        stream.SetLength(0);
        int seed = w.Stage.Options.Seed ^ 0x1111;
        long limit = ResolveFileSize(w.Stage.Options, out _);
        byte[] buffer = new byte[BufferSize];
        long errors = 0;
        long appended = 0;
        int flushes = 0;
        long started = Stopwatch.GetTimestamp();

        long totalAppended = 0;
        while ((appended < limit || w.Stage.Options.RunUntilStopped) && !w.Stop)
        {
            if (appended >= limit)
            {
                stream.SetLength(0);
                appended = 0;
            }
            int length = (int)Math.Min(BufferSize, limit - appended);
            long offset = appended;
            Span<byte> span = buffer.AsSpan(0, length);
            FillPattern(span, offset, seed);

            stream.Seek(0, SeekOrigin.End);
            stream.Write(span);
            appended += length;
            totalAppended += length;
            w.RecordWork(length * 2, 1);

            // Проверяем «хвост» файла: последние записанные 4 КБ.
            if (length >= VerifyBlockSize && (appended & 0x3FFFFF) == 0)
            {
                flushes++;
                byte[] verify = new byte[VerifyBlockSize];
                long tailOffset = appended - VerifyBlockSize;
                ReadBlock(stream, stream.SafeFileHandle, verify, tailOffset);
                if (!VerifyPattern(verify, tailOffset, seed, out _, out byte expected, out byte got))
                {
                    errors++;
                    w.Check("0x" + expected.ToString("X2"), "0x" + got.ToString("X2"));
                }
            }
        }

        if (stream.Length != appended)
        {
            errors++;
            w.Check($"длина файла {appended}", $"фактически {stream.Length}");
        }

        double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        w.Check("файл дописан", $"итоговый размер {stream.Length / (1024 * 1024)} МБ, проверок хвоста: {flushes}");
        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Bytes = totalAppended * 2,
            Operations = totalAppended / BufferSize,
            Expected = "файл растёт только в конец, хвост совпадает с шаблоном",
            Actual = errors == 0 ? "добавление корректно" : "ошибка при добавлении",
        };
    }

    private static StageOutcome ReadWriteAlternate(WorkerView w)
    {
        long size = PreparedFileSize(w, 0.8);
        using FileStream stream = CreateStream(w, "alternate", FileOptions.None, size);
        SafeFileHandle handle = stream.SafeFileHandle;
        int seed = w.Stage.Options.Seed ^ 0x9999;
        int blockSize = 256 * 1024;
        byte[] buffer = new byte[blockSize];
        byte[] verify = new byte[blockSize];
        long errors = 0;
        long bytes = 0;
        int cycles = 0;
        long started = Stopwatch.GetTimestamp();

        foreach (long offset in BlockOffsets(w, size, blockSize))
        {
            int length = (int)Math.Min(blockSize, size - offset);
            FillPattern(buffer.AsSpan(0, length), offset, seed);
            RandomAccess.Write(handle, buffer.AsSpan(0, length), offset);
            bytes += length;

            ReadBlock(stream, handle, verify.AsSpan(0, length), offset);
            bytes += length;
            cycles++;
            w.RecordWork(length * 2, 1);
            if (!VerifyPattern(verify.AsSpan(0, length), offset, seed, out _, out byte expected, out byte got))
            {
                errors++;
                w.Check("0x" + expected.ToString("X2"), "0x" + got.ToString("X2"));
            }
        }

        double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        w.Check("циклы запись-чтение завершены", $"{cycles} циклов за {seconds:0.###} с");
        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Bytes = bytes,
            Operations = cycles,
            Expected = "после каждой записи блок читается и сверяется",
            Actual = errors == 0 ? "все блоки совпали" : "блоки не совпали",
        };
    }

    private static StageOutcome LargeBlocks(WorkerView w)
    {
        long size = PreparedFileSize(w, 0.8);
        using FileStream stream = CreateStream(w, "large", FileOptions.None, size);
        SafeFileHandle handle = stream.SafeFileHandle;
        int seed = w.Stage.Options.Seed ^ 0x2222;
        int blockSize = 1024 * 1024;
        byte[] buffer = new byte[blockSize];
        byte[] verify = new byte[blockSize];
        long errors = 0;
        long written = 0;
        long read = 0;
        double writeSeconds = 0, readSeconds = 0;

        do
        {
        long cycleStarted = Stopwatch.GetTimestamp();
        // Запись крупными блоками.
        for (long offset = 0; offset < size && !w.Stop; offset += blockSize)
        {
            int length = (int)Math.Min(blockSize, size - offset);
            FillPattern(buffer.AsSpan(0, length), offset, seed);
            RandomAccess.Write(handle, buffer.AsSpan(0, length), offset);
            written += length;
            w.RecordWork(length, 1);
        }

        long writeTimestamp = Stopwatch.GetTimestamp();

        // Чтение крупными блоками с проверкой.
        for (long offset = 0; offset < size && !w.Stop; offset += blockSize)
        {
            int length = (int)Math.Min(blockSize, size - offset);
            ReadBlock(stream, handle, verify.AsSpan(0, length), offset);
            read += length;
            w.RecordWork(length, 1);
            if (!VerifyPattern(verify.AsSpan(0, length), offset, seed, out _, out byte expected, out byte got))
            {
                errors++;
                w.Check("0x" + expected.ToString("X2"), "0x" + got.ToString("X2"));
            }
        }

        writeSeconds += Stopwatch.GetElapsedTime(cycleStarted, writeTimestamp).TotalSeconds;
        readSeconds += Stopwatch.GetElapsedTime(writeTimestamp).TotalSeconds;
        } while (w.Stage.Options.RunUntilStopped && !w.Stop);
        

        w.Check("крупные блоки обработаны",
            $"запись {written / (1024 * 1024)} МБ за {writeSeconds:0.###} с, чтение {read / (1024 * 1024)} МБ за {readSeconds:0.###} с");
        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Bytes = written + read,
            Operations = (written + read) / blockSize,
            Expected = "блоки по 1 МБ записаны и прочитаны без расхождений",
            Actual = errors == 0 ? "все крупные блоки совпали" : "расхождения в крупных блоках",
        };
    }

    /// <summary>Один шаг CRC32 по таблице, вычисляемой «на лету» (экономит статическую таблицу).</summary>
    private static uint CrcTableStep(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
            crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;

        return crc;
    }
}
