using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PcMonitor.Core.Stress;

/// <summary>Заготовка результата этапа, которую заполняет конкретный тест.</summary>
internal sealed class StageOutcome
{
    /// <summary>Сколько байт обработано за этап (для расчёта МБ/с).</summary>
    internal double Bytes { get; set; }

    /// <summary>Сколько логических операций (проходов, блоков, сравнений) выполнено.</summary>
    internal double Operations { get; set; }

    /// <summary>Текстовая строка проверки: что именно сверялось на этом этапе.</summary>
    internal string Expected { get; set; } = "проверка корректности";

    /// <summary>Фактический результат проверки.</summary>
    internal string Actual { get; set; } = "совпадает";
}

/// <summary>
/// Универсальный исполнитель стресс-тестов: разбивает работу на этапы, поддерживает отмену,
/// транслирует прогресс в интерфейс и собирает итоговый отчёт.
/// </summary>
/// <remarks>
/// Один тест = ровно <see cref="StressTestCatalog.TestStageCount"/> этапов. Каждый этап выполняется
/// синхронно на вызывающем потоке; сам тест может создать дополнительные рабочие потоки.
/// При отмене возвращается частичный результат с заполненным признаком «отменён»,
/// исключение <see cref="OperationCanceledException"/> наружу не выпускается.
/// </remarks>
public sealed class StressTestRunner
{
    /// <summary>Переменная окружения с длительностью этапа в миллисекундах (нужна утилите проверки).</summary>
    internal const string StageDurationVariable = "PCMON_STRESS_STAGE_MS";

    /// <summary>Выполняет тест целиком и возвращает отчёт.</summary>
    /// <param name="test">Тест из каталога <see cref="StressTestCatalog"/>.</param>
    /// <param name="options">Параметры запуска.</param>
    /// <param name="progress">Необязательный получатель прогресса (можно передать <c>null</c>).</param>
    /// <param name="token">Токен отмены.</param>
    /// <returns>Отчёт по тесту; при отмене — частичный, с установленным признаком отмены.</returns>
    public Task<StressTestResult> RunAsync(IStressTest test, StressOptions options, IProgress<StressProgress>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(test);
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() => Run(test, options, progress, token), CancellationToken.None);
    }

    /// <summary>Синхронная реализация прогона: последовательно выполняет все этапы теста.</summary>
    private static StressTestResult Run(IStressTest test, StressOptions options, IProgress<StressProgress>? progress, CancellationToken token)
    {
        int stageCount = options.RunUntilStopped ? 1 : Math.Max(1, test.StageCount);
        TimeSpan stageDuration = ResolveStageDuration(test);
        var stages = new List<StressStageResult>(stageCount);
        var sink = progress is null ? null : new ProgressSink(progress, stageCount);
        var totalWatch = Stopwatch.StartNew();
        bool cancelled = token.IsCancellationRequested;
        double throughputSum = 0;
        long totalErrors = 0;
        string status = "запуск";

        int stage = 1;
        for (; stage <= stageCount; stage++)
        {
            if (token.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            StressStageResult result;
            try
            {
                result = test.RunStage(stage, options, token, sink?.AsProgress());
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Ошибка одного этапа не должна ронять весь пакет: фиксируем её как расхождение.
                result = new StressStageResult(stage, 0, 0, 1, "выполнение этапа без ошибок", "исключение: " + ex.GetType().Name + ": " + ex.Message, TimeSpan.Zero);
            }

            stages.Add(result);
            if (token.IsCancellationRequested) cancelled = true;
            totalErrors += result.Errors;
            throughputSum += result.Throughput;
            status = $"этап {stage}/{stageCount}: {result.Throughput:0.##} {test.ThroughputUnit}";
            sink?.StageCompleted(stage, totalErrors, result.Throughput, status);
            // Бессрочный прогон — один этап, а не перезапуски ограниченных тестов.
            if (options.RunUntilStopped) break;
        }

        totalWatch.Stop();
        if (token.IsCancellationRequested)
            cancelled = true;

        int executed = stages.Count;
        double average = executed == 0 ? 0 : throughputSum / executed;
        sink?.Finish(cancelled, totalErrors, status);

        string summary = BuildSummary(test, options, stageDuration, stageCount, executed, totalErrors, average, cancelled);
        bool passed = !cancelled && executed == stageCount && totalErrors == 0;

        return new StressTestResult(test.Id, test.Name, passed, totalErrors, totalWatch.Elapsed, stages, summary)
        {
            Cancelled = cancelled,
        };
    }

    /// <summary>Определяет длительность этапа: переменная окружения перекрывает значение теста.</summary>
    internal static TimeSpan ResolveStageDuration(IStressTest test)
    {
        string? raw = Environment.GetEnvironmentVariable(StageDurationVariable);
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out int ms) && ms >= 5)
            return TimeSpan.FromMilliseconds(ms);

        return test.StageDuration > TimeSpan.Zero ? test.StageDuration : TimeSpan.FromMilliseconds(300);
    }

    private static string BuildSummary(
        IStressTest test,
        StressOptions options,
        TimeSpan stageDuration,
        int stageCount,
        int executed,
        long totalErrors,
        double averageThroughput,
        bool cancelled)
    {
        string state = cancelled ? "остановлен по запросу отмены" : "завершён";
        string errors = totalErrors == 0 ? "расхождений нет" : $"расхождений: {totalErrors}";
        if (options.RunUntilStopped)
            return $"{test.Name}: {state}; непрерывный режим без ограничения времени; " +
                   $"потоков: {options.EffectiveThreads}; скорость {averageThroughput:0.##} {test.ThroughputUnit}; {errors}.";
        return $"{test.Name}: {state}; этапов {executed}/{stageCount} по {stageDuration.TotalMilliseconds:0} мс; " +
               $"потоков: {options.EffectiveThreads}; средняя пропускная способность {averageThroughput:0.##} {test.ThroughputUnit}; {errors}.";
    }
}

