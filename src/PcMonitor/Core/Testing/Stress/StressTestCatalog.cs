using System;
using System.Collections.Generic;
using System.Linq;

namespace PcMonitor.Core.Stress;

/// <summary>
/// Каталог всех стресс-тестов и бенчмарков движка.
/// </summary>
/// <remarks>
/// Каталог собирается один раз при первом обращении. Тесты категории <see cref="StressCategory.Gpu"/>
/// движок не реализует — их добавляет интерфейс приложения отдельно.
/// Каждый тест имеет ровно <see cref="TestStageCount"/> этапов, поэтому прогресс всех тестов
/// отображается одинаково.
/// </remarks>
public static class StressTestCatalog
{
    /// <summary>Количество этапов у каждого теста движка.</summary>
    public const int TestStageCount = 30;

    private static readonly IReadOnlyList<IStressTest> Empty = Array.Empty<IStressTest>();
    private static readonly IReadOnlyList<IStressTest> Memory = MemoryStressTests.Build();
    private static readonly IReadOnlyList<IStressTest> Cpu = CpuStressTests.Build();
    private static readonly IReadOnlyList<IStressTest> Cache = CacheStressTests.Build();
    private static readonly IReadOnlyList<IStressTest> Disk = DiskStressTests.Build();
    private static readonly IReadOnlyList<IStressTest> Tests = BuildCatalog();

    /// <summary>Все тесты движка (память, процессор, кэш, диск).</summary>
    public static IReadOnlyList<IStressTest> All => Tests;

    /// <summary>Возвращает тесты выбранной категории (для <see cref="StressCategory.Gpu"/> — пустой список).</summary>
    /// <param name="category">Категория тестов.</param>
    public static IReadOnlyList<IStressTest> ByCategory(StressCategory category)
    {
        switch (category)
        {
            case StressCategory.Memory:
                return Memory;
            case StressCategory.Cpu:
                return Cpu;
            case StressCategory.Cache:
                return Cache;
            case StressCategory.Disk:
                return Disk;
            default:
                // Видеокарты движок не тестирует: категория существует только для интерфейса.
                return Empty;
        }
    }

    /// <summary>Находит тест по идентификатору; возвращает <c>null</c>, если такого теста нет.</summary>
    /// <param name="id">Идентификатор теста, например <c>mem.own-address</c>.</param>
    public static IStressTest? Find(string id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Tests.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Количество тестов в категории.</summary>
    /// <param name="category">Категория тестов.</param>
    public static int Count(StressCategory category) => ByCategory(category).Count;

    /// <summary>
    /// Создаёт тест, «проверяющий сам движок»: один этап, который обязан пройти без ошибок.
    /// Используется утилитой StressCheck и модульными проверками инфраструктуры.
    /// </summary>
    internal static IStressTest CreateSelfCheck() =>
        StressTestFactory.CreateSingleStage(
            "engine.self-check",
            "Самопроверка движка",
            "Проверяет учёт ошибок, замер производительности, работу с буфером и очистку ресурсов.",
            TimeSpan.FromMilliseconds(100),
            SelfCheck);

    /// <summary>Собирает каталог и проверяет инварианты (ровно 30 этапов и уникальные идентификаторы).</summary>
    private static IReadOnlyList<IStressTest> BuildCatalog()
    {
        var tests = new List<IStressTest>(Capacity);
        tests.AddRange(Memory);
        tests.AddRange(Cpu);
        tests.AddRange(Cache);
        tests.AddRange(Disk);

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IStressTest test in tests)
        {
            if (!ids.Add(test.Id))
                throw new InvalidOperationException($"Дублирующийся идентификатор стресс-теста: {test.Id}");

            if (test.StageCount != TestStageCount)
                throw new InvalidOperationException($"Тест {test.Id} содержит {test.StageCount} этапов вместо {TestStageCount}.");

            if (test.StageDuration <= TimeSpan.Zero)
                throw new InvalidOperationException($"Тест {test.Id} имеет некорректную длительность этапа.");
        }

        return tests.AsReadOnly();
    }

    private const int Capacity = 64;

    /// <summary>Тело самопроверки: убеждаемся, что счётчики и эталонные буферы работают как ожидается.</summary>
    private static StageOutcome SelfCheck(WorkerView w)
    {
        byte[] buffer = w.Stage.Pool.Primary;
        if (buffer.Length < 64)
        {
            w.Fail("буфер не меньше 64 байт", $"буфер {buffer.Length} байт");
            return new StageOutcome { Expected = "буфер выделен", Actual = "буфер слишком мал" };
        }

        // Заведомо неверное сравнение должно привести к ошибке, а верное — нет.
        w.Fail("0xAA", "0x55");
        w.Check("0xAA", "0x55");

        return new StageOutcome
        {
            Bytes = buffer.Length,
            Operations = buffer.Length,
            Expected = "движок фиксирует расхождения и считает объём",
            Actual = "проверка выполнена",
        };
    }
}
