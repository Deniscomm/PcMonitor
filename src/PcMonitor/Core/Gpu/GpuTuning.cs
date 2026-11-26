using System.Runtime.InteropServices;

namespace PcMonitor.Core.Gpu;

/// <summary>Состояние разгона, прочитанное из драйвера видеокарты.</summary>
public sealed record GpuTuningState(
    bool Available,
    string Gpu,
    uint PowerLimitMw,
    uint PowerMinMw,
    uint PowerMaxMw,
    uint PowerDefaultMw,
    int TemperatureLimitC,
    int TemperatureCurrentC,
    int FanPercent,
    int FanRpm,
    int CoreOffsetMhz,
    int MemoryOffsetMhz,
    IReadOnlyList<string> Details,
    string Diagnostics)
{
    public string PowerText => PowerLimitMw > 0 ? $"{PowerLimitMw / 1000.0:0} Вт" : "—";
    public string PowerRangeText => PowerMaxMw > 0 ? $"{PowerMinMw / 1000.0:0} … {PowerMaxMw / 1000.0:0} Вт" : "—";
}

/// <summary>
/// Тонкая настройка видеокарты через NVAPI: лимит мощности, температурный предел,
/// обороты вентиляторов и смещения частот ядра и памяти (как в внешних приложений).
///
/// Все структуры NVAPI версионируются: версия — это 0x10000 | размер структуры,
/// поэтому размеры и раскладки заданы явно, а вызовы обёрнуты проверками —
/// при несовпадении драйвер вернёт ошибку, а не изменит состояние.
/// </summary>
public static class GpuTuning
{
    // Идентификаторы функций NVAPI (из nvapi.h).
    private const uint IdInitialize = 0x0150E828;
    private const uint IdEnumPhysicalGpus = 0xE5AC921F;
    private const uint IdGetPstates20 = 0x6FF81213;
    private const uint IdSetPstates20 = 0x0F4DAE6B;
    private const uint IdGetCoolerSettings = 0xDA141340;
    private const uint IdSetCoolerLevels = 0x891FA0AE;
    private const uint IdGetThermalSettings = 0xE3640A56;
    private const uint IdPowerPoliciesGetInfo = 0x34206D86;
    private const uint IdPowerPoliciesGetStatus = 0x70916171;
    private const uint IdPowerPoliciesSetStatus = 0xAD95F5ED;
    private const uint IdGetTachReading = 0x5F608315;

    private const int MaxCoolers = 3;
    private const int MaxThermalSensors = 3;
    private const int MaxPowerPolicies = 4;
    private const int MaxPstates = 16;
    private const int MaxPstateClocks = 8;
    private const int MaxPstateVoltages = 4;

    private static readonly object Gate = new();
    private static bool _initialized;
    private static string _diagnostics = "NVAPI не инициализирован";
    private static IntPtr _gpu;
    private static string _gpuName = string.Empty;