/// <summary>
/// Общее ядро выполнения одного этапа: единый таймер, учёт ошибок, отмена и многопоточность.
/// </summary>
internal static class StressStageRunner
{
    /// <summary>Выполняет один этап теста с общим контекстом и (при необходимости) рабочими потоками.</summary>
    internal static StressStageResult Execute(
        IStressTest test,
        int stage,
        StressOptions options,
        CancellationToken token,
        IProgress<StressProgress>? progress,
        Func<WorkerView, StageOutcome> workload,
        bool useManyThreads)
    {
        TimeSpan duration = StressTestRunner.ResolveStageDuration(test);
        var stageContext = new StressStageContext(stage, options, token, duration, test.ThroughputUnit);
        var watch = Stopwatch.StartNew();
        // Отдельный heartbeat не трогает рабочие буферы и не ставит дедлайн нагрузке.
        using var heartbeat = options.RunUntilStopped && progress is not null
            ? new Timer(_ =>
            {
                double seconds = Math.Max(watch.Elapsed.TotalSeconds, 1e-6);
                double speed = test.ThroughputUnit == "МБ/с"
                    ? stageContext.Bytes / (1024.0 * 1024.0) / seconds
                    : stageContext.Operations / seconds;
                progress.Report(new StressProgress(1, 0, 0, speed, stageContext.Errors,
                    $"непрерывная нагрузка · {speed:0.0} {test.ThroughputUnit}")
                    { Elapsed = watch.Elapsed, IsIndefinite = true });
            }, null, 0, 500)
            : null;

        if (token.IsCancellationRequested)
        {
            stageContext.CleanupFiles();
            return BuildResult(stageContext, null, stage, watch.Elapsed);
        }

        try
        {
            if (useManyThreads && options.EffectiveThreads > 1)
            {
                RunThreaded(test, stageContext, workload);
                watch.Stop();
                return BuildResult(stageContext, null, stage, watch.Elapsed);
            }

            var single = new WorkerView(stageContext, 0, options.Seed);
            StageOutcome? outcome = single.RunContinuous(workload);

            watch.Stop();
            return BuildResult(stageContext, outcome, stage, watch.Elapsed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            watch.Stop();
            return BuildResult(stageContext, null, stage, watch.Elapsed);
        }
        finally
        {
            // Временные файлы тестов диска удаляются всегда — и при ошибке, и при отмене.
            stageContext.CleanupFiles();
        }
    }

    /// <summary>Запускает работу на нескольких потоках: основной поток тоже считает.</summary>
    private static void RunThreaded(IStressTest test, StressStageContext stageContext, Func<WorkerView, StageOutcome> workload)
    {
        int threads = stageContext.Options.EffectiveThreads;
        var workers = new List<(Thread Thread, WorkerView View)>(threads);
        for (int i = 0; i < threads; i++)
        {
            var view = stageContext.CreateView(i);
            var thread = new Thread(static state =>
            {
                var payload = (WorkerPayload)state!;
                payload.View.Run(payload.Workload);
            }, 2 * 1024 * 1024)
            {
                IsBackground = true,
                Name = $"{test.Id}-{i}",
            };
            workers.Add((thread, view));
        }

        int started = 1;
        try
        {
            for (; started < workers.Count; started++)
                workers[started].Thread.Start(new WorkerPayload(workers[started].View, workload));
            workers[0].View.Run(workload);
        }
        finally
        {
            // При штатном ограниченном этапе все потоки завершают собственный проход.
            // Прервать уже запущенные нужно лишь при ошибке запуска следующего потока.
            if (started < workers.Count) stageContext.Abort();
            for (int i = 1; i < started; i++)
                workers[i].Thread.Join();
        }

        stageContext.CollectAll();
        if (stageContext.WorkerException is { } failure)
            throw new InvalidOperationException("Ошибка рабочего потока", failure);
    }

    private static StressStageResult BuildResult(StressStageContext stageContext, StageOutcome? outcome, int stage, TimeSpan elapsed)
    {
        double seconds = Math.Max(elapsed.TotalSeconds, 1e-6);
        double bytes = stageContext.Bytes;
        double operations = stageContext.Operations;

        // Пропускная способность измеряется в МБ/с для потоковых тестов и в операциях/с для
        // вычислительных: там объём данных ничего не говорит о нагрузке.
        double throughput = stageContext.ThroughputUnit == "МБ/с"
            ? (bytes > 1 ? bytes / (1024.0 * 1024.0) / seconds : operations / seconds)
            : operations / seconds;

        return new StressStageResult(
            stage,
            throughput,
            operations / seconds,
            stageContext.Errors,
            outcome?.Expected ?? stageContext.Expected,
            outcome?.Actual ?? stageContext.Actual,
            elapsed);
    }
}

/// <summary>Данные для рабочего потока этапа.</summary>
internal readonly record struct WorkerPayload(WorkerView View, Func<WorkerView, StageOutcome> Workload);

/// <summary>Настройки частоты проверок отмены и времени.</summary>
internal static class ThreadContextTuning
{
    /// <summary>Минимальный интервал между проверками токена/дедлайна (~15 мс).</summary>
    internal static readonly long StopCheckTicks = Math.Max(1, Stopwatch.Frequency / 64);
}

/// <summary>
/// Контекст одного рабочего потока: персональный генератор случайных чисел и собственные
/// счётчики ошибок/объёма, которые не требуют синхронизации в горячем цикле.
/// </summary>
internal sealed class WorkerView
{
    private readonly StressStageContext _stage;
    private long _lastStopCheck;
    private long _errors;
    private bool _liveCounters;

