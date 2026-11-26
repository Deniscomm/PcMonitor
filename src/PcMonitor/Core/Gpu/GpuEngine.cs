using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace PcMonitor.Core.Gpu;

/// <summary>A completed GPU batch, not CPU command-submission throughput. Pixels are a bounded BGRA thumbnail.</summary>
public readonly record struct GpuCamera(float X, float Y, float Z, float Yaw, float Pitch, bool Enabled);

public sealed record GpuRenderSample(
    long Frames, long DrawCalls, TimeSpan Elapsed, double FramesPerSecond,
    double CompletedFrameTimeMs, double? GpuFrameTimeMs, int Width, int Height,
    int DrawsPerFrame, byte[]? PreviewPixels, int PreviewWidth, int PreviewHeight);


/// <summary>
/// GPU-тесты на Direct3D 11 с рендером в offscreen-текстуру: swap chain не создаётся,
/// поэтому результат не зависит от частоты обновления монитора (никакой вертикальной синхронизации).
/// Сцены — реймарчинг фрактала, частицы, ландшафт, множество Жюлиа и многопроходная постобработка.
/// </summary>
public sealed class GpuEngine : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct GpuConstants
    {
        public float Time;
        public float Quality;
        public float Width;
        public float Height;
        public float Frame;
        public float Pad1;
        public float Pad2;
        public float Pad3;
        public float CameraX;
        public float CameraY;
        public float CameraZ;
        public float CameraYaw;
        public float CameraPitch;
        public float CameraMode;
        public float CameraPad1;
        public float CameraPad2;
    }

    private const int MaxChecksumSamples = 4096;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _blurShader;
    private readonly Dictionary<string, ID3D11PixelShader> _pixelShaders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ID3D11Buffer _constants;
    private readonly ID3D11SamplerState _sampler;

    private ID3D11Texture2D? _targetA;
    private ID3D11Texture2D? _targetB;
    private ID3D11Texture2D? _staging;
    private readonly ID3D11Query _completionQuery;
    private ID3D11Texture2D? _finalTarget;
    private ID3D11ShaderResourceView? _finalSrv;
    private ID3D11Texture2D? _previewTarget;
    private ID3D11Texture2D? _previewStaging;
    private ID3D11RenderTargetView? _previewRtv;
    private ID3D11PixelShader? _previewShader;
    public const int ThumbnailWidth = 320;
    public const int ThumbnailHeight = 180;
    private ID3D11RenderTargetView? _rtvA;
    private ID3D11RenderTargetView? _rtvB;
    private ID3D11ShaderResourceView? _srvA;
    private ID3D11ShaderResourceView? _srvB;

    private int _width;
    private int _height;
    private bool _disposed;

    /// <summary>Описание доступных сцен: каждая проверяет своё.</summary>
    public static IReadOnlyList<GpuSceneInfo> Scenes { get; } = new List<GpuSceneInfo>
    {
        new("freecam", "Свободная камера — ходьба/полёт",
            "Настоящая свободная 3D-камера: WASD, Space/Ctrl и мышь/стрелки. Есть пол, стены, свет и коллизия визуального пространства.", false),
        new("raytrace", "Трассировка лучей",
            "Настоящая трассировка: зеркальная, стеклянная и матовая сферы, шахматный пол, до 9 отражений " +
            "и несколько лучей на пиксель — честная проверка вычислительных блоков видеокарты.", false),
        new("fur", "Мех и волокна",
            "Пушистый тор с процедурными волокнами, объёмной поверхностью и многослойным затенением.", false),
        new("room", "Комната с отражениями",
            "Красный куб, синяя сфера, медный тор и резкие отражения, рассчитанные дополнительными лучами.", false),
        new("hall", "Зал с отражениями",
            "Длинный зал с зеркальным полом, светящимися панелями и повторным проходом луча отражения — " +
            "сцена проверяет трассировку отражений и плотность шейдеров.", true),
        new("planet", "Планета с кольцами",
            "Реймарчинг сферы, процедурные полосы газа, полупрозрачные кольца и звёздное поле. " +
            "Проверяет точность вычислений с плавающей точкой и смешанную нагрузку.", false),
        new("sunset", "Закат над водой",
            "Небо с облаками, солнце и отражение в воде с волнами. Проверяет шейдеры отражений " +
            "и работу с производными функции шума.", false),
        new("city", "Ночной город",
            "Реймарчинг кварталов со светящимися окнами и туманом. Тяжёлые циклы с ветвлениями — " +
            "проверяет, как видеокарта справляется с расходящимися лучами.", false),
        new("tunnel", "Неоновый тоннель",
            "Несущийся навстречу тоннель с кольцами и спицами: заливка, прозрачность и мелкая структура. " +
            "Хорошо показывает стабильность на больших разрешениях.", false),
        new("galaxy", "Галактика",
            "Более тысячи аддитивных частиц в спиральных рукавах: каждый пиксель суммирует вклад всех " +
            "самая тяжёлая сцена по ALU и смешиванию.", false),
        new("fractal", "Фрактал (зум)",
            "Бесконечный зум множества Мандельброта: до 620 итераций на пиксель, " +
            "проверяет устойчивость вычислений на пределе точности.", false),
        new("raymarch", "Фрактал (реймарчинг)",
            "Реймарчинг фрактала Mandelbulb: тяжёлые пиксельные вычисления, как в тяжёлых игровых сценах.", false),
        new("particles", "Частицы",
            "Десятки тысяч аналитических частиц с аддитивным смешиванием: проверка заливки и ALU.", false),
        new("terrain", "Ландшафт",
            "Реймарчинг ландшафта с фрактальным шумом в несколько октав: смешанная нагрузка.", false),
        new("julia", "Множество Жюлиа",
            "До 900 итераций на пиксель — самая тяжёлая вычислительная сцена.", false),
        new("bloom", "Постобработка (многопроходная)",
            "Сцена с многопроходным размытием: проверяет пропускную способность памяти видеокарты.", true),
    };

    /// <summary>Сведения о выбранном адаптере.</summary>
    public GpuAdapterInfo Adapter { get; }

    /// <summary>Диагностика создания устройства (если аппаратное не поднялось).</summary>
    public string DeviceDiagnostics { get; private set; } = "аппаратное устройство создано";

    /// <summary>Используется программный растеризатор вместо видеокарты.</summary>
    public bool UsedSoftwareRenderer { get; private set; }

    public GpuEngine(int adapterIndex = 0)
    {
        Adapter = EnumerateAdapters().FirstOrDefault(a => a.Index == adapterIndex)
                  ?? new GpuAdapterInfo(0, "Основная видеокарта", 0, 0, 0, false);

        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;

        // Honour the requested DXGI adapter. Explicit adapters require DriverType.Unknown.
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        if (factory.EnumAdapters1((uint)Math.Max(0, adapterIndex), out IDXGIAdapter1? selected).Failure || selected is null)
            throw new ArgumentOutOfRangeException(nameof(adapterIndex), "Видеокарта не найдена.");
        using (selected)
        {
            FeatureLevel[] fullLevels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
            SharpGen.Runtime.Result result = D3D11.D3D11CreateDevice(
                selected, DriverType.Unknown, DeviceCreationFlags.BgraSupport, fullLevels,
                out device, out FeatureLevel _, out context);
            if (result.Failure || device is null || context is null)
            {
                context?.Dispose();
                device?.Dispose();
                DeviceDiagnostics = $"Выбранная видеокарта: {result.Description}; ";
                result = D3D11.D3D11CreateDevice(
                    selected, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                    new[] { FeatureLevel.Level_11_0 }, out device, out _, out context);
            }
            if (result.Failure || device is null || context is null)
            {
                context?.Dispose();
                device?.Dispose();
                DeviceDiagnostics += $"аппаратный D3D11: {result.Description}; WARP: ";
                result = D3D11.D3D11CreateDevice(
                    null, DriverType.Warp, DeviceCreationFlags.BgraSupport, fullLevels,
                    out device, out _, out context);
                DeviceDiagnostics += result.Description;
                UsedSoftwareRenderer = true;
            }
            if (result.Failure || device is null || context is null)
            {
                context?.Dispose();
                device?.Dispose();
                throw new InvalidOperationException("Не удалось создать устройство Direct3D 11: " + result.Description);
            }
        }
        _device = device;
        _context = context;

        // Уточняем данные адаптера прямо из устройства: имя и объём видеопамяти
        // определяются даже там, где перечисление через DXGI не сработало.
        try
        {
            using IDXGIDevice dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter dxgiAdapter = dxgiDevice.GetAdapter();

            string name = "Видеоадаптер";
            long memory = 0;

            try
            {
                name = dxgiAdapter.Description.Description.TrimEnd('\0', ' ');
            }
            catch
            {
                // Оставим общее название.
            }
            uint vendorId = 0;
            uint deviceId = 0;
            bool software = false;

            try
            {
                using IDXGIAdapter1 adapter1 = dxgiAdapter.QueryInterface<IDXGIAdapter1>();
                AdapterDescription1 description = adapter1.Description1;
                name = description.Description.TrimEnd('\0', ' ');
                memory = unchecked((long)description.DedicatedVideoMemory);
                vendorId = description.VendorId;
                deviceId = description.DeviceId;
                software = description.Flags.HasFlag(AdapterFlags.Software);
            }
            catch
            {
                // Обойдёмся именем адаптера.
            }

            Adapter = new GpuAdapterInfo(adapterIndex, name, memory, vendorId, deviceId, software);
            UsedSoftwareRenderer |= software;
        }
        catch (Exception ex)
        {
            // Оставляем сведения, полученные перечислением адаптеров.
            DeviceDiagnostics += $"; данные адаптера: {ex.GetType().Name}: {ex.Message}";
        }

        try
        {
            byte[] vertexBytecode = ShaderCompiler.Compile(GpuShaders.Common, "VSMain", "vs_5_0");
            _vertexShader = _device.CreateVertexShader(vertexBytecode);
    
            // Compile only the selected scene on its worker. A bad unrelated shader must not disable all rendering.
            _blurShader = _device.CreatePixelShader(
                ShaderCompiler.Compile(GpuShaders.Common + GpuShaders.Blur, "PSBlur", "ps_5_0"));
            _completionQuery = _device.CreateQuery(new QueryDescription(QueryType.Event));
            _constants = _device.CreateBuffer(new BufferDescription(
                16 * sizeof(float), BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));
    
            _sampler = _device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.Never,
                MinLOD = 0,
                MaxLOD = float.MaxValue,
            });
    
        }
        catch
        {
            _sampler?.Dispose();
            _constants?.Dispose();
            _completionQuery?.Dispose();
            _blurShader?.Dispose();
            _vertexShader?.Dispose();
            _context.Dispose();
            _device.Dispose();
            throw;
        }

    }

    // ============================ Адаптеры ============================

    /// <summary>Список видеодаптеров (дискретные и встроенные).</summary>
    public static IReadOnlyList<GpuAdapterInfo> EnumerateAdapters()
    {
        var adapters = new List<GpuAdapterInfo>();

        try
        {
            using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint index = 0; index < 8; index++)
            {
                if (factory.EnumAdapters1(index, out IDXGIAdapter1? adapter).Failure || adapter is null) break;

                using (adapter)
                {
                    string name = "Видеоадаптер";
                    long memory = 0;
                    uint vendorId = 0;
                    uint deviceId = 0;
                    bool software = false;

                    try
                    {
                        name = adapter.Description.Description.TrimEnd('\0', ' ');
                    }
                    catch
                    {
                        // Оставим общее название.
                    }

                    try
                    {
                        using IDXGIAdapter1 adapter1 = adapter.QueryInterface<IDXGIAdapter1>();
                        AdapterDescription1 description = adapter1.Description1;
                        name = description.Description.TrimEnd('\0', ' ');

                        // Объём видеопамяти в описании адаптера — size_t: читаем аккуратно,
                        // на некоторых драйверах обычное приведение переполняется.
                        memory = unchecked((long)description.DedicatedVideoMemory);
                        vendorId = description.VendorId;
                        deviceId = description.DeviceId;
                        software = description.Flags.HasFlag(AdapterFlags.Software);
                    }
                    catch
                    {
                        // Обойдёмся именем адаптера.
                    }

                    adapters.Add(new GpuAdapterInfo((int)index, name, memory, vendorId, deviceId, software));
                }
            }
        }
        catch
        {
            // DXGI недоступен — вернём хотя бы общее описание.
        }

        if (adapters.Count == 0)
            adapters.Add(new GpuAdapterInfo(0, "Основная видеокарта", 0, 0, 0, false));

        return adapters;
    }

    // ============================ Тесты ============================

    /// <summary>
    /// Рисует один кадр сцены и возвращает пиксели (BGRA) — для живого предпросмотра
    /// в интерфейсе: видно, что именно считает видеокарта.
    /// </summary>
    public byte[] RenderPreview(string sceneName, int width, int height, float time, GpuPreset preset, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        EnsureSize(Math.Clamp(width, 160, 3840), Math.Clamp(height, 90, 2160));

        float quality = QualityFor(preset);
        token.ThrowIfCancellationRequested();
        GpuSceneInfo scene = Scenes.FirstOrDefault(s => string.Equals(s.Name, sceneName, StringComparison.OrdinalIgnoreCase))
                             ?? Scenes[0];
        int passes = scene.IsMultiPass ? (int)Math.Round(4 + quality * 12) : 1;

        RenderFrame(scene.Name, time, quality, passes, (long)(time * 60), token);
        _context.Flush();
        return ReadBack(token);
    }

    /// <summary>Renders directly at preview resolution so the live image stays responsive.</summary>
    public byte[] RenderPreviewThumbnail(string sceneName, GpuPreset preset, float time, CancellationToken token = default, GpuCamera? camera = null)
    {
        token.ThrowIfCancellationRequested();
        EnsureSize(ThumbnailWidth, ThumbnailHeight);
        GpuSceneInfo scene = Scenes.FirstOrDefault(s => s.Name.Equals(sceneName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Неизвестная сцена.", nameof(sceneName));
        const float previewQuality = 0.25f;
        RenderFrame(scene.Name, time, previewQuality, 1, (long)(time * 60), token, camera);
        _context.Flush();
        return ReadBack(token);
    }

    /// <summary>
    /// Runs real repeated GPU Draw commands until cancellation. Only the final draw is read back as a thumbnail.
    /// Call on one dedicated worker; the engine is not thread-safe.
    /// Completion is bounded to two in-flight frames. No full-resolution CPU readback is performed in this mode.
    /// </summary>
    public void RunContinuous(string sceneName, GpuPreset preset, CancellationToken token,
        Action<GpuRenderSample>? report = null, int workMultiplier = 1)
    {
        if (UsedSoftwareRenderer)
            throw new InvalidOperationException("WARP — программный рендер на CPU. GPU-нагрузка доступна только на аппаратном адаптере.");
        GpuSceneInfo scene = Scenes.FirstOrDefault(s => s.Name.Equals(sceneName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Неизвестная сцена.", nameof(sceneName));
        (int width, int height) = ResolutionFor(preset);
        EnsureSize(width, height);
        float quality = QualityFor(preset);
        int passes = scene.IsMultiPass ? (int)Math.Round(4 + quality * 12) : 1;
        workMultiplier = Math.Clamp(workMultiplier, 1, 32);
        int drawsPerFrame = passes > 1 ? passes + 1 : 1;
        _ = SceneShader(scene.Name); // exclude shader compilation from timing
        using ID3D11Query start = _device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        using ID3D11Query end = _device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        using ID3D11Query disjoint = _device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
        IntPtr data = Marshal.AllocHGlobal(16);
        var clock = Stopwatch.StartNew();
        double lastReport = 0;
        long frames = 0;
        long intervalFrames = 0;
        double intervalGpuMs = 0;
        bool validGpuTiming = true;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                _context.Begin(disjoint);
                _context.End(start);
                int batchSize = Math.Clamp(128 / workMultiplier, 4, 32);
                for (int i = 0; i < batchSize; i++)
                {
                    token.ThrowIfCancellationRequested();
                    for (int work = 0; work < workMultiplier; work++)
                    {
                        token.ThrowIfCancellationRequested();
                        RenderFrame(scene.Name, (float)clock.Elapsed.TotalSeconds, quality, passes,
                            (frames + i) * workMultiplier + work, token);
                    }
                }
                _context.End(end);
                _context.End(disjoint);
                SyncGpu(token);
                WaitForQuery(disjoint, token);
                _context.GetData(disjoint, data, 16, AsyncGetDataFlags.DoNotFlush).CheckError();
                ulong frequency = unchecked((ulong)Marshal.ReadInt64(data));
                bool unstable = Marshal.ReadInt32(data, 8) != 0;
                WaitForQuery(start, token);
                _context.GetData(start, data, 8, AsyncGetDataFlags.DoNotFlush).CheckError();
                ulong beginTicks = unchecked((ulong)Marshal.ReadInt64(data));
                WaitForQuery(end, token);
                _context.GetData(end, data, 8, AsyncGetDataFlags.DoNotFlush).CheckError();
                ulong endTicks = unchecked((ulong)Marshal.ReadInt64(data));
                frames += batchSize;
                intervalFrames += batchSize;
                if (!unstable && frequency > 0 && endTicks >= beginTicks)
                    intervalGpuMs += (endTicks - beginTicks) * 1000.0 / frequency;
                else
                    validGpuTiming = false;
                double seconds = clock.Elapsed.TotalSeconds;
                if (report is not null && seconds - lastReport >= 0.1)
                {
                    double intervalSeconds = seconds - lastReport;
                    report(new GpuRenderSample(frames, frames * drawsPerFrame * workMultiplier, clock.Elapsed,
                        intervalFrames / Math.Max(0.001, intervalSeconds),
                        intervalSeconds * 1000 / intervalFrames,
                        validGpuTiming ? intervalGpuMs / intervalFrames : null,
                        width, height, drawsPerFrame, null, ThumbnailWidth, ThumbnailHeight));
                    lastReport = seconds;
                    intervalFrames = 0;
                    intervalGpuMs = 0;
                    validGpuTiming = true;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(data);
        }
    }

    private byte[] ReadThumbnail(CancellationToken token)
    {
        if (_previewTarget is null)
        {
            var description = new Texture2DDescription
            {
                Width = ThumbnailWidth, Height = ThumbnailHeight, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.RenderTarget,
            };
            _previewTarget = _device.CreateTexture2D(description);
            _previewRtv = _device.CreateRenderTargetView(_previewTarget);
            description.Usage = ResourceUsage.Staging;
            description.BindFlags = BindFlags.None;
            description.CPUAccessFlags = CpuAccessFlags.Read;
            _previewStaging = _device.CreateTexture2D(description);
            _previewShader = _device.CreatePixelShader(ShaderCompiler.Compile(GpuShaders.Common + """
                Texture2D SourceTexture : register(t0);
                SamplerState SourceSampler : register(s0);
                float4 PSThumbnail(VSOut input) : SV_Target
                {
                    return float4(SourceTexture.Sample(SourceSampler, input.Uv).rgb, 1.0);
                }
                """, "PSThumbnail", "ps_5_0"));
        }
        _context.PSSetShaderResource(0, null!);
        _context.OMSetRenderTargets(_previewRtv!);
        _context.RSSetViewport(new Viewport(0f, 0f, ThumbnailWidth, ThumbnailHeight));
        _context.PSSetShader(_previewShader!);
        _context.PSSetShaderResource(0, _finalSrv!);
        _context.PSSetSampler(0, _sampler);
        _context.Draw(3, 0);
        _context.PSSetShaderResource(0, null!);
        _context.CopyResource(_previewStaging!, _previewTarget!);
        SyncGpu(token);
        _context.Map(_previewStaging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mapped).CheckError();
        try
        {
            var pixels = new byte[ThumbnailWidth * ThumbnailHeight * 4];
            for (int y = 0; y < ThumbnailHeight; y++)
                Marshal.Copy(IntPtr.Add(mapped.DataPointer, y * (int)mapped.RowPitch), pixels,
                    y * ThumbnailWidth * 4, ThumbnailWidth * 4);
            return pixels;
        }
        finally
        {
            _context.Unmap(_previewStaging!, 0);
        }
    }

    /// <summary>Разрешение текущей цели рендера (для предпросмотра).</summary>
    public (int Width, int Height) CurrentSize => (_width, _height);

    /// <summary>Прогон одной сцены: измеряет частоту кадров без вертикальной синхронизации.</summary>
    public GpuTestResult RunRasterTest(
        string sceneName,
        GpuPreset preset,
        TimeSpan duration,
        IProgress<GpuTestProgress>? progress,
        CancellationToken token,
        int syncEveryNFrames = 4)
    {
        token.ThrowIfCancellationRequested();
        GpuSceneInfo scene = Scenes.FirstOrDefault(s => string.Equals(s.Name, sceneName, StringComparison.OrdinalIgnoreCase))
                             ?? Scenes[0];

        (int width, int height) = ResolutionFor(preset);
        EnsureSize(width, height);

        float quality = QualityFor(preset);
        int passes = scene.IsMultiPass ? (int)Math.Round(4 + quality * 12) : 1;

        var stopwatch = Stopwatch.StartNew();
        var frameTimes = new List<double>(4096);
        long frames = 0;

        // Разогрев: компиляция шейдеров, прогрев кэшей и заполнение очереди команд.
        var warmup = Stopwatch.StartNew();
        int warmupFrames = 0;
        while (warmup.Elapsed < TimeSpan.FromSeconds(1.5) && !token.IsCancellationRequested)
        {
            RenderFrame(scene.Name, 0.1f * warmupFrames, quality, passes, warmupFrames, token);
            SyncGpu(token);
            warmupFrames++;
        }

        stopwatch.Restart();

        // Wait for completed batches; no pixel readback is needed for throughput measurement.
        // Utilization depends on the adapter and scene and is not inferred from this loop.
        int syncInterval = Math.Clamp(syncEveryNFrames, 1, 8);

        while (stopwatch.Elapsed < duration && !token.IsCancellationRequested)
        {
            var frameWatch = Stopwatch.StartNew();
            int batchFrames = 0;

            for (int step = 0; step < syncInterval && !token.IsCancellationRequested; step++)
            {
                RenderFrame(scene.Name, (float)stopwatch.Elapsed.TotalSeconds, quality, passes, frames, token);
                frames++;
                batchFrames++;
            }

            SyncGpu(token);
            frameWatch.Stop();

            frameTimes.Add(frameWatch.Elapsed.TotalMilliseconds / Math.Max(1, batchFrames));

            if (frames % (syncInterval * 4) == 0)
            {
                double elapsed = stopwatch.Elapsed.TotalSeconds;
                double fps = elapsed > 0 ? frames / elapsed : 0;
                progress?.Report(new GpuTestProgress(
                    scene.Title, 0, 1, Math.Clamp(elapsed / duration.TotalSeconds, 0, 1),
                    fps, "FPS", 0, $"кадров {frames}, {fps:0.0} FPS, {width}×{height}"));
            }
        }

        token.ThrowIfCancellationRequested();
        stopwatch.Stop();
        double seconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        double averageFps = frames / seconds;

        (double minimumFps, double maximumFps) = WindowedFps(frameTimes);

        int score = (int)Math.Round(averageFps * 100);
        string verdict = averageFps > 0
            ? $"сцена пройдена: {averageFps:0.0} FPS в среднем, минимум {minimumFps:0.0}"
            : "сцена не выдала кадров";

        return new GpuTestResult(
            $"{scene.Title} · {preset}",
            scene.Name,
            preset,
            score,
            averageFps,
            minimumFps,
            maximumFps,
            frames > 0 ? seconds * 1000 / frames : 0,
            frames,
            width,
            height,
            stopwatch.Elapsed,
            0,
            verdict,
            $"проходов на кадр: {passes}, качество {quality:0.00}, без вертикальной синхронизации");
    }

    /// <summary>Собственный GPU-бенчмарк: все сцены на выбранном пресете и сводный балл.</summary>
    public GpuTestResult RunBenchmark(
        GpuPreset preset,
        TimeSpan perScene,
        IProgress<GpuTestProgress>? progress,
        CancellationToken token)
    {
        var results = new List<GpuTestResult>();
        var stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < Scenes.Count && !token.IsCancellationRequested; i++)
        {
            GpuSceneInfo scene = Scenes[i];
            var stageProgress = new Progress<GpuTestProgress>(p => progress?.Report(p with
            {
                Stage = scene.Title,
                StageIndex = i + 1,
                StageCount = Scenes.Count,
                Percent = (i + p.Percent) / Scenes.Count * 100,
            }));

            GpuTestResult result = RunRasterTest(scene.Name, preset, perScene, stageProgress, token);
            results.Add(result);
            progress?.Report(new GpuTestProgress(scene.Title, i + 1, Scenes.Count,
                (i + 1) * 100.0 / Scenes.Count, result.AverageFps, "FPS", 0, result.Verdict));
        }

        stopwatch.Stop();

        if (results.Count == 0)
            return new GpuTestResult("GPU-бенчмарк", "—", preset, 0, 0, 0, 0, 0, 0, 0, 0, stopwatch.Elapsed, 0,
                "прогон отменён", string.Empty);

        // Сводный балл: среднее значение результатов сцен, нормированное приложением.
        double averageFps = results.Average(r => r.AverageFps);
        double minimumFps = results.Min(r => r.MinimumFps);
        double maximumFps = results.Max(r => r.MaximumFps);
        int score = (int)Math.Round(results.Average(r => r.Score));
        long frames = results.Sum(r => r.Frames);

        var details = string.Join("; ", results.Select(r => $"{r.SceneName}: {r.AverageFps:0.0} FPS ({r.Score})"));

        return new GpuTestResult(
            $"GPU-бенчмарк · {preset}",
            "все сцены",
            preset,
            score,
            averageFps,
            minimumFps,
            maximumFps,
            results.Average(r => r.GpuTimeMs),
            frames,
            results[0].Width,
            results[0].Height,
            stopwatch.Elapsed,
            0,
            $"итоговый балл {score} по {results.Count} сценам",
            details);
    }

    /// <summary>Проверка стабильности: эталонный кадр сравнивается с последующими (поиск артефактов).</summary>
    public long RunArtifactCheck(
        string sceneName,
        GpuPreset preset,
        int checks,
        TimeSpan duration,
        IProgress<GpuTestProgress>? progress,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        GpuSceneInfo scene = Scenes.FirstOrDefault(s => string.Equals(s.Name, sceneName, StringComparison.OrdinalIgnoreCase))
                             ?? Scenes[0];

        (int width, int height) = ResolutionFor(preset);
        EnsureSize(width, height);
        float quality = QualityFor(preset);
        int passes = scene.IsMultiPass ? (int)Math.Round(4 + quality * 12) : 1;

        // Эталон рисуется при фиксированном времени — так кадр детерминирован
        // и любое расхождение означает реальный сбой видеокарты.
        byte[] reference = RenderAndRead(scene.Name, 0f, quality, passes, token);
        long referenceHash = Hash(reference);

        long errors = 0;
        var stopwatch = Stopwatch.StartNew();

        for (int i = 0; i < checks && stopwatch.Elapsed < duration && !token.IsCancellationRequested; i++)
        {
            RenderFrame(scene.Name, 0f, quality, passes, 1, token);
            byte[] current = ReadBack(token);
            long hash = Hash(current);
            if (hash != referenceHash) errors++;

            progress?.Report(new GpuTestProgress(
                "Проверка стабильности", i + 1, checks, (i + 1) * 100.0 / checks, errors, "ошибок", errors,
                $"проверок {i + 1}, расхождений {errors}"));
        }

        return errors;
    }

    // ============================ Внутреннее ============================

    public static (int Width, int Height) ResolutionFor(GpuPreset preset) => preset switch
    {
        GpuPreset.Low => (1280, 720),
        GpuPreset.Medium => (1920, 1080),
        GpuPreset.High => (2560, 1440),
        _ => (3840, 2160),
    };

    private static float QualityFor(GpuPreset preset) => preset switch
    {
        GpuPreset.Low => 0.25f,
        GpuPreset.Medium => 0.5f,
        GpuPreset.High => 0.75f,
        _ => 1.0f,
    };

    private void EnsureSize(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_targetA is not null && _targetB is not null && _width == width && _height == height)
            return;

        _context.ClearState();
        DisposeTargets();

        _width = width;
        _height = height;

        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        };

        _targetA = _device.CreateTexture2D(description);
        _targetB = _device.CreateTexture2D(description);
        _rtvA = _device.CreateRenderTargetView(_targetA);
        _rtvB = _device.CreateRenderTargetView(_targetB);
        _srvA = _device.CreateShaderResourceView(_targetA);
        _srvB = _device.CreateShaderResourceView(_targetB);


    }

    /// <summary>Wait for actual GPU completion without mapping a texture or reading pixels.</summary>
    private void SyncGpu(CancellationToken token = default)
    {
        _context.End(_completionQuery);
        _context.Flush();
        WaitForQuery(_completionQuery, token);
    }

    private void WaitForQuery(ID3D11Query query, CancellationToken token)
    {
        var timeout = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var result = _context.GetData(query, IntPtr.Zero, 0, AsyncGetDataFlags.DoNotFlush);
            if (result.Code == 0) return; // S_OK; S_FALSE is pending, not success.
            result.CheckError();
            if (timeout.Elapsed > TimeSpan.FromSeconds(15))
                throw new TimeoutException("GPU не завершил команды за 15 секунд (возможен сбой драйвера).");
            // Brief polling keeps the UI free; the already-submitted batch keeps the GPU busy.
            Thread.Sleep(1);
        }
    }

    private ID3D11PixelShader SceneShader(string sceneName)
    {
        if (_pixelShaders.TryGetValue(sceneName, out ID3D11PixelShader? shader)) return shader;
        (string source, string entry) = ShaderSourceFor(sceneName);
        shader = _device.CreatePixelShader(ShaderCompiler.Compile(GpuShaders.Common + source, entry, "ps_5_0"));
        _pixelShaders[sceneName] = shader;
        return shader;
    }

    // Kept in this engine to preserve ownership of the shared shader catalog.
    internal static (string Source, string Entry) ShaderSourceFor(string sceneName) => sceneName.ToLowerInvariant() switch
        {
            "freecam" => (GpuShaders.FreeCam, "PSFreeCam"),
            "raytrace" => (GpuShaders.RayTrace, "PSRayTrace"),
            "fur" => (GpuShaders.Fur, "PSFur"),
            "room" => (GpuShaders.Room.Replace("float3(1.0)", "float3(1.0, 1.0, 1.0)", StringComparison.Ordinal), "PSRoom"),
            "hall" => (GpuShaders.Hall, "PSHall"),
            "planet" => (GpuShaders.Planet, "PSPlanet"),
            "sunset" => (GpuShaders.Sunset, "PSSunset"),
            "city" => (GpuShaders.City, "PSCity"),
            "tunnel" => (GpuShaders.Tunnel, "PSTunnel"),
            "galaxy" => (GpuShaders.Galaxy, "PSGalaxy"),
            "fractal" => (GpuShaders.Fractal, "PSFractal"),
            "particles" => (GpuShaders.Particles, "PSParticles"),
            "terrain" => (GpuShaders.Terrain, "PSTerrain"),
            "julia" => (GpuShaders.Julia, "PSJulia"),
            _ => (GpuShaders.Raymarch, "PSRaymarch"),
        };

    private void RenderFrame(string sceneName, float time, float quality, int passes, long frame, CancellationToken token = default, GpuCamera? camera = null)
    {
        token.ThrowIfCancellationRequested();
        if (_rtvA is null || _rtvB is null || _srvA is null || _srvB is null) return;

        ID3D11PixelShader shader = SceneShader(sceneName);
        token.ThrowIfCancellationRequested();
        // Unbind the prior pass before rebinding its input texture as an output.
        _context.PSSetShaderResource(0, null!);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.RSSetViewport(new Viewport(0f, 0f, _width, _height));
        _context.VSSetShader(_vertexShader);
        _context.PSSetConstantBuffer(0, _constants);

        ID3D11RenderTargetView target = _rtvA;
        _context.OMSetRenderTargets(target);
        _context.PSSetShader(shader);
        WriteConstants(time, quality, frame, camera);
        _context.Draw(3, 0);

        _finalTarget = _targetA;
        _finalSrv = _srvA;
        if (passes <= 1) return;

        // Многопроходный режим: размытие с переключением целей (нагрузка на память видеокарты).
        for (int pass = 0; pass < passes; pass++)
        {
            token.ThrowIfCancellationRequested();
            bool even = pass % 2 == 0;
            _context.PSSetShaderResource(0, null!);
            _context.OMSetRenderTargets(even ? _rtvB! : _rtvA!);
            _context.PSSetShader(_blurShader);
            _context.PSSetShaderResource(0, even ? _srvA! : _srvB!);
            _context.PSSetSampler(0, _sampler);
            WriteConstants(time, quality, frame + pass, camera);
            _context.Draw(3, 0);
            _finalTarget = even ? _targetB : _targetA;
            _finalSrv = even ? _srvB : _srvA;
        }

        _context.PSSetShaderResource(0, null!);
    }

    private void WriteConstants(float time, float quality, long frame, GpuCamera? camera = null)
    {
        var constants = new GpuConstants
        {
            Time = time,
            Quality = quality,
            Width = _width,
            Height = _height,
            Frame = frame,
            CameraX = camera?.X ?? 0f,
            CameraY = camera?.Y ?? 1.5f,
            CameraZ = camera?.Z ?? 0f,
            CameraYaw = camera?.Yaw ?? 0f,
            CameraPitch = camera?.Pitch ?? 0f,
            CameraMode = camera?.Enabled == true ? 1f : 0f,
        };

        MappedSubresource mapped = _context.Map(_constants, MapMode.WriteDiscard);
        Marshal.StructureToPtr(constants, mapped.DataPointer, false);
        _context.Unmap(_constants);
    }

    private byte[] RenderAndRead(string sceneName, float time, float quality, int passes, CancellationToken token = default)
    {
        RenderFrame(sceneName, time, quality, passes, 1, token);
        _context.Flush();
        return ReadBack(token);
    }

    /// <summary>Считывает кадр из цели рендера в системную память (для поиска артефактов).</summary>
    private byte[] ReadBack(CancellationToken token = default)
    {
        if (_finalTarget is null) return Array.Empty<byte>();
        if (_staging is null)
        {
            var description = _finalTarget.Description;
            description.Usage = ResourceUsage.Staging;
            description.BindFlags = BindFlags.None;
            description.CPUAccessFlags = CpuAccessFlags.Read;
            _staging = _device.CreateTexture2D(description);
        }

        _context.CopyResource(_staging, _finalTarget);
        SyncGpu(token);
        _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out MappedSubresource mapped).CheckError();

        try
        {
            int rowBytes = _width * 4;
            var pixels = new byte[rowBytes * _height];
            for (int y = 0; y < _height; y++)
            {
                IntPtr source = IntPtr.Add(mapped.DataPointer, y * (int)mapped.RowPitch);
                Marshal.Copy(source, pixels, y * rowBytes, rowBytes);
            }

            return pixels;
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }
    }

    private static long Hash(byte[] pixels)
    {
        if (pixels.Length == 0) return 0;

        long hash = 1469598103934665603;
        int step = Math.Max(4, pixels.Length / MaxChecksumSamples / 4 * 4);
        for (int i = 0; i + 3 < pixels.Length; i += step)
        {
            uint value = (uint)(pixels[i] | (pixels[i + 1] << 8) | (pixels[i + 2] << 16) | (pixels[i + 3] << 24));
            hash = (hash ^ value) * 1099511628211;
        }

        return hash;
    }

    private static (double Minimum, double Maximum) WindowedFps(List<double> frameTimes)
    {
        if (frameTimes.Count == 0) return (0, 0);

        const int window = 16;
        double minimum = double.MaxValue;
        double maximum = 0;

        for (int start = 0; start + window <= frameTimes.Count; start += window)
        {
            double sum = 0;
            for (int i = start; i < start + window; i++) sum += frameTimes[i];

            double milliseconds = sum / window;
            if (milliseconds <= 0) continue;

            double fps = 1000.0 / milliseconds;
            minimum = Math.Min(minimum, fps);
            maximum = Math.Max(maximum, fps);
        }

        if (minimum == double.MaxValue)
        {
            double average = frameTimes.Average();
            double fps = average > 0 ? 1000.0 / average : 0;
            return (fps, fps);
        }

        return (minimum, maximum);
    }

    private void DisposeTargets()
    {
        _srvA?.Dispose();
        _srvB?.Dispose();
        _rtvA?.Dispose();
        _rtvB?.Dispose();
        _staging?.Dispose();

        _targetA?.Dispose();
        _targetB?.Dispose();

        _srvA = _srvB = null;
        _rtvA = _rtvB = null;
        _staging = null;
        _finalTarget = null;
        _finalSrv = null;
        _targetA = _targetB = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _context.ClearState();
        DisposeTargets();
        _previewRtv?.Dispose();
        _previewStaging?.Dispose();
        _previewTarget?.Dispose();
        _previewShader?.Dispose();
        _completionQuery.Dispose();
        _sampler.Dispose();
        _constants.Dispose();
        foreach (ID3D11PixelShader shader in _pixelShaders.Values.Distinct()) shader.Dispose();
        _blurShader.Dispose();
        _vertexShader.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
