using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace PcMonitor.Core.Stress;

/// <summary>
/// Категория (группа) стресс-теста. Значения используются интерфейсом для группировки
/// списка тестов и подбора иконок/цветов.
/// </summary>
public enum StressCategory
{
    /// <summary>Тесты процессора: целочисленная арифметика, ветвления, FPU, SIMD.</summary>
    Cpu,

    /// <summary>Тесты оперативной памяти: шаблоны, инверсии, случайный доступ, копирование.</summary>
    Memory,

    /// <summary>Тесты кэш-подсистемы: обходы с разными шагами (L1/L2/L3/RAM).</summary>
    Cache,

    /// <summary>Тесты накопителя: последовательные и произвольные чтение/запись, целостность.</summary>
    Disk,

    /// <summary>Тесты видеоадаптера. Каталог движка их не реализует — тесты добавляет UI.</summary>
    Gpu,
}

/// <summary>
/// Параметры запуска стресс-теста. Один и тот же набор параметров применяется ко всем
/// тестам пакета, конкретный тест использует только нужные ему поля.
/// </summary>
public sealed record StressOptions
{
    /// <summary>Число рабочих потоков. <c>null</c> — использовать все логические процессоры.</summary>
    public int? Threads { get; init; }

    /// <summary>Один непрерывный этап без дедлайна, до явной отмены. По умолчанию выключен.</summary>
    public bool RunUntilStopped { get; init; }

    /// <summary>Сколько оперативной памяти (в мегабайтах) использовать в тестах памяти.</summary>
    public int MemoryMegabytes { get; init; } = 512;

    /// <summary>Папка для файлов тестов диска. Пустая строка — системная временная папка.</summary>
    public string DiskPath { get; init; } = "";

    /// <summary>Размер файла теста диска в байтах (жёсткое ограничение сверху).</summary>
    public long DiskBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Зерно генератора случайных чисел — обеспечивает повторяемость шаблонов.</summary>
    public int Seed { get; init; } = 12345;

    /// <summary>Число этапов для тестов, которые не имеют собственного разбиения. Обычно не задаётся.</summary>
    public int MaxReportedErrors { get; init; } = 8;

    /// <summary>Действующее число потоков: <see cref="Threads"/> либо все логические процессоры.</summary>
    public int EffectiveThreads => Math.Max(1, Threads ?? Environment.ProcessorCount);

    /// <summary>Запрошенный объём памяти в байтах.</summary>
    public long MemoryBytes => Math.Max(64L * 1024, (long)Math.Max(1, MemoryMegabytes) * 1024 * 1024);

    /// <summary>Действующий размер файла теста диска (не меньше 4 МиБ и не больше 4 ГиБ).</summary>
    public long EffectiveDiskBytes => Math.Clamp(DiskBytes, 4L * 1024 * 1024, 4L * 1024 * 1024 * 1024);

    /// <summary>Папка, в которой создаются временные файлы тестов диска.</summary>
    public string DiskDirectory =>
        string.IsNullOrWhiteSpace(DiskPath) ? Path.GetTempPath() : DiskPath;
}

/// <summary>Результат одного этапа: пропускная способность, производительность и найденные расхождения.</summary>
/// <param name="Stage">Номер этапа, начиная с 1.</param>
/// <param name="Throughput">Пропускная способность этапа (МБ/с) или операций/с — зависит от теста.</param>
/// <param name="OperationsPerSecond">Число логических операций (проходов/блоков) в секунду.</param>
/// <param name="Errors">Количество расхождений, обнаруженных на этом этапе.</param>
/// <param name="Expected">Ожидаемое значение (hex/текст) первого расхождения либо строка проверки.</param>
/// <param name="Actual">Фактически полученное значение первого расхождения.</param>
/// <param name="Duration">Фактическая длительность этапа.</param>
public sealed record StressStageResult(
    int Stage,
    double Throughput,
    double OperationsPerSecond,
    long Errors,
    string Expected,
    string Actual,
    TimeSpan Duration);

/// <summary>Текущий прогресс выполнения теста для интерфейса.</summary>
/// <param name="Stage">Текущий (завершённый) этап.</param>
/// <param name="StageCount">Всего этапов у теста.</param>
/// <param name="Percent">Прогресс в процентах, 0..100.</param>
/// <param name="Throughput">Пропускная способность последнего этапа.</param>
/// <param name="Errors">Суммарное число расхождений с начала теста.</param>
/// <param name="Status">Текстовое состояние («этап 3/30», «отменено» и т. п.).</param>
public sealed record StressProgress(
    int Stage,
    int StageCount,
    double Percent,
    double Throughput,
    long Errors,
    string Status)
{
    public TimeSpan Elapsed { get; init; }
    public bool IsIndefinite { get; init; }
}

/// <summary>Итог выполнения теста целиком.</summary>
/// <param name="TestId">Идентификатор теста из каталога.</param>
/// <param name="TestName">Отображаемое имя теста.</param>
/// <param name="Passed">Истина, если тест завершён полностью и не нашёл расхождений.</param>
/// <param name="TotalErrors">Суммарное количество расхождений по всем этапам.</param>
/// <param name="Duration">Полное время выполнения теста.</param>
/// <param name="Stages">Результаты выполненных этапов.</param>
/// <param name="Summary">Краткое человекочитаемое описание результата.</param>
public sealed record StressTestResult(
    string TestId,
    string TestName,
    bool Passed,
    long TotalErrors,
    TimeSpan Duration,
    IReadOnlyList<StressStageResult> Stages,
    string Summary)
{
    /// <summary>Истина, если выполнение было прервано запросом отмены (результат частичный).</summary>
    public bool Cancelled { get; init; }

    /// <summary>Средняя пропускная способность по выполненным этапам.</summary>
    public double AverageThroughput =>
        Stages.Count == 0 ? 0 : Stages.Average(static s => s.Throughput);
}