    internal WorkerView(StressStageContext stage, int index, int seed)
    {
        _stage = stage;
        Index = index;
        Seed = unchecked(seed + (int)((uint)index * 0x9E3779B1u));
        Random = new Random(Seed);
    }

    /// <summary>Порядковый номер рабочего потока (0 — поток вызывающего кода).</summary>
    internal int Index { get; }

    /// <summary>Контекст этапа, к которому относится этот рабочий поток.</summary>
    internal StressStageContext Stage => _stage;

    /// <summary>Псевдоним <see cref="Index"/> для читаемости в тестах.</summary>
    internal int ThreadIndex => Index;

    /// <summary>Персональное зерно потока.</summary>
    internal int Seed { get; }

    /// <summary>Псевдоним <see cref="Seed"/> для читаемости в тестах.</summary>
    internal int ThreadSeed => Seed;

    /// <summary>Персональный генератор случайных чисел (не разделяется между потоками).</summary>
    internal Random Random { get; }

    /// <summary>Истина, когда этап пора прекращать (истекло время или запрошена отмена).</summary>
    internal bool Stop
    {
        get
        {
            long now = Stopwatch.GetTimestamp();
            if (_stage.Aborted || _stage.Token.IsCancellationRequested) return true;
            if (now - _lastStopCheck < ThreadContextTuning.StopCheckTicks &&
                (!_stage.Deadline.HasValue || now < _stage.Deadline.Value))
                return false;

            _lastStopCheck = now;
            if (_stage.Deadline.HasValue && now >= _stage.Deadline.Value)
                return true;

            return _stage.Token.IsCancellationRequested;
        }
    }

