using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;

namespace PcMonitor.Core.Stress;

/// <summary>
/// Эталон для проверки вычислений с плавающей точкой: значения сравниваются с относительным
/// допуском, потому что порядок операций в разных запусках может отличаться на младшие биты.
/// </summary>
internal sealed class ReferenceCheck
{
    private readonly double _tolerance;

    internal ReferenceCheck(double tolerance) => _tolerance = tolerance;

    /// <summary>Запомненное эталонное значение (0, пока эталон не получен).</summary>
    internal double Reference { get; private set; }

    /// <summary>Истина, если эталон уже получен.</summary>
    internal bool HasReference { get; private set; }

    /// <summary>Сверяет значение с эталоном; при первом вызове эталон просто запоминается.</summary>
    /// <returns>Истина, если расхождения нет.</returns>
    internal bool Match(double value)
    {
        if (!HasReference)
        {
            Reference = value;
            HasReference = true;
            return true;
        }

        double scale = Math.Max(1.0, Math.Max(Math.Abs(Reference), Math.Abs(value)));
        return Math.Abs(value - Reference) <= _tolerance * scale;
    }

    /// <summary>Текстовое представление эталона для отчёта.</summary>
    internal string Display => Reference.ToString("0.###############");
}

/// <summary>
/// Тесты процессора: целочисленные цепочки, ветвления, плавающая точка, SIMD (System.Numerics.Vector),
/// решето простых чисел, хеши, матричное умножение, сортировка, «сжатие», N тел и вычисление π.
/// </summary>
/// <remarks>
/// Каждый тест считает полезный результат (контрольную сумму) и сверяет его с эталоном, который
/// получен при первом выполнении на этой же машине. Любое расхождение считается ошибкой:
/// оно означает либо сбой вычислений, либо несовместимость оптимизаций.
/// </remarks>
internal static class CpuStressTests
{
    private const int SieveLimit = 2_000_000;
    private const int MatrixSize = 64;
    private const int SortLength = 120_000;
    private const int PayloadLength = 512 * 1024;
    private const int Bodies = 384;

    // Эталонные данные, неизменяемые между этапами.
    private static readonly int[] MatrixValues = CreateMatrixValues();
    private static readonly int[] SortSource = CreateSortSource();
    private static readonly byte[] Payload = CreatePayload();
    private static readonly uint[] CrcTable = CreateCrcTable();
    private static readonly uint PayloadCrc = ComputeCrc(Payload);
    private static readonly int[] Primes = CreatePrimes();