/// <summary>
/// Один стресс-тест (бенчмарк). Реализации обязаны иметь ровно
/// <see cref="StressTestCatalog.StageCount"/> этапов и поддерживать отмену.
/// </summary>
public interface IStressTest
{
    /// <summary>Стабильный машинный идентификатор, например <c>mem.own-address</c>.</summary>
    string Id { get; }

    /// <summary>Отображаемое имя теста на русском языке.</summary>
    string Name { get; }

    /// <summary>Описание того, что именно проверяет тест.</summary>
    string Description { get; }

    /// <summary>Категория теста.</summary>
    StressCategory Category { get; }

    /// <summary>Количество этапов теста (у всех тестов движка ровно 30).</summary>
    int StageCount { get; }

    /// <summary>Длительность одного этапа — зависит от тяжести теста (от 300 мс до 3 с).</summary>
    TimeSpan StageDuration { get; }

    /// <summary>Единица измерения пропускной способности («МБ/с» или «оп/с»).</summary>
    string ThroughputUnit { get; }

    /// <summary>
    /// Выполняет один этап теста синхронно на вызывающем потоке.
    /// </summary>
    /// <param name="stage">Номер этапа, начиная с 1.</param>
    /// <param name="options">Параметры запуска (память, потоки, диск).</param>
    /// <param name="token">Токен отмены: тест обязан реагировать за 10–50 мс.</param>
    /// <param name="progress">Необязательный получатель прогресса (в том числе внутри этапа).</param>
    /// <returns>Результат этапа; работа может быть прекращена досрочно при отмене.</returns>
    StressStageResult RunStage(int stage, StressOptions options, CancellationToken token, IProgress<StressProgress>? progress);
}

/// <summary>Фабрика тестов движка. Проверяет инварианты и собирает экземпляры <see cref="IStressTest"/>.</summary>
internal static class StressTestFactory
{
    /// <summary>Создаёт тест, проверяя корректность метаданных.</summary>
    internal static IStressTest Create(
        string id,
        StressCategory category,
        string name,
        string description,
        TimeSpan stageDuration,
        Func<WorkerView, StageOutcome> workload,
        bool useManyThreads = false,
        string throughputUnit = "МБ/с")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(workload);

        return new DelegateStressTest(id, category, name, description, stageDuration, workload, useManyThreads, throughputUnit);
    }

    /// <summary>Тест из одного этапа — используется для проверки самого движка.</summary>
    internal static IStressTest CreateSingleStage(string id, string name, string description, TimeSpan stageDuration, Func<WorkerView, StageOutcome> workload) =>
        new SingleStageStressTest(id, name, description, stageDuration, workload);
}

/// <summary>Базовый каркас теста: реализует <see cref="IStressTest"/> через делегат рабочей функции.</summary>
internal sealed class DelegateStressTest : IStressTest
{
    private readonly Func<WorkerView, StageOutcome> _workload;
    private readonly bool _useManyThreads;

    internal DelegateStressTest(
        string id,
        StressCategory category,
        string name,
        string description,
        TimeSpan stageDuration,
        Func<WorkerView, StageOutcome> workload,
        bool useManyThreads,
        string throughputUnit)
    {
        Id = id;
        Category = category;
        Name = name;
        Description = description;
        StageDuration = stageDuration;
        _workload = workload;
        _useManyThreads = useManyThreads;
        ThroughputUnit = throughputUnit;
    }

    public string Id { get; }

    public string Name { get; }

    public string Description { get; }

    public StressCategory Category { get; }

    public int StageCount => StressTestCatalog.TestStageCount;

    public TimeSpan StageDuration { get; }

    public string ThroughputUnit { get; }

    public StressStageResult RunStage(int stage, StressOptions options, CancellationToken token, IProgress<StressProgress>? progress) =>
        StressStageRunner.Execute(this, stage, options, token, progress, _workload, _useManyThreads);
}

/// <summary>Упрощённый тест из одного этапа: применяется для самопроверки инфраструктуры.</summary>
internal sealed class SingleStageStressTest : IStressTest
{
    private readonly Func<WorkerView, StageOutcome> _workload;

    internal SingleStageStressTest(string id, string name, string description, TimeSpan stageDuration, Func<WorkerView, StageOutcome> workload)
    {
        Id = id;
        Name = name;
        Description = description;
        StageDuration = stageDuration;
        _workload = workload;
    }

    public string Id { get; }

    public string Name { get; }

    public string Description { get; }

    public StressCategory Category => StressCategory.Memory;

    public int StageCount => 1;

    public TimeSpan StageDuration { get; }

    public string ThroughputUnit => "МБ/с";

    public StressStageResult RunStage(int stage, StressOptions options, CancellationToken token, IProgress<StressProgress>? progress) =>
        StressStageRunner.Execute(this, stage, options, token, progress, _workload, useManyThreads: false);
}