    internal void ThrowIfCancellationRequested()
    {
        _stage.Token.ThrowIfCancellationRequested();
        if (_stage.Aborted) throw new OperationCanceledException("Этап прерван");
    }

    /// <summary>Регистрирует расхождение с подробностями (hex-значения шаблонов).</summary>
    internal void Fail(string expected, string actual)
    {
        _errors++;
        _stage.PublishDetails(expected, actual);
        FlushErrors();
    }

    /// <summary>Регистрирует несколько расхождений без подробностей (быстрый путь).</summary>
    internal void FailFast(long count = 1)
    {
        if (count <= 0)
            return;

        _errors += count;
        FlushErrors();
    }

    /// <summary>Фиксирует описание успешной проверки этапа.</summary>
    internal void Check(string expected, string actual) => _stage.PublishCheck(expected, actual);

    /// <summary>
    /// Публикует счётчики ошибок текущего потока в общий контекст этапа.
    /// Вызывается сразу после регистрации расхождения, чтобы отмена и прогресс видели ошибки.
    /// </summary>
    internal void FlushErrors()
    {
        long errors = _errors;
        if (errors <= 0)
            return;

        _errors = 0;
        _stage.AddErrors(errors);
    }

    /// <summary>Выполняет рабочую функцию потока с гарантированной публикацией счётчиков.</summary>
    internal void Run(Func<WorkerView, StageOutcome> workload)
    {
        try
        {
            RunContinuous(workload);
        }
        catch (OperationCanceledException)
        {
            _stage.Abort();
        }
        catch (Exception ex)
        {
            _stage.RecordWorkerException(ex);
        }
        finally
        {
            FlushErrors();
        }
    }

    /// <summary>Публикует завершённый блок/итерацию непрерывной нагрузки; ограниченный режим не меняется.</summary>
    internal void RecordWork(double bytes, double operations)
    {
        if (!_stage.Options.RunUntilStopped) return;
        _liveCounters = true;
        _stage.Add(bytes, operations);
    }

    /// <summary>Полные проходы одного непрерывного этапа; пул и токен остаются теми же.</summary>
    internal StageOutcome? RunContinuous(Func<WorkerView, StageOutcome> workload)
    {
        StageOutcome? outcome = null;
        do
        {
            if (Stop) break;
            _liveCounters = false;
            outcome = workload(this);
            if (outcome is not null && !_liveCounters) _stage.Add(outcome.Bytes, outcome.Operations);
            FlushErrors();
        } while (_stage.Options.RunUntilStopped && !Stop);
        return outcome;
    }

    /// <summary>Собственный счётчик расхождений потока (сбрасывается после публикации).</summary>
    internal long Errors => _errors;
}

/// <summary>
/// Контекст этапа: хранит параметры, дедлайн, накопленные ошибки и предоставляет
/// тесту вспомогательные методы (замер прогресса, регистрация расхождений).
/// </summary>
internal sealed class StressStageContext
{
    private readonly WorkerView[] _views = new WorkerView[64];
    private long _viewCount;
    private long _bytes;
    private long _operations;
    private long _errors;
    private long _details;
    private string? _firstExpected;
    private string? _firstActual;
    private BufferPool? _pool;
    private readonly object _poolLock = new();
    private BufferPool? _cachePool;
    private readonly List<string> _createdFiles = new();
    private int _aborted;
    private Exception? _workerException;

    internal StressStageContext(int stage, StressOptions options, CancellationToken token, TimeSpan stageDuration, string throughputUnit)
    {
        Stage = stage;
        Options = options;
        Token = token;
        StageDuration = stageDuration;
        ThroughputUnit = throughputUnit;
        Deadline = options.RunUntilStopped ? null :
            Stopwatch.GetTimestamp() + (long)(stageDuration.TotalSeconds * Stopwatch.Frequency);
    }

    /// <summary>
    /// Большой буфер этого теста. Выделяется один раз на весь тест (лениво), при нехватке
    /// памяти автоматически уменьшается — фактический объём попадает в отчёт.
    /// Число кусков согласовано с числом потоков, чтобы каждый поток работал со своим участком.
    /// </summary>
    internal BufferPool Pool
    {
        get
        {
            lock (_poolLock)
                return _pool ??= BufferPool.Allocate(Options.MemoryBytes, Options.Seed, Options.EffectiveThreads);
        }
    }