    /// <summary>Собирает список тестов процессора.</summary>
    internal static IReadOnlyList<IStressTest> Build()
    {
        const StressCategory C = StressCategory.Cpu;
        const string OperationsPerSecond = "Моп/с";
        return new List<IStressTest>
        {
            StressTestFactory.Create("cpu.int-add", C,
                "Целочисленное сложение (64 бита)",
                "Длинные цепочки сложений и умножений — базовый тест АЛУ и зависимостей по данным.",
                TimeSpan.FromMilliseconds(300), c => IntChain(c, 12_000_000, 0), throughputUnit: OperationsPerSecond),

            StressTestFactory.Create("cpu.int-xor-rotate", C,
                "XOR и циклические сдвиги",
                "Цепочка XOR и ROTL/ROR — проверка логики и баррель-шифтера.",
                TimeSpan.FromMilliseconds(300), c => IntChain(c, 12_000_000, 1), throughputUnit: OperationsPerSecond),

            StressTestFactory.Create("cpu.int-mixed", C,
                "Смешанная целочисленная арифметика",
                "Сложение, вычитание, умножение и деление по ветвям — неравномерная нагрузка на АЛУ.",
                TimeSpan.FromMilliseconds(600), c => IntMixed(c, 6_000_000), throughputUnit: OperationsPerSecond),

            StressTestFactory.Create("cpu.int-mtb", C,
                "Целочисленный стресс во всех потоках",
                "Те же цепочки, но в каждом логическом процессоре — проверка SMT и теплового пакета.",
                TimeSpan.FromMilliseconds(1200), c => IntChain(c, 12_000_000, 1), useManyThreads: true, throughputUnit: OperationsPerSecond),

            StressTestFactory.Create("cpu.branchy", C,
                "Непредсказуемые ветвления",
                "Псевдослучайные ветви по таблице данных — проверка предсказателя переходов.",
                TimeSpan.FromMilliseconds(600), c => Branchy(c, 4_000_000), throughputUnit: OperationsPerSecond),

            StressTestFactory.Create("cpu.branchy-mtb", C,
                "Ветвления во всех потоках",
                "Непредсказуемые переходы в каждом потоке — максимальная нагрузка на фронтенд ядра.",
                TimeSpan.FromMilliseconds(1200), c => Branchy(c, 4_000_000), useManyThreads: true, throughputUnit: OperationsPerSecond),

            StressTestFactory.Create("cpu.fpu-double", C,
                "Плавающая точка double",
                "Цепочка операций с двойной точностью: сложение, умножение, квадратный корень.",
                TimeSpan.FromMilliseconds(600), c => FpuDouble(c, 3_000_000), useManyThreads: true, throughputUnit: OperationsPerSecond),

            StressTestFactory.Create("cpu.fpu-float", C,
                "Плавающая точка float",
                "Одинарная точность: больше операций в секунду, проверка FPU на коротких зависимостях.",
                TimeSpan.FromMilliseconds(600), c => FpuFloat(c, 4_000_000), throughputUnit: OperationsPerSecond),

            StressTestFactory.Create("cpu.simd-vector", C,
                "SIMD через Vector&lt;T&gt;",
                "Векторное сложение и умножение с использованием System.Numerics.Vector (SSE/AVX).",
                TimeSpan.FromMilliseconds(900), c => SimdVector(c, 400_000), throughputUnit: OperationsPerSecond),

            StressTestFactory.Create("cpu.simd-mtb", C,
                "SIMD во всех потоках",
                "Векторные операции одновременно во всех процессорах — предельная нагрузка на FPU.",
                TimeSpan.FromMilliseconds(1800), c => SimdVector(c, 400_000), useManyThreads: true, throughputUnit: OperationsPerSecond),

            StressTestFactory.Create("cpu.prime-sieve", C,
                "Решето простых чисел",
                "Решето Эратосфена до 2 миллионов с проверкой эталонного списка простых чисел.",
                TimeSpan.FromMilliseconds(600), c => PrimeSieve(c), throughputUnit: OperationsPerSecond),

            StressTestFactory.Create("cpu.fnv1a", C,
                "Хеш FNV-1a",
                "Байтовый хеш FNV-1a по буферу 512 КБ со сверкой эталонного значения.",
                TimeSpan.FromMilliseconds(600), c => Fnv1a(c), throughputUnit: OperationsPerSecond),

            StressTestFactory.Create("cpu.crc32", C,
                "CRC32-подобная контрольная сумма",
                "Табличный CRC32 по буферу 512 КБ — эталон известен заранее.",
                TimeSpan.FromMilliseconds(600), c => Crc32(c), throughputUnit: OperationsPerSecond),

            StressTestFactory.Create("cpu.matrix-multiply", C,
                "Матричное умножение 64×64",
                "Тройной цикл умножения матриц с накоплением контрольной суммы результата.",
                TimeSpan.FromMilliseconds(900), c => MatrixMultiply(c), throughputUnit: OperationsPerSecond),

            StressTestFactory.Create("cpu.sorting", C,
                "Сортировка больших массивов",
                "Сортировка 120 000 элементов с проверкой упорядоченности и эталонной суммы.",
                TimeSpan.FromMilliseconds(900), c => Sorting(c), throughputUnit: OperationsPerSecond),

            StressTestFactory.Create("cpu.compression-like", C,
                "Сжатие-подобные преобразования",
                "LZ-подобный поиск повторов и RLE по буферу — тяжёлая работа с памятью и ветвлениями.",
                TimeSpan.FromMilliseconds(900), c => CompressionLike(c), throughputUnit: OperationsPerSecond),

            StressTestFactory.Create("cpu.n-body", C,
                "Гравитационная задача N тел",
                "384 тела и попарное взаимодействие — смесь умножений и квадратного корня.",
                TimeSpan.FromMilliseconds(1200), c => NBody(c), throughputUnit: OperationsPerSecond),

            StressTestFactory.Create("cpu.pi", C,
                "Вычисление числа π",
                "Ряд Лейбница для π: миллионы знакопеременных делений подряд.",
                TimeSpan.FromMilliseconds(600), c => Pi(c), throughputUnit: OperationsPerSecond),

            StressTestFactory.Create("cpu.mandelbrot", C,
                "Фрактал Мандельброта",
                "Расчёт множества Мандельброта: циклы с ветвлениями и плавающей точкой.",
                TimeSpan.FromMilliseconds(1200), c => Mandelbrot(c), useManyThreads: true, throughputUnit: OperationsPerSecond),
        };
    }

