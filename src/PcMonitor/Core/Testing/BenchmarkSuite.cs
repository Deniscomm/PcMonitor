using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace PcMonitor.Core;

/// <summary>Вид бенчмарка.</summary>
public enum BenchmarkKind
{
    CpuSingle,
    CpuMulti,
    MemoryBandwidth,
    MemoryLatency,
    DiskSequentialWrite,
    DiskSequentialRead,
    DiskRandomRead,
    DiskRandomWrite,
}

/// <summary>Результат одного бенчмарка.</summary>
public sealed record BenchmarkResult(
    BenchmarkKind Kind,
    string Name,
    double Value,
    string Unit,
    int Score,
    TimeSpan Duration,
    string Details)
{
    public string ValueText => Value >= 100 ? $"{Value:0}" : Value >= 10 ? $"{Value:0.0}" : $"{Value:0.00}";
    public string DisplayName => LocalizationManager.T(Name);
    public string DisplayDetails => LocalizationManager.T(Details);
    public string StartedText { get; init; } = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");
}

/// <summary>Набор встроенных бенчмарков: процессор, память, диск.</summary>
public static class BenchmarkSuite
{
    // ============================ Процессор ============================

    /// <summary>Целочисленная и плавающая нагрузка на одном потоке.</summary>
    public static BenchmarkResult CpuSingle(TimeSpan duration, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        long operations = 0;
        double accumulator = 0;
        ulong hash = 1469598103934665603;

        while (stopwatch.Elapsed < duration && !token.IsCancellationRequested)
        {
            var batch = Stopwatch.StartNew();
            long local = 0;
            while (batch.ElapsedMilliseconds < 250 && !token.IsCancellationRequested)
            {
                for (int i = 0; i < 2_000_000; i++)
                {
                    hash = (hash ^ (ulong)i) * 1099511628211;
                    accumulator += Math.Sqrt(i + 1.0) * 0.0000001;
                }

                local += 2_000_000;
            }

            operations += local;
        }

        double seconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        double mops = operations / seconds / 1_000_000;
        double checksum = accumulator + (hash & 0xFFFF);
        return new BenchmarkResult(
            BenchmarkKind.CpuSingle,
            "Процессор, один поток",
            mops,
            "млн оп/с",
            (int)Math.Round(mops * 10),
            stopwatch.Elapsed,
            $"контрольная сумма {checksum:0.###}");
    }

    /// <summary>Та же нагрузка, но во всех потоках (аналог Cinebench multi).</summary>
    public static BenchmarkResult CpuMulti(TimeSpan duration, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        int threads = Environment.ProcessorCount;
        long[] perThread = new long[threads];
        double[] checksums = new double[threads];

        Parallel.For(0, threads, new ParallelOptions { CancellationToken = token }, index =>
        {
            ulong hash = 1469598103934665603UL + (ulong)index * 7919;
            double accumulator = 0;
            long operations = 0;
            var local = Stopwatch.StartNew();

            while (local.Elapsed < duration && !token.IsCancellationRequested)
            {
                var batch = Stopwatch.StartNew();
                while (batch.ElapsedMilliseconds < 250 && !token.IsCancellationRequested)
                {
                    for (int i = 0; i < 1_000_000; i++)
                    {
                        hash = (hash ^ (ulong)i) * 1099511628211;
                        accumulator += Math.Sqrt(i + 1.0) * 0.0000001;
                    }

                    operations += 1_000_000;
                }
            }

            perThread[index] = operations;
            checksums[index] = accumulator + (hash & 0xFFFF);
        });

        double seconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        double mops = perThread.Sum() / seconds / 1_000_000;
        return new BenchmarkResult(
            BenchmarkKind.CpuMulti,
            "Процессор, все потоки",
            mops,
            "млн оп/с",
            (int)Math.Round(mops * 10),
            stopwatch.Elapsed,
            $"потоков {threads}, контрольная сумма {checksums.Sum():0.###}");
    }

    // ============================ Память ============================