    /// <summary>Отдельный большой буфер для тестов кэша (не менее 128 МБ) — он заполняется один раз на весь тест.</summary>
    internal BufferPool? CachePool
    {
        get => _cachePool;
        set => _cachePool = value;
    }

    /// <summary>Номер этапа (с 1).</summary>
    internal int Stage { get; }

    /// <summary>Параметры запуска.</summary>
    internal StressOptions Options { get; }

    /// <summary>Токен отмены.</summary>
    internal CancellationToken Token { get; }

    /// <summary>Длительность этапа.</summary>
    internal TimeSpan StageDuration { get; }

    /// <summary>Единица измерения пропускной способности теста.</summary>
    internal string ThroughputUnit { get; }

    /// <summary>Метка времени (в тиках Stopwatch), после которой этап обязан завершиться.</summary>
    internal long? Deadline { get; }

    /// <summary>Суммарное число расхождений по всем потокам этапа.</summary>
    internal long Errors => Interlocked.Read(ref _errors);

    /// <summary>Суммарное число обработанных байт по всем потокам.</summary>
    internal double Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Суммарное число операций по всем потокам.</summary>
    internal double Operations => Interlocked.Read(ref _operations);

    /// <summary>Ожидаемое значение первого расхождения (или строка проверки).</summary>
    internal string Expected => Volatile.Read(ref _firstExpected) ?? "совпадает";

    /// <summary>Фактическое значение первого расхождения.</summary>
    internal string Actual => Volatile.Read(ref _firstActual) ?? "совпадает";

    /// <summary>Истина, если этап был прерван.</summary>
    internal bool Aborted => Volatile.Read(ref _aborted) != 0;

    internal Exception? WorkerException => Volatile.Read(ref _workerException);
    internal void RecordWorkerException(Exception exception)
    {
        Interlocked.CompareExchange(ref _workerException, exception, null);
        Abort();
    }

    /// <summary>Принудительно завершает этап (например, при отмене).</summary>
    internal void Abort() => Volatile.Write(ref _aborted, 1);

    /// <summary>Проверяет, что текущее время не превысило дедлайн этапа.</summary>
    internal bool TimeLeft() => !Aborted && !Token.IsCancellationRequested &&
        (!Deadline.HasValue || Stopwatch.GetTimestamp() < Deadline.Value);

    /// <summary>Есть ли ещё время на очередную итерацию длительностью <paramref name="lastIteration"/>.</summary>
    internal bool HasTimeFor(TimeSpan lastIteration)
    {
        if (Aborted || Token.IsCancellationRequested) return false;
        if (!Deadline.HasValue) return true;
        long estimate = Math.Max((long)(lastIteration.TotalSeconds * Stopwatch.Frequency), Stopwatch.Frequency / 2000);
        return Stopwatch.GetTimestamp() + estimate < Deadline.Value;
    }

    /// <summary>Учитывает обработанный объём и число операций (публикуется рабочим потоком).</summary>
    internal void Add(double bytes, double operations)
    {
        if (bytes > 0)
            Interlocked.Add(ref _bytes, (long)bytes);
        if (operations > 0)
            Interlocked.Add(ref _operations, (long)operations);
    }

    /// <summary>Добавляет найденные рабочим потоком расхождения в общий счётчик этапа.</summary>
    internal void AddErrors(long count)
    {
        if (count > 0)
            Interlocked.Add(ref _errors, count);
    }

    /// <summary>Публикует подробности первого расхождения (без блокировки в горячем пути).</summary>
    internal void PublishDetails(string expected, string actual)
    {
        if (Interlocked.Increment(ref _details) > 16)
            return;

        Volatile.Write(ref _firstExpected, expected);
        Volatile.Write(ref _firstActual, actual);
    }

    /// <summary>
    /// Публикует описание успешной проверки. Если расхождение уже зафиксировано,
    /// подробности не перезаписываются — интерфейс должен видеть именно ошибку.
    /// </summary>
    internal void PublishCheck(string expected, string actual)
    {
        if (Interlocked.Read(ref _errors) > 0 || Volatile.Read(ref _firstExpected) is not null)
            return;

        Volatile.Write(ref _firstExpected, expected);
        Volatile.Write(ref _firstActual, actual);
    }