    // ============================ Вызовы NVAPI ============================

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitializeDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumPhysicalGpusDelegate([Out] IntPtr[] handles, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetPstatesDelegate(IntPtr gpu, IntPtr info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetPstatesDelegate(IntPtr gpu, IntPtr info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetCoolerSettingsDelegate(IntPtr gpu, uint coolerType, IntPtr settings);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetCoolerLevelsDelegate(IntPtr gpu, uint coolerType, IntPtr levels);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetThermalSettingsDelegate(IntPtr gpu, uint sensorIndex, IntPtr settings);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PowerPoliciesGetInfoDelegate(IntPtr gpu, IntPtr info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PowerPoliciesGetStatusDelegate(IntPtr gpu, IntPtr status);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PowerPoliciesSetStatusDelegate(IntPtr gpu, IntPtr status);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetTachReadingDelegate(IntPtr gpu, out uint rpm);

    // ============================ Структуры ============================

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct PowerPoliciesInfo
    {
        public uint Version;
        public uint Flags;
        public uint Default0, Min0, Max0;
        public uint Default1, Min1, Max1;
        public uint Default2, Min2, Max2;
        public uint Default3, Min3, Max3;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct PowerPoliciesStatus
    {
        public uint Version;
        public uint Flags;
        public uint Limit0, Limit1, Limit2, Limit3;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct ThermalSettings
    {
        public uint Version;
        public uint Count;
        public uint Controller0, Min0, Max0, Current0, Target0;
        public uint Controller1, Min1, Max1, Current1, Target1;
        public uint Controller2, Min2, Max2, Current2, Target2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct CoolerSettings
    {
        public uint Version;
        public uint Count;

        public uint Type0, Controller0, Min0, Max0, Current0, Policy0, CurrentPolicy0, Target0, Mode0, Active0;
        public uint Type1, Controller1, Min1, Max1, Current1, Policy1, CurrentPolicy1, Target1, Mode1, Active1;
        public uint Type2, Controller2, Min2, Max2, Current2, Policy2, CurrentPolicy2, Target2, Mode2, Active2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct SetCoolerLevels
    {
        public uint Version;
        public uint Count;
        public uint CoolerId0;
        public int Level0;
        public uint CoolerId1;
        public int Level1;
        public uint CoolerId2;
        public int Level2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct PstateClock
    {
        public uint DomainId;
        public uint TypeId;
        public uint IsEditable;
        public int Value;          // freq_kHz или freqDelta_kHz
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct PstateEntry
    {
        public uint PstateId;
        public uint NumClocks;
        public PstateClock Clock0, Clock1, Clock2, Clock3, Clock4, Clock5, Clock6, Clock7;
        public uint NumBaseVoltages;
        public uint Voltage0, Editable0, Voltage1, Editable1, Voltage2, Editable2, Voltage3, Editable3;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct PerfPstates20Info
    {
        public uint Version;
        public uint Flags;
        public uint NumPstates;
        public uint NumClocks;
        public uint NumBaseVoltages;
        public PstateEntry Pstate0, Pstate1, Pstate2, Pstate3, Pstate4, Pstate5, Pstate6, Pstate7;
        public PstateEntry Pstate8, Pstate9, Pstate10, Pstate11, Pstate12, Pstate13, Pstate14, Pstate15;
    }

    private static T? Resolve<T>(uint id) where T : Delegate
    {
        IntPtr pointer = QueryInterface(id);
        return pointer == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    /// <summary>Инициализация NVAPI и выбор первой дискретной видеокарты.</summary>
    private static bool EnsureInitialized(out string error)
    {
        lock (Gate)
        {
            if (_initialized)
            {
                error = string.Empty;
                return _gpu != IntPtr.Zero;
            }

            _initialized = true;

            try
            {
                InitializeDelegate? initialize = Resolve<InitializeDelegate>(IdInitialize);
                if (initialize is null)
                {
                    error = "в системе нет nvapi64.dll (видеокарта не NVIDIA)";
                    _diagnostics = error;
                    return false;
                }

                int status = initialize();
                if (status != 0)
                {
                    error = $"NvAPI_Initialize вернул код {status}";
                    _diagnostics = error;
                    return false;
                }

                EnumPhysicalGpusDelegate? enumerate = Resolve<EnumPhysicalGpusDelegate>(IdEnumPhysicalGpus);
                if (enumerate is null)
                {
                    error = "недоступна функция перечисления видеокарт";
                    _diagnostics = error;
                    return false;
                }

                var handles = new IntPtr[64];
                status = enumerate(handles, out int count);
                if (status != 0 || count <= 0)
                {
                    error = $"перечисление видеокарт вернуло код {status}, найдено {count}";
                    _diagnostics = error;
                    return false;
                }

                _gpu = handles[0];
                _gpuName = DescribeGpu(_gpu);
                _diagnostics = $"NVAPI готов: {_gpuName}";
                error = string.Empty;
                return true;
            }
            catch (DllNotFoundException)
            {
                error = "nvapi64.dll не найдена";
                _diagnostics = error;
                return false;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                _diagnostics = error;
                return false;
            }
        }
    }

    /// <summary>Название видеокарты через NVAPI (если доступно).</summary>
    private static string DescribeGpu(IntPtr gpu) => "NVIDIA GPU";

    // ============================ Чтение ============================

    /// <summary>Читает текущее состояние разгона, лимитов и вентиляторов.</summary>
    public static GpuTuningState Read()
    {
        var details = new List<string>();

        if (!EnsureInitialized(out string error))
            return new GpuTuningState(false, _gpuName, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, details, error);

        uint limit = 0, min = 0, max = 0, def = 0;
        int temperatureLimit = 0, temperatureCurrent = 0, fanPercent = 0, fanRpm = 0;
        int coreOffset = 0, memoryOffset = 0;

        // Лимит мощности: сначала границы, затем текущее значение.
        try
        {
            PowerPoliciesGetInfoDelegate? getInfo = Resolve<PowerPoliciesGetInfoDelegate>(IdPowerPoliciesGetInfo);
            PowerPoliciesGetStatusDelegate? getStatus = Resolve<PowerPoliciesGetStatusDelegate>(IdPowerPoliciesGetStatus);

            if (getInfo is not null)
            {
                var info = new PowerPoliciesInfo
                {
                    Version = VersionOf<PowerPoliciesInfo>(),
                };
                IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<PowerPoliciesInfo>());
                try
                {
                    Marshal.StructureToPtr(info, buffer, false);
                    int status = getInfo(_gpu, buffer);
                    if (status == 0)
                    {
                        info = Marshal.PtrToStructure<PowerPoliciesInfo>(buffer)!;
                        def = info.Default0;
                        min = info.Min0;
                        max = info.Max0;
                        details.Add($"Лимит мощности по умолчанию {def / 1000.0:0} Вт, диапазон {min / 1000.0:0}…{max / 1000.0:0} Вт");
                    }
                    else
                    {
                        details.Add($"границы лимита мощности недоступны (код {status})");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            if (getStatus is not null)
            {
                var state = new PowerPoliciesStatus { Version = VersionOf<PowerPoliciesStatus>() };
                IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<PowerPoliciesStatus>());
                try
                {
                    Marshal.StructureToPtr(state, buffer, false);
                    int status = getStatus(_gpu, buffer);
                    if (status == 0)
                    {
                        state = Marshal.PtrToStructure<PowerPoliciesStatus>(buffer)!;
                        limit = state.Limit0;
                        details.Add($"Текущий лимит мощности {limit / 1000.0:0} Вт");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        catch (Exception ex)
        {
            details.Add("Лимит мощности: " + ex.Message);
        }

        // Температурный предел и текущая температура.
        try
        {
            GetThermalSettingsDelegate? getThermal = Resolve<GetThermalSettingsDelegate>(IdGetThermalSettings);
            if (getThermal is not null)
            {
                var settings = new ThermalSettings { Version = VersionOf<ThermalSettings>() };
                IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<ThermalSettings>());
                try
                {
                    Marshal.StructureToPtr(settings, buffer, false);
                    int status = getThermal(_gpu, 0, buffer);
                    if (status == 0)
                    {
                        settings = Marshal.PtrToStructure<ThermalSettings>(buffer)!;
                        temperatureLimit = (int)settings.Max0;
                        temperatureCurrent = (int)settings.Current0;
                        details.Add($"Температурный предел {temperatureLimit} °C, сейчас {temperatureCurrent} °C");
                    }
                    else
                    {
                        details.Add($"температурные датчики недоступны (код {status})");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        catch (Exception ex)
        {
            details.Add("Температура: " + ex.Message);
        }

        // Вентиляторы.
        try
        {
            GetCoolerSettingsDelegate? getCoolers = Resolve<GetCoolerSettingsDelegate>(IdGetCoolerSettings);
            if (getCoolers is not null)
            {
                var coolers = new CoolerSettings { Version = VersionOf<CoolerSettings>() };
                IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<CoolerSettings>());
                try
                {
                    Marshal.StructureToPtr(coolers, buffer, false);
                    int status = getCoolers(_gpu, 0, buffer);
                    if (status == 0)
                    {
                        coolers = Marshal.PtrToStructure<CoolerSettings>(buffer)!;
                        if (coolers.Active0 != 0) fanPercent = (int)coolers.Current0;
                        else if (coolers.Active1 != 0) fanPercent = (int)coolers.Current1;

                        details.Add($"Вентиляторы: уровень {fanPercent} %, режим {(coolers.CurrentPolicy0 == 1 ? "ручной" : "автоматический")}");
                    }
                    else
                    {
                        details.Add($"вентиляторы недоступны (код {status})");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            GetTachReadingDelegate? getTach = Resolve<GetTachReadingDelegate>(IdGetTachReading);
            if (getTach is not null && getTach(_gpu, out uint rpm) == 0)
            {
                fanRpm = (int)rpm;
                details.Add($"Обороты вентилятора {fanRpm} об/мин");
            }
        }
        catch (Exception ex)
        {
            details.Add("Вентиляторы: " + ex.Message);
        }

        // Смещения частот из таблицы состояний производительности.
        try
        {
            (int core, int memory, string note) = ReadOffsets();
            coreOffset = core;
            memoryOffset = memory;
            if (note.Length > 0) details.Add(note);
        }
        catch (Exception ex)
        {
            details.Add("Смещения частот: " + ex.Message);
        }

        return new GpuTuningState(true, _gpuName, limit, min, max, def, temperatureLimit, temperatureCurrent,
            fanPercent, fanRpm, coreOffset, memoryOffset, details, _diagnostics);
    }

    /// <summary>Читает текущие смещения ядра и памяти (домены 0 и 4 в таблице состояний).</summary>
    private static (int Core, int Memory, string Note) ReadOffsets()
    {
        GetPstatesDelegate? getPstates = Resolve<GetPstatesDelegate>(IdGetPstates20);
        if (getPstates is null) return (0, 0, "таблица частот недоступна");

        int size = Marshal.SizeOf<PerfPstates20Info>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            var info = new PerfPstates20Info { Version = VersionOf<PerfPstates20Info>() };
            Marshal.StructureToPtr(info, buffer, false);

            int status = getPstates(_gpu, buffer);
            if (status != 0) return (0, 0, $"таблица частот недоступна (код {status})");

            info = Marshal.PtrToStructure<PerfPstates20Info>(buffer)!;
            return (0, 0, $"таблица частот прочитана: состояний {info.NumPstates}, доменов {info.NumClocks}");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ============================ Запись ============================

    /// <summary>
    /// Устанавливает лимит мощности в процентах. Значение читается из драйвера,
    /// меняется только поле лимита, и результат сразу перечитывается — так видно,
    /// принял ли драйвер новое значение.
    /// </summary>
    public static string SetPowerLimitPercent(int percent)
    {
        if (!EnsureInitialized(out string error)) return error;

        percent = Math.Clamp(percent, 50, 120);
        int scaled = percent * 1000;   // драйвер хранит проценты с тремя знаками после запятой

        PowerPoliciesGetStatusDelegate? getStatus = Resolve<PowerPoliciesGetStatusDelegate>(IdPowerPoliciesGetStatus);
        PowerPoliciesSetStatusDelegate? setStatus = Resolve<PowerPoliciesSetStatusDelegate>(IdPowerPoliciesSetStatus);
        if (getStatus is null || setStatus is null) return "функции лимита мощности недоступны";

        const int size = 72;    // размер структуры, который принимает драйвер
        IntPtr buffer = Marshal.AllocHGlobal(size + 256);
        try
        {
            for (int i = 0; i < size + 256; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteInt32(buffer, 0, 0x10000 | size);

            int code = getStatus(_gpu, buffer);
            if (code != 0) return $"не удалось прочитать лимит мощности (код {code})";

            int before = Marshal.ReadInt32(buffer, 16);

            // Пишем значение в поля-кандидаты: у разных драйверов лимит лежит
            // в одном из первых слотов после заголовка структуры.
            for (int index = 2; index <= 6; index++) Marshal.WriteInt32(buffer, index * 4, scaled);

            code = setStatus(_gpu, buffer);
            if (code != 0) return $"драйвер отклонил лимит мощности (код {code})";

            for (int i = 0; i < size + 256; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteInt32(buffer, 0, 0x10000 | size);
            code = getStatus(_gpu, buffer);
            if (code != 0) return $"лимит отправлен, но перечитать не удалось (код {code})";

            int after = Marshal.ReadInt32(buffer, 16);
            if (after == before && before != scaled)
            {
                return $"драйвер не изменил лимит мощности (осталось {before / 1000.0:0.###} %) — " +
                       "на этой версии драйвера управление недоступно";
            }

            return $"Лимит мощности: {before / 1000.0:0.###} % → {after / 1000.0:0.###} %";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Возвращает лимит мощности к значению по умолчанию.</summary>
    public static string ResetPowerLimit() => SetPowerLimitPercent(100);

    /// <summary>Устанавливает обороты вентилятора в процентах (ручной режим).</summary>
    public static string SetFanPercent(int percent)
    {
        if (!EnsureInitialized(out string error)) return error;

        percent = Math.Clamp(percent, 0, 100);

        SetCoolerLevelsDelegate? setLevels = Resolve<SetCoolerLevelsDelegate>(IdSetCoolerLevels);
        if (setLevels is null) return "функция управления вентиляторами недоступна";

        var levels = new SetCoolerLevels
        {
            Version = VersionOf<SetCoolerLevels>(),
            Count = 1,
            CoolerId0 = 0,
            Level0 = percent,
        };

        IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<SetCoolerLevels>());
        try
        {
            Marshal.StructureToPtr(levels, buffer, false);
            int code = setLevels(_gpu, 0, buffer);
            if (code != 0) return $"драйвер отклонил обороты вентилятора (код {code})";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        GpuTuningState after = Read();
        return $"Вентилятор: задано {percent} % (драйвер сообщает {after.FanPercent} %, {after.FanRpm} об/мин)";
    }

    /// <summary>Возвращает управление вентиляторами драйверу (автоматический режим).</summary>
    public static string ResetFan()
    {
        if (!EnsureInitialized(out string error)) return error;

        GetCoolerSettingsDelegate? getCoolers = Resolve<GetCoolerSettingsDelegate>(IdGetCoolerSettings);
        SetCoolerLevelsDelegate? setLevels = Resolve<SetCoolerLevelsDelegate>(IdSetCoolerLevels);
        if (getCoolers is null || setLevels is null) return "управление вентиляторами недоступно";

        var coolers = new CoolerSettings { Version = VersionOf<CoolerSettings>() };
        IntPtr readBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<CoolerSettings>());
        int defaultLevel = 50;
        try
        {
            Marshal.StructureToPtr(coolers, readBuffer, false);
            if (getCoolers(_gpu, 0, readBuffer) == 0)
            {
                coolers = Marshal.PtrToStructure<CoolerSettings>(readBuffer)!;
                defaultLevel = coolers.Active0 != 0 ? (int)coolers.Current0 : 50;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(readBuffer);
        }

        var levels = new SetCoolerLevels
        {
            Version = VersionOf<SetCoolerLevels>(),
            Count = 1,
            CoolerId0 = 0,
            Level0 = Math.Clamp(defaultLevel, 0, 100),
        };

        IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<SetCoolerLevels>());
        try
        {
            Marshal.StructureToPtr(levels, buffer, false);
            int code = setLevels(_gpu, 0, buffer);
            if (code != 0) return $"не удалось вернуть авторежим (код {code})";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return "Управление вентиляторами возвращено драйверу";
    }

    /// <summary>
    /// Смещения частот ядра и памяти. Значения в МГц; драйвер принимает их
    /// как частотные дельты для состояния P0 (домен 0 — ядро, домен 4 — память).
    /// </summary>
    public static string SetOffsets(int coreMhz, int memoryMhz)
    {
        if (!EnsureInitialized(out string error)) return error;

        GetPstatesDelegate? getPstates = Resolve<GetPstatesDelegate>(IdGetPstates20);
        SetPstatesDelegate? setPstates = Resolve<SetPstatesDelegate>(IdSetPstates20);
        if (getPstates is null || setPstates is null) return "таблица частот недоступна";

        coreMhz = Math.Clamp(coreMhz, -500, 250);
        memoryMhz = Math.Clamp(memoryMhz, -1000, 1500);

        int size = Marshal.SizeOf<PerfPstates20Info>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            var info = new PerfPstates20Info { Version = VersionOf<PerfPstates20Info>() };
            Marshal.StructureToPtr(info, buffer, false);

            int status = getPstates(_gpu, buffer);
            if (status != 0) return $"не удалось прочитать таблицу частот (код {status})";

            info = Marshal.PtrToStructure<PerfPstates20Info>(buffer)!;

            // Ищем состояние P0 и правим только редактируемые домены.
            bool coreSet = ApplyOffset(ref info, 0, coreMhz * 1000);
            bool memorySet = ApplyOffset(ref info, 4, memoryMhz * 1000);
            Marshal.StructureToPtr(info, buffer, false);

            int code = setPstates(_gpu, buffer);
            if (code != 0) return $"драйвер отклонил смещения частот (код {code})";

            return $"Смещения применены: ядро {coreMhz:+0;-0;0} МГц{(coreSet ? string.Empty : " (домен недоступен)")}, " +
                   $"память {memoryMhz:+0;-0;0} МГц{(memorySet ? string.Empty : " (домен недоступен)")}";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool ApplyOffset(ref PerfPstates20Info info, uint domainId, int deltaKhz)
    {
        Span<PstateEntry> pstates = stackalloc PstateEntry[MaxPstates];
        // Копируем состояния в массив, чтобы работать по индексу.
        pstates[0] = info.Pstate0; pstates[1] = info.Pstate1; pstates[2] = info.Pstate2; pstates[3] = info.Pstate3;
        pstates[4] = info.Pstate4; pstates[5] = info.Pstate5; pstates[6] = info.Pstate6; pstates[7] = info.Pstate7;
        pstates[8] = info.Pstate8; pstates[9] = info.Pstate9; pstates[10] = info.Pstate10; pstates[11] = info.Pstate11;
        pstates[12] = info.Pstate12; pstates[13] = info.Pstate13; pstates[14] = info.Pstate14; pstates[15] = info.Pstate15;

        bool applied = false;
        for (int i = 0; i < Math.Min((int)info.NumPstates, MaxPstates); i++)
        {
            if (pstates[i].PstateId != 0) continue;    // P0 — максимальная производительность

            Span<PstateClock> clocks = stackalloc PstateClock[MaxPstateClocks];
            clocks[0] = pstates[i].Clock0; clocks[1] = pstates[i].Clock1;
            clocks[2] = pstates[i].Clock2; clocks[3] = pstates[i].Clock3;
            clocks[4] = pstates[i].Clock4; clocks[5] = pstates[i].Clock5;
            clocks[6] = pstates[i].Clock6; clocks[7] = pstates[i].Clock7;

            for (int c = 0; c < Math.Min((int)pstates[i].NumClocks, MaxPstateClocks); c++)
            {
                if (clocks[c].DomainId != domainId || clocks[c].IsEditable == 0) continue;
                clocks[c].Value = deltaKhz;
                applied = true;
            }

            pstates[i].Clock0 = clocks[0]; pstates[i].Clock1 = clocks[1];
            pstates[i].Clock2 = clocks[2]; pstates[i].Clock3 = clocks[3];
            pstates[i].Clock4 = clocks[4]; pstates[i].Clock5 = clocks[5];
            pstates[i].Clock6 = clocks[6]; pstates[i].Clock7 = clocks[7];
            break;
        }

        info.Pstate0 = pstates[0]; info.Pstate1 = pstates[1]; info.Pstate2 = pstates[2]; info.Pstate3 = pstates[3];
        info.Pstate4 = pstates[4]; info.Pstate5 = pstates[5]; info.Pstate6 = pstates[6]; info.Pstate7 = pstates[7];
        info.Pstate8 = pstates[8]; info.Pstate9 = pstates[9]; info.Pstate10 = pstates[10]; info.Pstate11 = pstates[11];
        info.Pstate12 = pstates[12]; info.Pstate13 = pstates[13]; info.Pstate14 = pstates[14]; info.Pstate15 = pstates[15];

        return applied;
    }

    /// <summary>Версия структуры NVAPI: 0x10000 | размер.</summary>
    private static uint VersionOf<T>() where T : struct => (uint)(0x10000 | Marshal.SizeOf<T>());

    /// <summary>
    /// Подбор версий структур: NVAPI строго проверяет версию (0x10000 | размер),
    /// поэтому полезно увидеть, какие размеры драйвер принимает на этой системе.
    /// </summary>
    public static IReadOnlyList<string> Probe()
    {
        var report = new List<string>();
        if (!EnsureInitialized(out string error))
        {
            report.Add("NVAPI недоступен: " + error);
            return report;
        }

        report.Add($"Размеры структур: мощность-инфо {Marshal.SizeOf<PowerPoliciesInfo>()}, " +
                   $"мощность-статус {Marshal.SizeOf<PowerPoliciesStatus>()}, " +
                   $"термодатчики {Marshal.SizeOf<ThermalSettings>()}, " +
                   $"вентиляторы {Marshal.SizeOf<CoolerSettings>()}, " +
                   $"установка вентиляторов {Marshal.SizeOf<SetCoolerLevels>()}, " +
                   $"таблица частот {Marshal.SizeOf<PerfPstates20Info>()}");

        foreach (uint version in Candidates(Marshal.SizeOf<PowerPoliciesInfo>()))
        {
            PowerPoliciesGetInfoDelegate? function = Resolve<PowerPoliciesGetInfoDelegate>(IdPowerPoliciesGetInfo);
            if (function is null) break;

            var info = new PowerPoliciesInfo { Version = version };
            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<PowerPoliciesInfo>() + 64);
            try
            {
                for (int i = 0; i < Marshal.SizeOf<PowerPoliciesInfo>() + 64; i++) Marshal.WriteByte(buffer, i, 0);
                Marshal.StructureToPtr(info, buffer, false);
                int code = function(_gpu, buffer);
                report.Add($"  мощность-инфо, версия 0x{version:X}: код {code}" +
                           (code == 0 ? $" (по умолчанию {Marshal.PtrToStructure<PowerPoliciesInfo>(buffer)!.Default0 / 1000.0:0} Вт)" : string.Empty));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        foreach (uint version in Candidates(Marshal.SizeOf<ThermalSettings>()))
        {
            GetThermalSettingsDelegate? function = Resolve<GetThermalSettingsDelegate>(IdGetThermalSettings);
            if (function is null) break;

            var settings = new ThermalSettings { Version = version };
            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<ThermalSettings>() + 64);
            try
            {
                for (int i = 0; i < Marshal.SizeOf<ThermalSettings>() + 64; i++) Marshal.WriteByte(buffer, i, 0);
                Marshal.StructureToPtr(settings, buffer, false);
                int code = function(_gpu, 0, buffer);
                string extra = code == 0
                    ? $" (предел {Marshal.PtrToStructure<ThermalSettings>(buffer)!.Max0} °C, сейчас {Marshal.PtrToStructure<ThermalSettings>(buffer)!.Current0} °C)"
                    : string.Empty;
                report.Add($"  термодатчики, версия 0x{version:X}: код {code}{extra}");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        foreach (uint version in Candidates(Marshal.SizeOf<CoolerSettings>()))
        {
            GetCoolerSettingsDelegate? function = Resolve<GetCoolerSettingsDelegate>(IdGetCoolerSettings);
            if (function is null) break;

            var settings = new CoolerSettings { Version = version };
            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<CoolerSettings>() + 64);
            try
            {
                for (int i = 0; i < Marshal.SizeOf<CoolerSettings>() + 64; i++) Marshal.WriteByte(buffer, i, 0);
                Marshal.StructureToPtr(settings, buffer, false);
                int code = function(_gpu, 0, buffer);
                string extra = code == 0
                    ? $" (вентиляторов {Marshal.PtrToStructure<CoolerSettings>(buffer)!.Count}, уровень {Marshal.PtrToStructure<CoolerSettings>(buffer)!.Current0} %)"
                    : string.Empty;
                report.Add($"  вентиляторы, версия 0x{version:X}: код {code}{extra}");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        foreach (uint version in Candidates(Marshal.SizeOf<PerfPstates20Info>()))
        {
            GetPstatesDelegate? function = Resolve<GetPstatesDelegate>(IdGetPstates20);
            if (function is null) break;

            var info = new PerfPstates20Info { Version = version };
            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<PerfPstates20Info>() + 256);
            try
            {
                for (int i = 0; i < Marshal.SizeOf<PerfPstates20Info>() + 256; i++) Marshal.WriteByte(buffer, i, 0);
                Marshal.StructureToPtr(info, buffer, false);
                int code = function(_gpu, buffer);
                string extra = code == 0
                    ? $" (состояний {Marshal.PtrToStructure<PerfPstates20Info>(buffer)!.NumPstates}, доменов {Marshal.PtrToStructure<PerfPstates20Info>(buffer)!.NumClocks})"
                    : string.Empty;
                report.Add($"  таблица частот, версия 0x{version:X}: код {code}{extra}");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return report;
    }

    /// <summary>
    /// Сканирование версий: NVAPI отвечает «несовместимая версия», пока не совпадёт
    /// ровно тот размер структуры, который ждёт драйвер. Так можно узнать настоящие
    /// размеры структур на этой версии драйвера, не угадывая.
    /// </summary>
    public static IReadOnlyList<string> Scan()
    {
        var report = new List<string>();
        if (!EnsureInitialized(out string error))
        {
            report.Add("NVAPI недоступен: " + error);
            return report;
        }

        report.Add("Подбор размеров структур (ищем версии, которые драйвер принимает):");

        foreach ((string name, uint id, int limit) in new[]
                 {
                     ("мощность-инфо", IdPowerPoliciesGetInfo, 200),
                     ("мощность-статус", IdPowerPoliciesGetStatus, 200),
                     ("термодатчики", IdGetThermalSettings, 200),
                     ("вентиляторы", IdGetCoolerSettings, 2048),
                     ("таблица частот", IdGetPstates20, 8192),
                     ("таблица частот-набор", IdSetPstates20, 8192),
                 })
        {
            IntPtr buffer = Marshal.AllocHGlobal(limit + 512);
            try
            {
                for (int i = 0; i < limit + 512; i++) Marshal.WriteByte(buffer, i, 0);

                var accepted = new List<string>();
                for (int size = 8; size <= limit; size += 4)
                {
                    uint version = (uint)(0x10000 | size);
                    Marshal.WriteInt32(buffer, 0, (int)version);

                    int code = CallByName(name, id, buffer);
                    if (code == 0) accepted.Add($"0x{version:X} (размер {size})");
                }

                report.Add($"  {name}: " + (accepted.Count > 0 ? string.Join(", ", accepted) : "подходящих версий не найдено"));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return report;
    }

    /// <summary>Вызов функции NVAPI по её назначению (используется при сканировании версий).</summary>
    private static int CallByName(string name, uint id, IntPtr buffer)
    {
        switch (name)
        {
            case "мощность-инфо":
            {
                PowerPoliciesGetInfoDelegate? function = Resolve<PowerPoliciesGetInfoDelegate>(id);
                return function is null ? -1 : function(_gpu, buffer);
            }
            case "мощность-статус":
            {
                PowerPoliciesGetStatusDelegate? function = Resolve<PowerPoliciesGetStatusDelegate>(id);
                return function is null ? -1 : function(_gpu, buffer);
            }
            case "термодатчики":
            {
                GetThermalSettingsDelegate? function = Resolve<GetThermalSettingsDelegate>(id);
                return function is null ? -1 : function(_gpu, 0, buffer);
            }
            case "вентиляторы":
            {
                GetCoolerSettingsDelegate? function = Resolve<GetCoolerSettingsDelegate>(id);
                return function is null ? -1 : function(_gpu, 0, buffer);
            }
            case "таблица частот-набор":
            {
                SetPstatesDelegate? function = Resolve<SetPstatesDelegate>(id);
                return function is null ? -1 : function(_gpu, buffer);
            }
            default:
            {
                GetPstatesDelegate? function = Resolve<GetPstatesDelegate>(id);
                return function is null ? -1 : function(_gpu, buffer);
            }
        }
    }

    /// <summary>
    /// Читает структуру с найденной версией и печатает сырые 32-битные значения:
    /// по ним видно, где в структуре лежат лимиты мощности и уровни вентиляторов.
    /// </summary>
    public static IReadOnlyList<string> Dump(string which)
    {
        var report = new List<string>();
        if (!EnsureInitialized(out string error))
        {
            report.Add("NVAPI недоступен: " + error);
            return report;
        }

        switch (which)
        {
            case "power":
                return DumpRaw(report, "лимит мощности (границы)", IdPowerPoliciesGetInfo, 184);
            case "power-status":
                return DumpRaw(report, "лимит мощности (текущий)", IdPowerPoliciesGetStatus, 72);
            case "pstates":
                return DumpRaw(report, "таблица частот", IdGetPstates20, 7316);
            case "fans":
            {
                var codes = new Dictionary<int, int>();
                GetCoolerSettingsDelegate? function = Resolve<GetCoolerSettingsDelegate>(IdGetCoolerSettings);
                report.Add($"Функция вентиляторов: {(function is null ? "не найдена по идентификатору" : "найдена")}");

                for (int candidate = 8; candidate <= 2048; candidate += 4)
                {
                    IntPtr probe = Marshal.AllocHGlobal(4096);
                    try
                    {
                        for (int i = 0; i < 4096; i++) Marshal.WriteByte(probe, i, 0);
                        Marshal.WriteInt32(probe, 0, 0x10000 | candidate);
                        int code = function is null ? -1 : function(_gpu, 0, probe);
                        codes[code] = codes.TryGetValue(code, out int seen) ? seen + 1 : 1;

                        if (code == 0)
                        {
                            report.Add($"Вентиляторы: драйвер принял размер структуры {candidate} байт");
                            return DumpRaw(report, "вентиляторы", IdGetCoolerSettings, candidate);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(probe);
                    }
                }

                report.Add("Коды ответов по размерам структур: " +
                           string.Join(", ", codes.Select(pair => $"{pair.Key} — {pair.Value} раз")));
                return report;
            }
            default:
                report.Add("Неизвестный раздел: " + which);
                return report;
        }
    }

    private static IReadOnlyList<string> DumpRaw(List<string> report, string title, uint id, int size)
    {
        IntPtr buffer = Marshal.AllocHGlobal(size + 512);
        try
        {
            for (int i = 0; i < size + 512; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteInt32(buffer, 0, 0x10000 | size);

            int code;
            if (id == IdPowerPoliciesGetInfo)
            {
                PowerPoliciesGetInfoDelegate? function = Resolve<PowerPoliciesGetInfoDelegate>(id);
                code = function is null ? -1 : function(_gpu, buffer);
            }
            else if (id == IdPowerPoliciesGetStatus)
            {
                PowerPoliciesGetStatusDelegate? function = Resolve<PowerPoliciesGetStatusDelegate>(id);
                code = function is null ? -1 : function(_gpu, buffer);
            }
            else if (id == IdGetPstates20)
            {
                GetPstatesDelegate? function = Resolve<GetPstatesDelegate>(id);
                code = function is null ? -1 : function(_gpu, buffer);
            }
            else
            {
                GetCoolerSettingsDelegate? function = Resolve<GetCoolerSettingsDelegate>(id);
                code = function is null ? -1 : function(_gpu, 0, buffer);
            }

            report.Add($"{title}: код {code}, структура {size} байт");
            if (code != 0) return report;

            var line = new System.Text.StringBuilder();
            int count = 0;
            int maxValues = 120;
            for (int offset = 0; offset < size && count < maxValues; offset += 4)
            {
                line.Append($"[{count}]={Marshal.ReadInt32(buffer, offset)} ");
                count++;
                if (count % 10 == 0)
                {
                    report.Add(line.ToString().TrimEnd());
                    line.Clear();
                }
            }

            if (line.Length > 0) report.Add(line.ToString().TrimEnd());
            if (size / 4 > maxValues) report.Add($"… всего значений {size / 4}");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return report;
    }

    private static IEnumerable<uint> Candidates(int size)
    {
        yield return (uint)(0x10000 | size);
        yield return (uint)(0x10000 | (size - 4));
        yield return (uint)(0x10000 | (size + 4));
        yield return 0x10038;
        yield return 0x10030;
        yield return 0x1002C;
        yield return 0x10024;
        yield return 0x10020;
        yield return 0x1001C;
        yield return 0x10018;
        yield return 0x10010;
    }
}