    /// <summary>Пропускная способность памяти (тест вида STREAM: копирование, масштабирование, сумма).</summary>
    public static BenchmarkResult MemoryBandwidth(int megabytes, TimeSpan duration, CancellationToken token)
    {
        int elements = Math.Max(1, megabytes) * 1024 * 1024 / sizeof(double);
        double[] a = new double[elements];
        double[] b = new double[elements];
        double[] c = new double[elements];

        for (int i = 0; i < elements; i++)
        {
            a[i] = i * 0.5;
            b[i] = i * 0.25;
            c[i] = 0;
        }

        var stopwatch = Stopwatch.StartNew();
        long bytesMoved = 0;
        double scalar = 1.0000001;

        while (stopwatch.Elapsed < duration && !token.IsCancellationRequested)
        {
            // Copy: c = a
            Array.Copy(a, c, elements);
            bytesMoved += (long)elements * sizeof(double) * 2;

            if (stopwatch.Elapsed >= duration || token.IsCancellationRequested) break;

            // Scale: c = scalar * a
            for (int i = 0; i < elements; i++) c[i] = a[i] * scalar;
            bytesMoved += (long)elements * sizeof(double) * 2;

            if (stopwatch.Elapsed >= duration || token.IsCancellationRequested) break;

            // Add: c = a + b
            for (int i = 0; i < elements; i++) c[i] = a[i] + b[i];
            bytesMoved += (long)elements * sizeof(double) * 3;
        }

        double seconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        double gbPerSecond = bytesMoved / seconds / 1024 / 1024 / 1024;
        double checksum = c[0] + c[elements / 2] + c[^1];

        return new BenchmarkResult(
            BenchmarkKind.MemoryBandwidth,
            "Пропускная способность памяти",
            gbPerSecond,
            "ГБ/с",
            (int)Math.Round(gbPerSecond * 100),
            stopwatch.Elapsed,
            $"буфер {megabytes} МБ, контрольная сумма {checksum:0.###}");
    }

    /// <summary>Задержка доступа к памяти (обход случайной цепочки указателей).</summary>
    public static BenchmarkResult MemoryLatency(int megabytes, CancellationToken token)
    {
        int elements = Math.Max(1, megabytes) * 1024 * 1024 / sizeof(int);
        int[] chain = new int[elements];
        var random = new Random(12345);

        for (int i = 0; i < elements; i++) chain[i] = i;
        for (int i = elements - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (chain[i], chain[j]) = (chain[j], chain[i]);
        }

        var stopwatch = Stopwatch.StartNew();
        long accesses = 0;
        int index = 0;
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(2) && !token.IsCancellationRequested)
        {
            for (int i = 0; i < 200_000; i++) index = chain[index];
            accesses += 200_000;
        }