    /// <summary>Регистрирует одно расхождение с подробностями.</summary>
    internal void Fail(string expected, string actual)
    {
        Interlocked.Increment(ref _errors);
        PublishDetails(expected, actual);
    }

    /// <summary>Регистрирует <paramref name="count"/> расхождений без подробностей (быстрый путь).</summary>
    internal void FailFast(long count = 1)
    {
        if (count > 0)
            Interlocked.Add(ref _errors, count);
    }

    /// <summary>Преобразует контекст в фасад рабочего потока.</summary>
    internal WorkerView CreateView(int index)
    {
        var view = new WorkerView(this, index, Options.Seed);
        if (index < _views.Length)
            _views[index] = view;

        Interlocked.Increment(ref _viewCount);
        return view;
    }

    /// <summary>Публикует счётчики всех потоков этапа в общий контекст.</summary>
    internal void CollectAll()
    {
        long count = Math.Min(Interlocked.Read(ref _viewCount), _views.Length);
        for (long i = 0; i < count; i++)
            Volatile.Read(ref _views[i])?.FlushErrors();
    }

    /// <summary>Запоминает созданный тестом файл, чтобы удалить его после прогона.</summary>
    internal void TrackFile(string path)
    {
        lock (_createdFiles)
            _createdFiles.Add(path);
    }