    // ---------------------------------------------------------------- инфраструктура

    /// <summary>Форматирует эталонное значение для отчёта.</summary>
    private static string Format(object? value) => value switch
    {
        null => "нет",
        ulong u => "0x" + u.ToString("X16"),
        long l => "0x" + l.ToString("X16"),
        double d => d.ToString("0.###############"),
        float f => f.ToString("0.#######"),
        int i => "0x" + i.ToString("X8"),
        uint u => "0x" + u.ToString("X8"),
        bool b => b ? "да" : "нет",
        _ => value.ToString() ?? "нет",
    };

    /// <summary>Запускает «итерацию» с замером времени и проверкой контрольной суммы.</summary>
    private static StageOutcome RunIterations(WorkerView w, Func<long> iteration, long operationsPerIteration, long bytesPerIteration, string title)
    {
        long iterations = 0;
        long operations = 0;
        double bytes = 0;
        long errors = 0;
        long? reference = null;
        string expected = title;
        string actual = "совпадает";
        TimeSpan lastIteration = TimeSpan.Zero;

        while (!w.Stop && w.Stage.HasTimeFor(lastIteration))
        {
            var watch = Stopwatch.StartNew();
            long checksum = iteration();
            watch.Stop();
            lastIteration = watch.Elapsed;

            iterations++;
            operations += operationsPerIteration;
            bytes += bytesPerIteration;
            w.RecordWork(bytesPerIteration, operationsPerIteration);

            if (reference is null)
            {
                reference = checksum;
                w.Check(title, Format(checksum));
            }
            else if (reference.Value != checksum)
            {
                errors++;
                expected = Format(reference.Value);
                actual = Format(checksum);
                w.Check(expected, actual);
            }
        }

        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Bytes = bytes,
            Operations = operations,
            Expected = title,
            Actual = errors == 0 ? $"эталон {Format(reference)} (итераций: {iterations})" : $"расхождение: {actual}",
        };
    }

    // ---------------------------------------------------------------- целочисленные тесты

    /// <summary>Длинная цепочка зависимых целочисленных операций (kind: 0 — сложение, 1 — xor/rotate).</summary>
    private static StageOutcome IntChain(WorkerView w, int iterations, int kind)
    {
        ulong seed = unchecked((ulong)w.Seed + (ulong)w.Stage.Stage);
        return RunIterations(w, () =>
        {
            ulong a = seed | 1;
            ulong b = seed ^ 0x9E3779B97F4A7C15UL;
            if (kind == 0)
            {
                for (int i = 0; i < iterations; i++)
                {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
                    a += b + (ulong)i;
                    b ^= a >> 7;
                    a *= 0x100000001B3UL;
                    b += a;
                }
            }
            else
            {
                for (int i = 0; i < iterations; i++)
                {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
                    a ^= b + (ulong)i;
                    b = BitOperations.RotateLeft(b, 13) ^ a;
                    a = BitOperations.RotateRight(a, 7) + b;
                }
            }

            return unchecked((long)(a ^ b));
        }, iterations * (kind == 0 ? 4 : 4), 0, kind == 0 ? "цепочка add/xor/mul" : "цепочка xor/rotl/rotr");
    }

    /// <summary>Смешанные операции: сложение, вычитание, умножение и деление по кругу.</summary>
    private static StageOutcome IntMixed(WorkerView w, int iterations)
    {
        ulong seed = unchecked((ulong)w.Seed + (ulong)w.Stage.Stage);
        return RunIterations(w, () =>
        {
            ulong a = seed | 1;
            ulong b = (seed ^ 0xDEADBEEFCAFEBABEUL) | 1;
            for (int i = 0; i < iterations; i++)
            {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
                a += b;
                b -= a >> 3;
                a *= b | 1;
                b /= (a & 7) + 1;
                a ^= b << 5;
            }

            return unchecked((long)(a ^ b));
        }, iterations * 6, 0, "смешанная целочисленная арифметика");
    }

    /// <summary>Непредсказуемые ветвления по таблице псевдослучайных данных.</summary>
    private static StageOutcome Branchy(WorkerView w, int iterations)
    {
        // Таблица данных — собственная у каждого потока: общий буфер движка здесь не годится,
        // иначе потоки затирали бы шаблоны друг друга.
        const int TableSize = 4096;
        byte[] table = new byte[TableSize];
        Random random = w.Random;
        for (int i = 0; i < TableSize; i++)
            table[i] = (byte)random.Next(256);

        ulong seed = unchecked((ulong)w.Seed);

        return RunIterations(w, () =>
        {
            long sum = 0;
            long count = 0;
            for (int i = 0; i < iterations; i++)
            {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
                byte value = table[i & (TableSize - 1)];
                if ((value & 1) != 0)
                {
                    if (value > 128)
                        sum += value * 3;
                    else
                        sum -= value;
                }
                else if ((value & 2) != 0)
                {
                    sum ^= value << 4;
                }
                else
                {
                    sum += value ^ 0x5A;
                }

                if (sum > 1_000_000_000L)
                    sum %= 1_000_000_007L;
                count += value & 1;
            }

            return unchecked((long)((ulong)sum * 31 + (ulong)count + seed));
        }, iterations * 3, iterations, "ветвления по случайным данным");
    }

    // ---------------------------------------------------------------- плавающая точка

    private static StageOutcome FpuDouble(WorkerView w, int iterations)
    {
        double seed = (w.Seed % 1000) / 7.0 + 1.0;
        return RunIterations(w, () =>
        {
            double a = seed;
            double b = seed * 1.0000001 + 0.5;
            for (int i = 0; i < iterations; i++)
            {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
                a = a * 1.0000001 + b;
                b = Math.Sqrt(Math.Abs(a)) + 0.5;
                a = a / (b + 1.0);
                b = a * a - b;
            }

            return BitConverter.DoubleToInt64Bits(a + b) ^ (long)iterations;
        }, iterations * 5, iterations * 16, "цепочка double (sqrt/mul/div)");
    }

    private static StageOutcome FpuFloat(WorkerView w, int iterations)
    {
        float seed = (w.Seed % 1000) / 7.0f + 1.0f;
        return RunIterations(w, () =>
        {
            float a = seed;
            float b = seed * 1.5f + 0.25f;
            for (int i = 0; i < iterations; i++)
            {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
                a = a * 1.0001f + b;
                b = MathF.Sqrt(MathF.Abs(a)) + 0.5f;
                a = a / (b + 1.0f);
                b = a * a - b;
            }

            return BitConverter.SingleToInt32Bits(a + b) ^ iterations;
        }, iterations * 5, iterations * 8, "цепочка float (sqrt/mul/div)");
    }

    /// <summary>Векторные операции через System.Numerics.Vector (SIMD).</summary>
    private static StageOutcome SimdVector(WorkerView w, int iterations)
    {
        float[] left = new float[Vector<float>.Count * 4];
        float[] right = new float[Vector<float>.Count * 4];
        Random random = w.Random;
        for (int i = 0; i < left.Length; i++)
        {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
            left[i] = (float)(random.NextDouble() * 2 - 1);
            right[i] = (float)(random.NextDouble() * 2 - 1) + 0.5f;
        }

        return RunIterations(w, () =>
        {
            Vector<float> acc = Vector<float>.Zero;
            for (int i = 0; i < iterations; i++)
            {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
                Vector<float> a = new(left);
                Vector<float> b = new(right);
                acc += (a * b) + Vector.SquareRoot(Vector.Abs(a));
                acc = acc / (Vector<float>.One + new Vector<float>(0.5f));
                // Записываем обратно в массив, чтобы компилятор не выбросил вычисления.
                (acc + a).CopyTo(left);
            }

            float sum = 0;
            for (int i = 0; i < left.Length; i++)
                sum += left[i];

            return BitConverter.SingleToInt32Bits(sum) ^ iterations;
        }, iterations * (long)Vector<float>.Count * 8, iterations * Vector<float>.Count * 16, $"SIMD Vector<float> ({Vector<float>.Count} элемента)");
    }

    // ---------------------------------------------------------------- алгоритмические тесты

    /// <summary>Решето Эратосфена с проверкой эталонного списка простых чисел.</summary>
    private static StageOutcome PrimeSieve(WorkerView w)
    {
        bool[] composite = new bool[SieveLimit + 1];
        int[] found = new int[Primes.Length];
        long errors = 0;
        int iterations = 0;

        while (!w.Stop)
        {
            Array.Clear(composite);
            for (int i = 2; (long)i * i <= SieveLimit; i++)
            {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
                if (composite[i])
                    continue;

                for (int j = i * i; j <= SieveLimit; j += i)
                    composite[j] = true;
            }

            int count = 0;
            for (int i = 2; i <= SieveLimit; i++)
            {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
                if (!composite[i])
                {
                    if (count < found.Length)
                        found[count] = i;
                    count++;
                }
            }

            iterations++;
            w.RecordWork(SieveLimit, SieveLimit);
            if (count != Primes.Length)
            {
                errors++;
                w.Check($"простых чисел до {SieveLimit}: {Primes.Length}", $"получено {count}");
                break;
            }

            int mismatch = 0;
            for (int i = 0; i < Primes.Length; i++)
            {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
                if (found[i] != Primes[i])
                {
                    mismatch = i;
                    break;
                }
            }

            if (mismatch != 0)
            {
                errors++;
                w.Check("простое число #" + mismatch + " = " + Primes[mismatch], "получено " + found[mismatch]);
                break;
            }
        }

        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Bytes = (double)SieveLimit * iterations,
            Operations = (double)SieveLimit * iterations,
            Expected = $"простых чисел до {SieveLimit}: {Primes.Length}",
            Actual = errors == 0 ? $"эталон совпал (проходов: {iterations})" : "расхождение списка простых чисел",
        };
    }

    /// <summary>Хеш FNV-1a по фиксированному буферу 512 КБ.</summary>
    private static StageOutcome Fnv1a(WorkerView w) =>
        RunIterations(w, () =>
        {
            ulong hash = 14695981039346656037UL;
            foreach (byte b in Payload)
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }

            return unchecked((long)hash);
        }, Payload.Length, Payload.Length, "хеш FNV-1a буфера 512 КБ");

    /// <summary>Табличный CRC32 по фиксированному буферу 512 КБ.</summary>
    private static StageOutcome Crc32(WorkerView w) =>
        RunIterations(w, () =>
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in Payload)
                crc = (crc >> 8) ^ CrcTable[(crc ^ b) & 0xFF];

            return unchecked((int)(crc ^ 0xFFFFFFFFu));
        }, Payload.Length, Payload.Length, "CRC32 буфера 512 КБ (эталон 0x" + PayloadCrc.ToString("X8") + ")");

    /// <summary>Матричное умножение 64×64 с контрольной суммой результата.</summary>
    private static StageOutcome MatrixMultiply(WorkerView w)
    {
        int size = MatrixSize;
        int[] a = (int[])MatrixValues.Clone();
        int[] b = (int[])MatrixValues.Clone();
        int[] c = new int[size * size];
        long errors = 0;
        int reference = 0;
        bool hasReference = false;
        int iterations = 0;

        while (!w.Stop && w.Stage.HasTimeFor(TimeSpan.Zero))
        {
            Array.Clear(c);
            for (int i = 0; i < size; i++)
            {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
                int rowOffset = i * size;
                for (int k = 0; k < size; k++)
                {
                if ((k & 16383) == 0) w.ThrowIfCancellationRequested();
                    int av = a[rowOffset + k];
                    int bOffset = k * size;
                    for (int j = 0; j < size; j++)
                        c[rowOffset + j] += av * b[bOffset + j];
                }
            }

            int checksum = 0;
            foreach (int value in c)
                checksum = unchecked(checksum * 31 + value);

            iterations++;
            w.RecordWork((double)size * size * sizeof(int) * 3, (double)size * size * size);
            if (!hasReference)
            {
                reference = checksum;
                hasReference = true;
                w.Check($"контрольная сумма умножения {size}×{size}", "0x" + checksum.ToString("X8"));
            }
            else if (checksum != reference)
            {
                errors++;
                w.Check("0x" + reference.ToString("X8"), "0x" + checksum.ToString("X8"));
            }
        }

        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Operations = (double)size * size * size * iterations,
            Bytes = (double)size * size * sizeof(int) * 3 * iterations,
            Expected = $"умножение {size}×{size}: контрольная сумма 0x{reference:X8}",
            Actual = errors == 0 ? $"совпадает (итераций: {iterations})" : "контрольная сумма изменилась",
        };
    }

    /// <summary>Сортировка большого массива с проверкой упорядоченности и суммы.</summary>
    private static StageOutcome Sorting(WorkerView w)
    {
        int[] data = new int[SortLength];
        long errors = 0;
        long reference = 0;
        bool hasReference = false;
        int iterations = 0;

        while (!w.Stop)
        {
            Array.Copy(SortSource, data, SortLength);
            Array.Sort(data);

            long sum = 0;
            bool sorted = true;
            for (int i = 0; i < data.Length; i++)
            {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
                sum += data[i];
                if (i > 0 && data[i - 1] > data[i])
                    sorted = false;
            }

            iterations++;
            w.RecordWork((double)SortLength * sizeof(int), SortLength);
            if (!sorted)
            {
                errors++;
                w.Check("массив отсортирован по возрастанию", "порядок нарушен");
            }

            if (!hasReference)
            {
                reference = sum;
                hasReference = true;
                w.Check("сумма элементов после сортировки", reference.ToString());
            }
            else if (sum != reference)
            {
                errors++;
                w.Check(reference.ToString(), sum.ToString());
            }
        }

        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Operations = (double)SortLength * iterations,
            Bytes = (double)SortLength * sizeof(int) * iterations,
            Expected = "сумма элементов после сортировки: " + reference,
            Actual = errors == 0 ? $"совпадает (сортировок: {iterations})" : "ошибка сортировки",
        };
    }

    /// <summary>LZ-подобный поиск повторов и RLE-упаковка — «сжатие» без внешних библиотек.</summary>
    private static StageOutcome CompressionLike(WorkerView w)
    {
        long reference = 0;
        bool hasReference = false;
        long errors = 0;
        int iterations = 0;

        while (!w.Stop)
        {
            long encoded = 0;
            byte previous = 0;
            int run = 0;
            for (int i = 0; i < Payload.Length; i++)
            {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
                byte value = Payload[i];
                if (run > 0 && value == previous)
                {
                    run++;
                    if (run == 255)
                    {
                        encoded = encoded * 31 + run;
                        run = 0;
                    }

                    continue;
                }

                encoded = encoded * 31 + previous + run;
                previous = value;
                run = 1;
            }

            encoded = encoded * 31 + previous + run;

            // LZ-подобное «сопоставление»: ищем повторы длиной 4 байта на расстоянии до 4096.
            int matches = 0;
            for (int i = 0; i + 4 < Payload.Length; i += 2)
            {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
                int limit = Math.Min(4096, i);
                for (int d = 4; d <= limit; d += 4)
                {
                if ((d & 16383) == 0) w.ThrowIfCancellationRequested();
                    if (Payload[i] == Payload[i - d] && Payload[i + 1] == Payload[i - d + 1])
                    {
                        matches++;
                        break;
                    }
                }
            }

            long checksum = encoded ^ ((long)matches * unchecked((long)0x9E3779B97F4A7C15UL));
            iterations++;
            w.RecordWork(Payload.Length, Payload.Length);
            if (!hasReference)
            {
                reference = checksum;
                hasReference = true;
                w.Check("сжатие-подобное преобразование", "0x" + checksum.ToString("X16"));
            }
            else if (checksum != reference)
            {
                errors++;
                w.Check("0x" + reference.ToString("X16"), "0x" + checksum.ToString("X16"));
            }
        }

        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Operations = (double)Payload.Length * iterations,
            Bytes = (double)Payload.Length * iterations,
            Expected = "контрольная сумма сжатия: 0x" + reference.ToString("X16"),
            Actual = errors == 0 ? $"совпадает (проходов: {iterations})" : "контрольная сумма изменилась",
        };
    }

    /// <summary>
    /// Задача N тел: тела ставятся на стабильные круговые орбиты вокруг общего центра,
    /// поэтому полная энергия системы сохраняется и годится как эталон корректности.
    /// </summary>
    private static StageOutcome NBody(WorkerView w)
    {
        const int Count = Bodies;
        const double Dt = 0.001;
        const double Softening = 0.01;
        const int StepsPerIteration = 32;

        double[] x = new double[Count];
        double[] y = new double[Count];
        double[] z = new double[Count];
        double[] vx = new double[Count];
        double[] vy = new double[Count];
        double[] vz = new double[Count];

        // Полная энергия устойчивой системы меняется очень мало, поэтому допуск жёсткий:
        // он ловит и сбои вычислений, и случайную порчу состояния.
        var check = new ReferenceCheck(1e-12);
        long errors = 0;
        int iterations = 0;

        while (!w.Stop)
        {
            // Инициализация одинакова в каждой итерации, поэтому эталон воспроизводим.
            InitializeBodies(x, y, z, vx, vy, vz);
            int steps = 0;
            while (steps < StepsPerIteration && !w.Stop)
            {
                Step(x, y, z, vx, vy, vz, Dt, Softening);
                steps++;
            }

            // Сверяем только полностью выполненную серию шагов: иначе сравнивались бы
            // состояния с разным числом шагов.
            if (steps < StepsPerIteration)
                break;

            double energy = TotalEnergy(x, y, z, vx, vy, vz, Softening);
            iterations++;
            w.RecordWork((double)Count * sizeof(double) * 6 * StepsPerIteration, (double)Count * Count * 20 * StepsPerIteration);
            if (!check.Match(energy))
            {
                errors++;
                w.Check(check.Display, energy.ToString("0.###############"));
            }
        }

        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Operations = (double)Count * Count * 20 * StepsPerIteration * iterations,
            Bytes = (double)Count * sizeof(double) * 6 * StepsPerIteration * iterations,
            Expected = "полная энергия системы N тел: " + check.Display,
            Actual = errors == 0 ? $"сохраняется (итераций: {iterations})" : "энергия изменилась",
        };
    }

    /// <summary>Ставит тела на круговые орбиты вокруг центра масс — устойчивая конфигурация.</summary>
    private static void InitializeBodies(double[] x, double[] y, double[] z, double[] vx, double[] vy, double[] vz)
    {
        const double CentralMass = 1000.0;
        const double G = 1.0;
        int count = x.Length;
        for (int i = 0; i < count; i++)
        {
            double radius = 0.5 + (i % 16) * 0.05;
            double angle = i * 0.37;
            double speed = Math.Sqrt(G * CentralMass / radius);
            x[i] = Math.Cos(angle) * radius;
            y[i] = Math.Sin(angle) * radius;
            z[i] = ((i % 8) - 4) * 0.01;
            vx[i] = -Math.Sin(angle) * speed;
            vy[i] = Math.Cos(angle) * speed;
            vz[i] = 0;
        }
    }

    /// <summary>Один шаг симуляции: попарные силы, затем обновление скоростей и координат.</summary>
    private static void Step(double[] x, double[] y, double[] z, double[] vx, double[] vy, double[] vz, double dt, double softening)
    {
        int count = x.Length;
        for (int i = 0; i < count; i++)
        {
            double ax = 0;
            double ay = 0;
            double az = 0;
            for (int j = 0; j < count; j++)
            {
                if (i == j)
                    continue;

                double dx = x[j] - x[i];
                double dy = y[j] - y[i];
                double dz = z[j] - z[i];
                double distance = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz) + softening);
                double factor = 1.0 / (distance * distance * distance);
                ax += dx * factor;
                ay += dy * factor;
                az += dz * factor;
            }

            vx[i] += ax * dt;
            vy[i] += ay * dt;
            vz[i] += az * dt;
        }

        for (int i = 0; i < count; i++)
        {
            x[i] += vx[i] * dt;
            y[i] += vy[i] * dt;
            z[i] += vz[i] * dt;
        }
    }

    /// <summary>Полная (кинетическая плюс потенциальная) энергия системы.</summary>
    private static double TotalEnergy(double[] x, double[] y, double[] z, double[] vx, double[] vy, double[] vz, double softening)
    {
        int count = x.Length;
        double energy = 0;
        for (int i = 0; i < count; i++)
        {
            energy += 0.5 * ((vx[i] * vx[i]) + (vy[i] * vy[i]) + (vz[i] * vz[i]));
            for (int j = i + 1; j < count; j++)
            {
                double dx = x[j] - x[i];
                double dy = y[j] - y[i];
                double dz = z[j] - z[i];
                double distance = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz) + softening);
                energy -= 1.0 / distance;
            }
        }

        return energy;
    }

    /// <summary>Ряд Лейбница для π: длинная цепочка делений и знакопеременных сложений.</summary>
    private static StageOutcome Pi(WorkerView w)
    {
        const int Iterations = 3_000_000;
        return RunIterations(w, () =>
        {
            double sum = 0;
            double divisor = 1;
            double sign = 1;
            for (int i = 0; i < Iterations; i++)
            {
                if ((i & 16383) == 0) w.ThrowIfCancellationRequested();
                sum += sign / divisor;
                divisor += 2;
                sign = -sign;
            }

            double pi = sum * 4;
            return BitConverter.DoubleToInt64Bits(Math.Round(pi, 12));
        }, Iterations * 4, Iterations * 8, "ряд Лейбница: π с 12 знаками");
    }

    /// <summary>Множество Мандельброта: циклы с ветвлениями и плавающей точкой.</summary>
    private static StageOutcome Mandelbrot(WorkerView w)
    {
        const int Width = 320;
        const int Height = 200;
        const int MaxIterations = 400;
        int[] pixels = new int[Width * Height];
        long reference = 0;
        bool hasReference = false;
        long errors = 0;
        int passes = 0;

        while (!w.Stop)
        {
            for (int py = 0; py < Height; py++)
            {
                if ((py & 16383) == 0) w.ThrowIfCancellationRequested();
                double y0 = (py / (double)Height * 2.4) - 1.2;
                for (int px = 0; px < Width; px++)
                {
                if ((px & 16383) == 0) w.ThrowIfCancellationRequested();
                    double x0 = (px / (double)Width * 3.2) - 2.1;
                    double x = 0;
                    double y = 0;
                    int iteration = 0;
                    while ((x * x) + (y * y) <= 4 && iteration < MaxIterations)
                    {
                        double xtemp = (x * x) - (y * y) + x0;
                        y = (2 * x * y) + y0;
                        x = xtemp;
                        iteration++;
                    }

                    pixels[(py * Width) + px] = iteration;
                }
            }

            long checksum = 0;
            foreach (int value in pixels)
                checksum = unchecked((checksum * 31) + value);

            passes++;
            w.RecordWork((double)pixels.Length * sizeof(int), (double)Width * Height);
            if (!hasReference)
            {
                reference = checksum;
                hasReference = true;
                w.Check("контрольная сумма фрактала", "0x" + checksum.ToString("X16"));
            }
            else if (checksum != reference)
            {
                errors++;
                w.Check("0x" + reference.ToString("X16"), "0x" + checksum.ToString("X16"));
            }
        }

        if (errors > 0)
            w.FailFast(errors);

        return new StageOutcome
        {
            Operations = (double)Width * Height * passes,
            Bytes = (double)pixels.Length * sizeof(int) * passes,
            Expected = "контрольная сумма фрактала: 0x" + reference.ToString("X16"),
            Actual = errors == 0 ? $"совпадает (кадров: {passes})" : "фрактал считается неверно",
        };
    }

    // ---------------------------------------------------------------- эталонные данные

    private static int[] CreateMatrixValues()
    {
        var values = new int[MatrixSize * MatrixSize];
        int state = 1234567;
        for (int i = 0; i < values.Length; i++)
        {
            state = unchecked((state * 1103515245) + 12345);
            values[i] = (state >> 16) & 0x7F;
        }

        return values;
    }

    private static int[] CreateSortSource()
    {
        var values = new int[SortLength];
        int state = 987654321;
        for (int i = 0; i < values.Length; i++)
        {
            state = unchecked((state * 1103515245) + 12345);
            values[i] = state & 0x7FFFFFFF;
        }

        return values;
    }

    private static byte[] CreatePayload()
    {
        var bytes = new byte[PayloadLength];
        int state = 424242;
        for (int i = 0; i < bytes.Length; i++)
        {
            state = unchecked((state * 1103515245) + 12345);
            // Смесь повторяющихся и случайных байтов: полезна и для хешей, и для «сжатия».
            bytes[i] = (i % 7 == 0 || i % 11 == 0) ? (byte)(i & 0xFF) : (byte)((state >> 16) & 0xFF);
        }

        return bytes;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;

            table[i] = value;
        }

        return table;
    }

    private static uint ComputeCrc(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
            crc = (crc >> 8) ^ CrcTable[(crc ^ b) & 0xFF];

        return crc ^ 0xFFFFFFFFu;
    }

    /// <summary>Эталонный список простых чисел до <see cref="SieveLimit"/>.</summary>
    private static int[] CreatePrimes()
    {
        var composite = new bool[SieveLimit + 1];
        var primes = new List<int>();
        for (int i = 2; i <= SieveLimit; i++)
        {
            if (composite[i])
                continue;

            primes.Add(i);
            for (long j = (long)i * i; j <= SieveLimit; j += i)
                composite[j] = true;
        }

        return primes.ToArray();
    }
}
