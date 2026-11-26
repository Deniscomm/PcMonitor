using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;
using PcMonitor.Core;
using PcMonitor.Core.Gpu;
using PcMonitor.Core.Testing;

namespace PcMonitor.Views;

public sealed class SceneRow
{
    public SceneRow(GpuSceneInfo scene) => Info = scene;
    public GpuSceneInfo Info { get; }
    public string Title => LocalizationManager.T(Info.Title);
    public string Description => LocalizationManager.T(Info.Description);
    public string Speed { get; set; } = string.Empty;
}

public sealed record RenderResultRow(string Title, string Detail);

/// <summary>Rendering is owned by one background operation; the dispatcher only displays bounded snapshots.</summary>
public partial class RendersView : UserControl
{
    private const int CpuPreviewWidth = 288;
    private const int CpuPreviewHeight = 162;
    private const double GpuThermalLimit = 85;
    private const double CpuThermalLimit = 95;
    private readonly ObservableCollection<SceneRow> _scenes = new();
    private readonly ObservableCollection<RenderResultRow> _results = new();
    private readonly ConcurrentQueue<Action> _resultsPending = new();
    private readonly DispatcherTimer _uiTimer = new(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(50) };
    private Action? _pendingUi;
    private CancellationTokenSource? _operationCts;
    private Task? _operation;
    private MonitorContext? _monitor;
    private Window? _owner;
    private WriteableBitmap? _liveBitmap;
    private WriteableBitmap? _cpuBitmap;
    private SceneRow? _current;
    private int _generation;
    private bool _isActive;
    private bool _subscribed;
    private bool _gpuSensorsSeen;
    private bool _cpuSensorsSeen;
    private string _stopReason = "Остановлено пользователем";
    private int _autoFlight = 1;
    private int _moveForward;
    private int _moveStrafe;
    private int _moveVertical;
    private int _turnHorizontal;
    private int _turnVertical;
    private float _manualSceneTime;
    private float _cameraX;
    private float _cameraY = 1.6f;
    private float _cameraZ = -4f;
    private float _cameraYaw;
    private float _cameraPitch;

    public RendersView()
    {
        InitializeComponent();
        SceneList.ItemsSource = _scenes;
        ResultList.ItemsSource = _results;
        foreach (GpuSceneInfo scene in GpuEngine.Scenes) _scenes.Add(new SceneRow(scene));
        SceneList.SelectedIndex = 0;
        _uiTimer.Tick += (_, _) => UpdateUi();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => { if (!IsVisible) Stop(); };
    }

    public event Action<string>? StatusChanged;