    /// <summary>Удаляет все временные файлы, созданные тестом (в том числе при отмене).</summary>
    internal void CleanupFiles()
    {
        string[] files;
        lock (_createdFiles)
        {
            files = _createdFiles.ToArray();
            _createdFiles.Clear();
        }

        foreach (string path in files)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
                // Файл занят — оставляем его операционной системе, тест всё равно завершён.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>Получатель прогресса с ограничением частоты: интерфейс не должен захлёбываться сообщениями.</summary>
internal sealed class ProgressSink
{
    private readonly IProgress<StressProgress> _progress;
    private readonly int _stageCount;
    private readonly Progress<StressProgress> _relay;
    private double _lastPercent = -100;
    private long _lastTicks;

    internal ProgressSink(IProgress<StressProgress> progress, int stageCount)
    {
        _progress = progress;
        _stageCount = Math.Max(1, stageCount);
        _relay = new Progress<StressProgress>(progress.Report);
        _lastTicks = Stopwatch.GetTimestamp() - (Stopwatch.Frequency / 2);
    }

    /// <summary>Возвращает фасад <see cref="IProgress{T}"/> для передачи внутрь этапа.</summary>
    internal IProgress<StressProgress> AsProgress() => _relay;

    /// <summary>Сообщает о завершении этапа.</summary>
    internal void StageCompleted(int stage, long errors, double throughput, string status)
    {
        _lastPercent = 100.0 * stage / _stageCount;
        _lastTicks = Stopwatch.GetTimestamp();
        _progress.Report(new StressProgress(stage, _stageCount, Math.Min(100, _lastPercent), throughput, errors, status));
    }

    /// <summary>Сообщает о промежуточном состоянии внутри этапа (не чаще двух раз в секунду).</summary>
    internal void Tick(int stage, double overallPercent, double throughput, long errors, string status)
    {
        long now = Stopwatch.GetTimestamp();
        if (overallPercent - _lastPercent < 1.0 && now - _lastTicks < Stopwatch.Frequency / 2)
            return;

        _lastPercent = overallPercent;
        _lastTicks = now;
        _progress.Report(new StressProgress(stage, _stageCount, Math.Clamp(overallPercent, 0, 100), throughput, errors, status));
    }

    /// <summary>Финальное сообщение прогресса.</summary>
    internal void Finish(bool cancelled, long errors, string status)
    {
        string text = cancelled ? "отменено: " + status : "завершено: " + status;
        _progress.Report(new StressProgress(_stageCount, _stageCount, 100, 0, errors, text));
    }
}

/// <summary>
/// Пул больших буферов памяти, устойчивый к нехватке адресуемого места.
/// Память выделяется кусками (chunk); при неудаче размер куска уменьшается вдвое,
/// а фактический объём попадает в отчёт теста.
/// </summary>
internal sealed class BufferPool
{
    private const long MinimumChunk = 4L * 1024 * 1024;

    /// <summary>Куски выравниваются по странице: так целочисленные тесты всегда видят целые слова.</summary>
    private const int ChunkAlignment = 4096;

    private BufferPool(List<byte[]> chunks, long allocated, long requested, List<int> seedValues)
    {
        Chunks = chunks;
        Allocated = allocated;
        Requested = requested;
        ShortfallBytes = Math.Max(0, requested - allocated);
        SeedValues = seedValues;
    }

    /// <summary>Куски памяти (каждый — отдельный массив в куче больших объектов).</summary>
    internal List<byte[]> Chunks { get; }

    /// <summary>Сколько байт реально выделено.</summary>
    internal long Allocated { get; }

    /// <summary>Сколько байт было запрошено.</summary>
    internal long Requested { get; }

    /// <summary>Насколько запрошенный объём пришлось уменьшить.</summary>
    internal long ShortfallBytes { get; }

    /// <summary>Числа, которыми пул был заполнен при создании (пригодится тестам сравнения).</summary>
    internal List<int> SeedValues { get; }

    /// <summary>Первый (рабочий) кусок пула.</summary>
    internal byte[] Primary => Chunks[0];

    /// <summary>Ориентировочный «безопасный» предел одного массива для тестов, которым нужен свой буфер.</summary>
    internal long IndividualBudget => Math.Max(4L * 1024 * 1024, Math.Min(Allocated / 4, 1024L * 1024 * 1024));

    /// <summary>Текст предупреждения о нехватке памяти либо пустая строка.</summary>
    internal string ShortfallNote =>
        ShortfallBytes <= 0
            ? string.Empty
            : $"запрошено {Requested / (1024 * 1024)} МБ, доступно {Allocated / (1024 * 1024)} МБ (уменьшено на {ShortfallBytes / (1024 * 1024)} МБ)";

    /// <summary>Заполняет пул одним значением — «холодный» старт для тестов.</summary>
    internal void Fill(byte value)
    {
        foreach (byte[] chunk in Chunks)
            chunk.AsSpan().Fill(value);
    }

    /// <summary>Выделяет максимально возможный объём, уменьшая его при нехватке памяти.</summary>
    /// <param name="requestedBytes">Желаемый объём.</param>
    /// <param name="seed">Зерно начального заполнения.</param>
    /// <param name="partitionCount">
    /// Желаемое число кусков (обычно число потоков). Многопоточные тесты делят буфер между
    /// потоками по кускам, поэтому кусок не должен быть больше, чем <c>объём / partitionCount</c>.
    /// </param>
    internal static BufferPool Allocate(long requestedBytes, int seed, int partitionCount = 1)
    {
        long requested = Math.Max(20L * 1024 * 1024, requestedBytes);
        int partitions = Math.Max(1, Math.Min(partitionCount, 256));

        // Ограничиваем размер куска так, чтобы кусков гарантированно хватило каждому потоку.
        long maxChunk = Math.Max(MinimumChunk, requested / partitions);
        long chunkSize = Math.Clamp(requested, MinimumChunk, Math.Min(maxChunk, 128L * 1024 * 1024));
        chunkSize -= chunkSize % ChunkAlignment;

        var chunks = new List<byte[]>();
        long allocated = 0;

        while (requested - allocated >= chunkSize)
        {
            byte[]? chunk = TryAllocate((int)Math.Min(chunkSize, int.MaxValue));
            if (chunk is null)
            {
                // Не хватило памяти — уменьшаем кусок; когда уменьшать некуда, прекращаем.
                chunkSize /= 2;
                chunkSize -= chunkSize % ChunkAlignment;
                if (chunkSize < MinimumChunk)
                    break;

                continue;
            }

            chunks.Add(chunk);
            allocated += chunk.Length;
        }

        if (chunks.Count == 0)
        {
            // Совсем не хватило памяти — работаем на минимальном буфере, чтобы тест не падал.
            chunks.Add(new byte[1024 * 1024]);
            allocated = chunks[0].Length;
        }

        var seedValues = new List<int>(chunks.Count);
        for (int i = 0; i < chunks.Count; i++)
            seedValues.Add(unchecked(seed * 31 + 7 + i));

        var pool = new BufferPool(chunks, allocated, requested, seedValues);
        pool.Fill(unchecked((byte)(seed * 31 + 7)));
        return pool;
    }

    private static byte[]? TryAllocate(int size)
    {
        if (size <= 0)
            return null;

        try
        {
            return new byte[size];
        }
        catch (OutOfMemoryException)
        {
            return null;
        }
    }
}