        double seconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        double nanoseconds = seconds * 1_000_000_000 / Math.Max(1, accesses);
        return new BenchmarkResult(
            BenchmarkKind.MemoryLatency,
            "Задержка доступа к памяти",
            nanoseconds,
            "нс",
            (int)Math.Round(1000 / Math.Max(0.001, nanoseconds)),
            stopwatch.Elapsed,
            $"цепочка {megabytes} МБ, проверка {index}");
    }

    // ============================ Диск ============================

    /// <summary>Имя файла для тестов диска.</summary>
    public static string CreateTempFile(string? directory, string tag)
    {
        string folder = string.IsNullOrWhiteSpace(directory) ? AppSettings.TestsDirectory : directory;
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"pcmonitor-{tag}-{Guid.NewGuid():N}.tmp");
    }

    public static BenchmarkResult DiskSequentialWrite(string path, int megabytes, CancellationToken token)
    {
        try
        {
            return DiskSequentialWriteNative(path, megabytes, token);
        }
        catch (Exception)
        {
            return DiskSequentialWriteBuffered(path, megabytes, token);
        }
    }

    /// <summary>Последовательная запись без буферизации: измеряется реальный накопитель.</summary>
    private static BenchmarkResult DiskSequentialWriteNative(string path, int megabytes, CancellationToken token)
    {
        const int blockSize = 1024 * 1024;
        using SafeFileHandle handle = NativeFile.Open(path, write: true, create: true);
        IntPtr buffer = NativeFile.Allocate(blockSize);

        try
        {
            var pattern = new byte[blockSize];
            new Random(7).NextBytes(pattern);
            Marshal.Copy(pattern, 0, buffer, blockSize);

            long total = (long)megabytes * 1024 * 1024;
            long written = 0;
            var stopwatch = Stopwatch.StartNew();
            while (written < total && !token.IsCancellationRequested)
            {
                NativeFile.Write(handle, buffer, blockSize);
                written += blockSize;
            }

            NativeFile.Flush(handle);
            stopwatch.Stop();

            double seconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
            double mbPerSecond = written / seconds / 1024 / 1024;
            return new BenchmarkResult(
                BenchmarkKind.DiskSequentialWrite,
                "Диск: последовательная запись",
                mbPerSecond,
                "МБ/с",
                (int)Math.Round(mbPerSecond),
                stopwatch.Elapsed,
                $"записано {written / 1024 / 1024} МБ без буферизации (обход кэша ОС)");
        }
        finally
        {
            NativeFile.Free(buffer);
        }
    }

    public static BenchmarkResult DiskSequentialRead(string path, int megabytes, CancellationToken token)
    {
        try
        {
            return DiskSequentialReadNative(path, megabytes, token);
        }
        catch (Exception)
        {
            return DiskSequentialReadBuffered(path, megabytes, token);
        }
    }

    /// <summary>Последовательное чтение без буферизации.</summary>
    private static BenchmarkResult DiskSequentialReadNative(string path, int megabytes, CancellationToken token)
    {
        const int blockSize = 1024 * 1024;
        using SafeFileHandle handle = NativeFile.Open(path, write: false, create: false);
        IntPtr buffer = NativeFile.Allocate(blockSize);

        try
        {
            long total = (long)megabytes * 1024 * 1024;
            long read = 0;
            var stopwatch = Stopwatch.StartNew();
            while (read < total && !token.IsCancellationRequested)
            {
                int count = NativeFile.Read(handle, buffer, blockSize);
                if (count <= 0) break;
                read += count;
            }

            stopwatch.Stop();
            double seconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
            double mbPerSecond = read / seconds / 1024 / 1024;
            return new BenchmarkResult(
                BenchmarkKind.DiskSequentialRead,
                "Диск: последовательное чтение",
                mbPerSecond,
                "МБ/с",
                (int)Math.Round(mbPerSecond),
                stopwatch.Elapsed,
                $"прочитано {read / 1024 / 1024} МБ без буферизации (обход кэша ОС)");
        }
        finally
        {
            NativeFile.Free(buffer);
        }
    }

    private static BenchmarkResult DiskSequentialWriteBuffered(string path, int megabytes, CancellationToken token)
    {
        int blockSize = 1024 * 1024;
        byte[] buffer = new byte[blockSize];
        new Random(7).NextBytes(buffer);

        var stopwatch = Stopwatch.StartNew();
        long written = 0;
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, blockSize, FileOptions.None))
        {
            long total = (long)megabytes * 1024 * 1024;
            while (written < total && !token.IsCancellationRequested)
            {
                stream.Write(buffer, 0, blockSize);
                written += blockSize;
            }

            stream.Flush(true);
        }

        double seconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        double mbPerSecond = written / seconds / 1024 / 1024;
        return new BenchmarkResult(
            BenchmarkKind.DiskSequentialWrite,
            "Диск: последовательная запись",
            mbPerSecond,
            "МБ/с",
            (int)Math.Round(mbPerSecond),
            stopwatch.Elapsed,
            $"записано {written / 1024 / 1024} МБ (буферизованный режим)");
    }

    private static BenchmarkResult DiskSequentialReadBuffered(string path, int megabytes, CancellationToken token)
    {
        int blockSize = 1024 * 1024;
        byte[] buffer = new byte[blockSize];
        var stopwatch = Stopwatch.StartNew();
        long read = 0;
        long total = (long)megabytes * 1024 * 1024;

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, blockSize, FileOptions.SequentialScan))
        {
            while (read < total && !token.IsCancellationRequested)
            {
                int count = stream.Read(buffer, 0, blockSize);
                if (count <= 0) break;
                read += count;
            }
        }

        double seconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        double mbPerSecond = read / seconds / 1024 / 1024;
        return new BenchmarkResult(
            BenchmarkKind.DiskSequentialRead,
            "Диск: последовательное чтение",
            mbPerSecond,
            "МБ/с",
            (int)Math.Round(mbPerSecond),
            stopwatch.Elapsed,
            $"прочитано {read / 1024 / 1024} МБ (буферизованный режим, возможен кэш ОС)");
    }

    /// <summary>Случайные блоки 4 КБ без буферизации — аналог строки RND4K в CrystalDiskMark.</summary>
    public static BenchmarkResult DiskRandom(string path, bool write, int operations, CancellationToken token)
    {
        try
        {
            return DiskRandomNative(path, write, operations, token);
        }
        catch (Exception)
        {
            return DiskRandomBuffered(path, write, operations, token);
        }
    }

    private static BenchmarkResult DiskRandomNative(string path, bool write, int operations, CancellationToken token)
    {
        const int blockSize = 4096;
        using SafeFileHandle handle = NativeFile.Open(path, write: write, create: !write ? false : true);
        IntPtr buffer = NativeFile.Allocate(blockSize);

        try
        {
            var pattern = new byte[blockSize];
            new Random(11).NextBytes(pattern);
            Marshal.Copy(pattern, 0, buffer, blockSize);

            long length = new FileInfo(path).Length;
            if (length < blockSize * 256) length = blockSize * 256;

            var random = new Random(20260101);
            var stopwatch = Stopwatch.StartNew();
            int done = 0;
            var latencies = new List<double>(operations);

            while (done < operations && !token.IsCancellationRequested)
            {
                long offset = (long)(random.NextDouble() * (length - blockSize)) & ~(blockSize - 1L);
                NativeFile.Seek(handle, offset);

                var op = Stopwatch.StartNew();
                if (write) NativeFile.Write(handle, buffer, blockSize);
                else if (NativeFile.Read(handle, buffer, blockSize) <= 0) break;
                op.Stop();

                latencies.Add(op.Elapsed.TotalMilliseconds);
                done++;
            }

            if (write) NativeFile.Flush(handle);
            stopwatch.Stop();

            double seconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
            double iops = done / seconds;
            double mbPerSecond = iops * blockSize / 1024 / 1024;
            latencies.Sort();
            double median = latencies.Count > 0 ? latencies[latencies.Count / 2] : 0;

            return new BenchmarkResult(
                write ? BenchmarkKind.DiskRandomWrite : BenchmarkKind.DiskRandomRead,
                write ? "Диск: случайная запись 4К" : "Диск: случайное чтение 4К",
                iops,
                "IOPS",
                (int)Math.Round(iops),
                stopwatch.Elapsed,
                $"{mbPerSecond:0.0} МБ/с, медиана задержки {median:0.00} мс, операций {done}, без буферизации");
        }
        finally
        {
            NativeFile.Free(buffer);
        }
    }

    private static BenchmarkResult DiskRandomBuffered(string path, bool write, int operations, CancellationToken token)
    {
        const int blockSize = 4096;
        byte[] buffer = new byte[blockSize];
        new Random(11).NextBytes(buffer);

        using var stream = new FileStream(path, FileMode.OpenOrCreate,
            write ? FileAccess.Write : FileAccess.Read, FileShare.None, blockSize,
            FileOptions.RandomAccess | FileOptions.WriteThrough);

        long length = Math.Max(stream.Length, 256L * 1024 * 1024);
        var random = new Random(20260101);
        var stopwatch = Stopwatch.StartNew();
        int done = 0;
        var latencies = new List<double>(operations);

        while (done < operations && !token.IsCancellationRequested)
        {
            long offset = (long)(random.NextDouble() * (length - blockSize)) & ~(blockSize - 1L);
            stream.Position = offset;

            var op = Stopwatch.StartNew();
            if (write) stream.Write(buffer, 0, blockSize);
            else
            {
                int read = stream.Read(buffer, 0, blockSize);
                if (read <= 0) break;
            }

            op.Stop();
            latencies.Add(op.Elapsed.TotalMilliseconds);
            done++;
        }

        stream.Flush(true);
        stopwatch.Stop();
        double seconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        double iops = done / seconds;
        double mbPerSecond = iops * blockSize / 1024 / 1024;
        latencies.Sort();
        double median = latencies.Count > 0 ? latencies[latencies.Count / 2] : 0;

        return new BenchmarkResult(
            write ? BenchmarkKind.DiskRandomWrite : BenchmarkKind.DiskRandomRead,
            write ? "Диск: случайная запись 4К" : "Диск: случайное чтение 4К",
            iops,
            "IOPS",
            (int)Math.Round(iops),
            stopwatch.Elapsed,
            $"{mbPerSecond:0.0} МБ/с, медиана задержки {median:0.00} мс, операций {done}, буферизованный режим");
    }

    // ============================ Хранение результатов ============================

    public static string HistoryPath => Path.Combine(AppSettings.HistoryDirectory, "benchmarks.json");

    public static List<BenchmarkResult> LoadHistory()
    {
        try
        {
            if (File.Exists(HistoryPath))
                return JsonSerializer.Deserialize<List<BenchmarkResult>>(File.ReadAllText(HistoryPath)) ?? new List<BenchmarkResult>();
        }
        catch
        {
            // Повреждённая история не должна ломать приложение.
        }

        return new List<BenchmarkResult>();
    }

    public static void SaveHistory(IEnumerable<BenchmarkResult> results)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(results.Take(200).ToList(), options));
        }
        catch
        {
            // История — не критичные данные.
        }
    }
}