    public void ApplyLanguage(string language)
    {
        SceneList.Items.Refresh();
        ResultList.Items.Refresh();
        LocalizationManager.Apply(this, language);
    }


    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            if (!value) Stop();
            // Returning to the tab never resumes computation without a new user click.
        }
    }

    public void Attach(MonitorContext context)
    {
        UnsubscribeMonitor();
        _monitor = context;
        if (IsLoaded) SubscribeMonitor();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SubscribeMonitor();
        _owner = Window.GetWindow(this);
        if (_owner is not null)
        {
            _owner.Closing += OnOwnerClosing;
            _owner.StateChanged += OnOwnerStateChanged;
            _owner.IsVisibleChanged += OnOwnerVisibilityChanged;
        }
        CheckThermals();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Stop();
        UnsubscribeMonitor();
        if (_owner is not null)
        {
            _owner.Closing -= OnOwnerClosing;
            _owner.StateChanged -= OnOwnerStateChanged;
            _owner.IsVisibleChanged -= OnOwnerVisibilityChanged;
            _owner = null;
        }
    }

    private void SubscribeMonitor()
    {
        if (_monitor is null || _subscribed) return;
        _monitor.Updated += CheckThermals;
        _subscribed = true;
    }

    private void UnsubscribeMonitor()
    {
        if (_monitor is not null && _subscribed) _monitor.Updated -= CheckThermals;
        _subscribed = false;
    }

    private void OnOwnerClosing(object? sender, CancelEventArgs e) => Stop();
    private void OnOwnerStateChanged(object? sender, EventArgs e)
    {
        if (_owner?.WindowState == WindowState.Minimized) Stop();
    }
    private void OnOwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_owner?.IsVisible != true) Stop();
    }

    /// <summary>Request cancellation immediately. The worker disposes its own D3D resources after exiting.</summary>
    public void Stop()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(Stop));
            return;
        }
        CancelOperation("Остановлено / пауза");
    }

    private void CancelOperation(string reason)
    {
        if (_operationCts is null || _operationCts.IsCancellationRequested) return;
        _stopReason = reason;
        _operationCts.Cancel();
        _uiTimer.Stop();
        Interlocked.Exchange(ref _pendingUi, null);
        LiveStats.Text = reason + " · завершение команд и освобождение ресурсов…";
        CpuScoreText.Text = reason + " · вычисления останавливаются…";
        StopButton.IsEnabled = false;
        StatusChanged?.Invoke(reason);
    }

    private GpuPreset SelectedPreset => PresetBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
        Enum.TryParse(tag, out GpuPreset preset) ? preset : GpuPreset.Medium;

    private int SelectedGpuWorkMultiplier => WorkMultiplierBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
        int.TryParse(tag, out int multiplier) ? Math.Clamp(multiplier, 1, 32) : 32;

    private void Scene_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _manualSceneTime = 0;
        _cameraX = 0; _cameraY = 1.6f; _cameraZ = -4f; _cameraYaw = 0; _cameraPitch = 0;
        _current = SceneList.SelectedItem as SceneRow;
        if (_operationCts is null)
        {
            LiveImage.Source = null;
            _liveBitmap = null;
            LiveTitle.Text = _current?.Title ?? "Сцена не выбрана";
            LiveStats.Text = "Выберите предпросмотр или непрерывную нагрузку";
        }
    }

    private bool PrepareStart(bool highLoad, bool gpuLoad)
    {
        if (_operationCts is not null || !IsVisible) return false;
        _gpuSensorsSeen = false;
        _cpuSensorsSeen = false;
        (double? gpu, double? cpu, bool fresh) = Temperatures();
        if (gpu >= GpuThermalLimit || cpu >= CpuThermalLimit)
        {
            MessageBox.Show(Window.GetWindow(this), "Температура уже достигла порога автостопа. Дайте системе остыть.",
                "Рендеры", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        bool requiredAvailable = fresh && (gpuLoad ? gpu.HasValue : cpu.HasValue);
        if (highLoad && !requiredAvailable && MessageBox.Show(Window.GetWindow(this),
                "Свежие температурные датчики для выбранной нагрузки недоступны. Автостоп по температуре не гарантирован.\n\n" +
                "Продолжить без этой защиты? Следите за температурой самостоятельно и используйте «Стоп / пауза».",
                "Нагрузка без температурной защиты", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return false;
        return true;
    }

    private void SetBusy(bool busy)
    {
        ShowButton.IsEnabled = !busy;
        GpuLoadButton.IsEnabled = !busy;
        RunAllButton.IsEnabled = !busy;
        CpuPreviewButton.IsEnabled = !busy;
        CpuLoadButton.IsEnabled = !busy;
        PresetBox.IsEnabled = !busy;
        WorkMultiplierBox.IsEnabled = !busy;
        SceneList.IsEnabled = !busy;
        StopButton.IsEnabled = busy;
    }

    private void WorkMultiplier_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GpuLoadButton is not null)
            GpuLoadButton.Content = $"Начать нагрузку GPU · {SelectedGpuWorkMultiplier}×";
    }

    private void NavigationButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            Volatile.Write(ref _autoFlight, 0);
            float sin = MathF.Sin(_cameraYaw);
            float cos = MathF.Cos(_cameraYaw);
            switch (tag)
            {
                case "forward": _cameraX += sin * 0.45f; _cameraZ += cos * 0.45f; break;
                case "back": _cameraX -= sin * 0.45f; _cameraZ -= cos * 0.45f; break;
                case "left": _cameraX -= cos * 0.45f; _cameraZ += sin * 0.45f; break;
                case "right": _cameraX += cos * 0.45f; _cameraZ -= sin * 0.45f; break;
                case "up": _cameraY = Math.Clamp(_cameraY + 0.3f, 0.35f, 4.7f); break;
                case "down": _cameraY = Math.Clamp(_cameraY - 0.3f, 0.35f, 4.7f); break;
            }
            AutoFlightCheck.IsChecked = false;
            LiveStats.Text = "Ручной полёт включён · клавиши W/A/S/D, Space/Ctrl";
        }
    }

    private void AutoFlight_Changed(object sender, RoutedEventArgs e) =>
        Volatile.Write(ref _autoFlight, AutoFlightCheck.IsChecked == true ? 1 : 0);

    private void Navigation_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        int value = e.Key switch
        {
            System.Windows.Input.Key.W => 1,
            System.Windows.Input.Key.S => -1,
            System.Windows.Input.Key.A => -1,
            System.Windows.Input.Key.D => 1,
            System.Windows.Input.Key.Space => 1,
            System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl => -1,
            System.Windows.Input.Key.Left or System.Windows.Input.Key.Right => e.Key == System.Windows.Input.Key.Left ? -1 : 1,
            System.Windows.Input.Key.Up or System.Windows.Input.Key.Down => e.Key == System.Windows.Input.Key.Up ? 1 : -1,
            _ => 0,
        };
        if (value == 0) return;
        Volatile.Write(ref _autoFlight, 0);
        if (e.Key is System.Windows.Input.Key.W or System.Windows.Input.Key.S) _moveForward = value;
        else if (e.Key is System.Windows.Input.Key.A or System.Windows.Input.Key.D) _moveStrafe = value;
        else if (e.Key is System.Windows.Input.Key.Left or System.Windows.Input.Key.Right) _turnHorizontal = value;
        else if (e.Key is System.Windows.Input.Key.Up or System.Windows.Input.Key.Down) _turnVertical = value;
        else _moveVertical = value;
        AutoFlightCheck.IsChecked = false;
        e.Handled = true;
    }

    private void Navigation_PreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is System.Windows.Input.Key.W or System.Windows.Input.Key.S) _moveForward = 0;
        else if (e.Key is System.Windows.Input.Key.A or System.Windows.Input.Key.D) _moveStrafe = 0;
        else if (e.Key is System.Windows.Input.Key.Left or System.Windows.Input.Key.Right) _turnHorizontal = 0;
        else if (e.Key is System.Windows.Input.Key.Up or System.Windows.Input.Key.Down) _turnVertical = 0;
        else if (e.Key is System.Windows.Input.Key.Space or System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl) _moveVertical = 0;
    }

    private GpuCamera NextCamera(double elapsed)
    {
        if (Volatile.Read(ref _autoFlight) != 0)
        {
            _cameraX = (float)Math.Sin(elapsed * 0.35) * 3.0f;
            _cameraZ = (float)Math.Cos(elapsed * 0.35) * 3.0f;
            _cameraYaw = (float)(elapsed * 0.35 + Math.PI);
            _cameraPitch = 0;
        }
        else
        {
            float forward = Volatile.Read(ref _moveForward) * 0.055f;
            float strafe = Volatile.Read(ref _moveStrafe) * 0.055f;
            float vertical = Volatile.Read(ref _moveVertical) * 0.04f;
            _cameraYaw += Volatile.Read(ref _turnHorizontal) * 0.035f;
            _cameraPitch = Math.Clamp(_cameraPitch + Volatile.Read(ref _turnVertical) * 0.025f, -1.2f, 1.2f);
            float sin = MathF.Sin(_cameraYaw), cos = MathF.Cos(_cameraYaw);
            _cameraX += sin * forward + cos * strafe;
            _cameraZ += cos * forward - sin * strafe;
            _cameraY = Math.Clamp(_cameraY + vertical, 0.35f, 4.7f);
        }
        return new GpuCamera(_cameraX, _cameraY, _cameraZ, _cameraYaw, _cameraPitch, true);
    }

    private float NextSceneTime(double autoSeconds)
    {
        if (Volatile.Read(ref _autoFlight) != 0)
            return (float)(autoSeconds * 0.35);
        float velocity = Volatile.Read(ref _moveForward) * 0.08f +
                         Volatile.Read(ref _moveStrafe) * 0.025f +
                         Volatile.Read(ref _moveVertical) * 0.04f;
        _manualSceneTime += velocity;
        return _manualSceneTime;
    }

    private async void StartOperation(string title, Action<CancellationToken, int> work)
    {
        if (_operationCts is not null) return;
        var cts = new CancellationTokenSource();
        _operationCts = cts;
        int generation = ++_generation;
        _stopReason = "Остановлено пользователем";
        SetBusy(true);
        LiveTitle.Text = title;
        LiveStats.Text = "Подготовка на фоновом потоке…";
        CpuScoreText.Text = title.StartsWith("CPU", StringComparison.Ordinal) ? "Подготовка CPU…" : CpuScoreText.Text;
        _uiTimer.Start();
        CheckThermals();
        StatusChanged?.Invoke(title);
        string? error = null;
        try
        {
            _operation = Task.Factory.StartNew(() => work(cts.Token, generation),
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await _operation;
            if (!cts.IsCancellationRequested) DrainUi();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            error = ex is AggregateException aggregate ? aggregate.Flatten().InnerExceptions[0].Message : ex.Message;
        }
        finally
        {
            _uiTimer.Stop();
            Interlocked.Exchange(ref _pendingUi, null);
            while (_resultsPending.TryDequeue(out _)) { }
            _operation = null;
            _operationCts = null;
            bool cancelled = cts.IsCancellationRequested;
            cts.Dispose();
            SetBusy(false);
            RunProgress.Value = 0;
            string ending = error is not null ? "Ошибка: " + error : cancelled ? _stopReason : "Завершено";
            LiveStats.Text = ending + " · вычисления не выполняются";
            if (title.StartsWith("CPU", StringComparison.Ordinal)) CpuScoreText.Text += " · " + ending;
            StatusChanged?.Invoke(ending);
        }
    }

    // Only one replaceable UI snapshot can be pending, even when rendering is much faster than the dispatcher.
    private void Publish(int generation, CancellationToken token, Action update) =>
        Interlocked.Exchange(ref _pendingUi, () =>
        {
            if (generation == _generation && !token.IsCancellationRequested) update();
        });

    private void UpdateUi()
    {
        CheckThermals();
        if (_operationCts?.IsCancellationRequested != true) DrainUi();
    }

    private void DrainUi()
    {
        Interlocked.Exchange(ref _pendingUi, null)?.Invoke();
        while (_resultsPending.TryDequeue(out Action? update)) update();
    }

    private static void WriteImage(Image image, ref WriteableBitmap? bitmap, byte[] pixels, int width, int height)
    {
        if (bitmap is null || bitmap.PixelWidth != width || bitmap.PixelHeight != height)
        {
            bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            image.Source = bitmap;
        }
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => Stop();

    private void Show_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } scene || !PrepareStart(false, true)) return;
        GpuPreset preset = SelectedPreset;
        bool freeNavigation = AutoFlightCheck.IsChecked != true;
        string renderScene = freeNavigation ? "freecam" : scene.Info.Name;
        StartOperation($"GPU-предпросмотр · {scene.Title} · {GpuEngine.ThumbnailWidth}×{GpuEngine.ThumbnailHeight} · качество {preset}", (token, generation) =>
        {
            token.ThrowIfCancellationRequested();
            using var engine = new GpuEngine(0);
            var clock = Stopwatch.StartNew();
            long frames = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var watch = Stopwatch.StartNew();
                byte[] pixels = engine.RenderPreviewThumbnail(renderScene, preset,
                    NextSceneTime(clock.Elapsed.TotalSeconds), token,
                    freeNavigation ? NextCamera(clock.Elapsed.TotalSeconds) : null);
                watch.Stop();
                long completedFrames = ++frames;
                double ms = watch.Elapsed.TotalMilliseconds;
                double actualFps = frames / Math.Max(0.001, clock.Elapsed.TotalSeconds);
                Publish(generation, token, () =>
                {
                    WriteImage(LiveImage, ref _liveBitmap, pixels, GpuEngine.ThumbnailWidth, GpuEngine.ThumbnailHeight);
                    LiveTitle.Text = $"{(freeNavigation ? "Свободная камера · " : string.Empty)}{scene.Title} · {preset} · {engine.Adapter.Name}";
                    LiveStats.Text = $"Показывается {GpuEngine.ThumbnailWidth}×{GpuEngine.ThumbnailHeight} · " +
                        $"{actualFps:0.0} кадров/с (цель 30) · " +
                        $"{ms:0.00} мс с миниатюрой · кадров {completedFrames}" +
                        (engine.UsedSoftwareRenderer ? " · WARP: программный CPU-рендер" : " · аппаратный D3D11");
                });
                if (token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(Math.Max(0, 33.33 - watch.Elapsed.TotalMilliseconds))))
                    token.ThrowIfCancellationRequested();
            }
        });
    }

    private void GpuLoad_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } scene || !PrepareStart(true, true)) return;
        GpuPreset preset = SelectedPreset;
        int multiplier = SelectedGpuWorkMultiplier;
        StartOperation($"GPU-нагрузка ×{multiplier} · {scene.Title} · {preset}", (token, generation) =>
        {
            token.ThrowIfCancellationRequested();
            using var engine = new GpuEngine(0);
            using var previewCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task previewTask = Task.Factory.StartNew(() =>
            {
                using var previewEngine = new GpuEngine(0);
                if (previewEngine.UsedSoftwareRenderer)
                    throw new InvalidOperationException("Аппаратный D3D11-поток предпросмотра недоступен.");
                var previewClock = Stopwatch.StartNew();
                while (!previewCts.IsCancellationRequested)
                {
                    previewCts.Token.ThrowIfCancellationRequested();
                    byte[] pixels = previewEngine.RenderPreviewThumbnail(scene.Info.Name, GpuPreset.Low,
                        NextSceneTime(previewClock.Elapsed.TotalSeconds), previewCts.Token,
                        scene.Info.Name.Equals("freecam", StringComparison.OrdinalIgnoreCase) ? NextCamera(previewClock.Elapsed.TotalSeconds) : null);
                    Publish(generation, token, () => WriteImage(LiveImage, ref _liveBitmap,
                        pixels, GpuEngine.ThumbnailWidth, GpuEngine.ThumbnailHeight));
                    if (previewCts.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(45)))
                        previewCts.Token.ThrowIfCancellationRequested();
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try
            {
                engine.RunContinuous(scene.Info.Name, preset, token, sample => Publish(generation, token, () =>
                {
                    LiveTitle.Text = $"Нагрузка GPU · {scene.Title} · {preset} · {engine.Adapter.Name}";
                    string gpuTime = sample.GpuFrameTimeMs is { } ms ? $"GPU {ms:0.00} мс/кадр" : "GPU-время недоступно (disjoint)";
                    LiveStats.Text = $"Offscreen GPU {sample.Width}×{sample.Height} · {sample.FramesPerSecond:0.0} FPS (не FPS экрана) · {gpuTime}\n" +
                        $"Завершённый пакет: {sample.CompletedFrameTimeMs:0.00} мс/кадр · кадров {sample.Frames:N0} · " +
                        $"GPU Draw {sample.DrawCalls:N0} ({sample.DrawsPerFrame} × {multiplier} рендеров/кадр) · {sample.Elapsed.TotalSeconds:0.0} с\n" +
                        "Живое превью 720p обновляется отдельно, не тормозит очередь GPU · аппаратный D3D11";
                }), multiplier);
            }
            finally
            {
                previewCts.Cancel();
                try { previewTask.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) when (previewCts.IsCancellationRequested) { }
            }
        });
    }

    private void CpuPreview_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareStart(false, false)) return;
        StartOperation("CPU-предпросмотр: один кадр", (token, generation) =>
        {
            var watch = Stopwatch.StartNew();
            byte[] pixels = CpuRenderBenchmark.RenderPreviewFrame(CpuPreviewWidth, CpuPreviewHeight, 0, 1, token);
            watch.Stop();
            Publish(generation, token, () =>
            {
                WriteImage(CpuImage, ref _cpuBitmap, pixels, CpuPreviewWidth, CpuPreviewHeight);
                CpuScoreText.Text = $"Предпросмотр: {CpuPreviewWidth}×{CpuPreviewHeight} · 1 поток · {watch.Elapsed.TotalMilliseconds:0.0} мс. Не полный бенчмарк.";
            });
        });
    }

    private void CpuLoad_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareStart(true, false)) return;
        int threads = Environment.ProcessorCount;
        StartOperation($"CPU-нагрузка: {threads} потоков", (token, generation) =>
            CpuRenderBenchmark.RunContinuous(threads, token, sample => Publish(generation, token, () =>
            {
                WriteImage(CpuImage, ref _cpuBitmap, sample.Pixels, sample.Width, sample.Height);
                CpuScoreText.Text = $"{sample.Width}×{sample.Height} · потоков {sample.Threads} · " +
                    $"{sample.PrimaryRaysPerSecond / 1_000_000:0.00} млн первичных лучей/с\n" +
                    $"Первичных лучей {sample.PrimaryRays:N0} · завершённых строк {sample.CompletedRows:N0} · {sample.Elapsed.TotalSeconds:0.0} с";
            })));
    }

    private void RunAll_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareStart(true, true)) return;
        GpuPreset preset = SelectedPreset;
        SceneRow[] rows = _scenes.ToArray();
        _results.Clear();
        LiveImage.Source = null;
        _liveBitmap = null;
        foreach (SceneRow row in rows) row.Speed = string.Empty;
        SceneList.Items.Refresh();
        StartOperation($"Прогон всех сцен · {preset}", (token, generation) =>
        {
            token.ThrowIfCancellationRequested();
            using var engine = new GpuEngine(0);
            if (engine.UsedSoftwareRenderer)
                throw new InvalidOperationException("WARP — программный CPU-рендер. GPU-бенчмарк не будет выдан за аппаратный результат.");
            for (int index = 0; index < rows.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                int stage = index;
                SceneRow row = rows[index];
                var progress = new InlineProgress<GpuTestProgress>(p => Publish(generation, token, () =>
                {
                    LiveTitle.Text = $"{row.Title} · {preset} · {engine.Adapter.Name}";
                    LiveStats.Text = p.Status;
                    RunProgress.Value = (stage + Math.Clamp(p.Percent, 0, 1)) / rows.Length;
                }));
                GpuTestResult result = engine.RunRasterTest(row.Info.Name, preset, TimeSpan.FromSeconds(6), progress, token, 2);
                _resultsPending.Enqueue(() =>
                {
                    if (generation != _generation || token.IsCancellationRequested) return;
                    row.Speed = $"{result.AverageFps:0.0} FPS · балл {result.Score}";
                    _results.Add(new RenderResultRow($"{row.Title} · {preset} · {result.Width}×{result.Height}",
                        $"{result.AverageFps:0.0} FPS; минимум {result.MinimumFps:0.0}; балл {result.Score}. Артефакты не проверялись."));
                    SceneList.Items.Refresh();
                    RunProgress.Value = (stage + 1.0) / rows.Length;
                });
            }
        });
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    private (double? Gpu, double? Cpu, bool Fresh) Temperatures()
    {
        if (_monitor is null) return (null, null, false);
        double age = (DateTime.Now - _monitor.LastUpdate).TotalSeconds;
        bool fresh = _monitor.LastUpdate != default && age >= -2 && age <= 15;
        if (!fresh) return (null, null, false);
        double? gpu = null;
        double? cpu = null;
        foreach (SensorRow row in _monitor.Rows)
        {
            // Thermal safety ignores user hide/ignore settings and includes the hottest GPU hotspot/VRAM sensor.
            if (row.Type != SensorType.Temperature || row.IsThreshold || row.DisplayValue is not { } value ||
                !float.IsFinite(value) || value <= 0 || value > 200) continue;
            if (row.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                gpu = Math.Max(gpu ?? value, value);
            else if (row.HardwareType == HardwareType.Cpu)
                cpu = Math.Max(cpu ?? value, value);
        }
        return (gpu, cpu, fresh);
    }

    private void CheckThermals()
    {
        (double? gpu, double? cpu, bool fresh) = Temperatures();
        bool zh = LocalizationManager.Language == "zh-CN";
        bool ru = LocalizationManager.Language is "ru" or "ru-RU";
        string missing = zh ? "无新鲜传感器数据" : ru ? "нет свежего датчика" : "no fresh sensor data";
        string gpuText = gpu is { } g ? $"GPU {g:0.0} °C" : $"GPU: {missing}";
        string cpuText = cpu is { } c ? $"CPU {c:0.0} °C" : $"CPU: {missing}";
        string autoStop = zh ? "自动停止" : ru ? "Автостоп" : "Automatic stop";
        string protection = !fresh || (!gpu.HasValue && !cpu.HasValue)
            ? (zh ? "温度保护不可用。" : ru ? "Температурная защита недоступна." : "Temperature protection unavailable.")
            : (zh ? "可用传感器丢失也会停止负载。" : ru ? "Потеря доступных датчиков также останавливает нагрузку." : "Loss of available sensors also stops the load.");
        ThermalText.Text = $"{gpuText} · {cpuText}\n{autoStop}: GPU ≥ {GpuThermalLimit:0} °C / CPU ≥ {CpuThermalLimit:0} °C. {protection}";
        if (_operationCts is null || _operationCts.IsCancellationRequested) return;
        if (gpu >= GpuThermalLimit || cpu >= CpuThermalLimit)
        {
            CancelOperation($"Термостоп: {gpuText} · {cpuText}");
            return;
        }
        if ((_gpuSensorsSeen && !gpu.HasValue) || (_cpuSensorsSeen && !cpu.HasValue))
        {
            CancelOperation("Защитная остановка: температурные датчики пропали или старше 15 секунд");
            return;
        }
        _gpuSensorsSeen |= gpu.HasValue;
        _cpuSensorsSeen |= cpu.HasValue;
    }
}

