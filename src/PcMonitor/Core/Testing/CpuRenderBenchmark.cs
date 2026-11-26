using System.Diagnostics;

namespace PcMonitor.Core.Testing;

/// <summary>Результат рендер-бенчмарка процессора (в духе Cinebench).</summary>
public sealed record CpuRenderResult(
    string Name,
    int Threads,
    double Score,
    double RaysPerSecond,
    TimeSpan Duration,
    long Checksum,
    string Details)
{
    public string ScoreText => Score.ToString("0");
}

/// <summary>
/// Рендер-бенчмарк процессора: детерминированная трассировка лучей небольшой сцены.
/// Балл считается как миллионы лучей в секунду, умноженные на 100 — числа сопоставимы
/// между прогонами на одной машине (как «cb» в Cinebench).
/// </summary>
public sealed record CpuRenderSample(long PrimaryRays, long CompletedRows, double PrimaryRaysPerSecond,
    TimeSpan Elapsed, int Threads, int Width, int Height, byte[] Pixels);

public static class CpuRenderBenchmark
{
    private const int Bounces = 3;
    private const int Width = 640;
    private const int Height = 360;

    /// <summary>Многопоточный прогон.</summary>
    public static CpuRenderResult RunMulti(TimeSpan duration, int? threads, IProgress<double>? progress, CancellationToken token)
    {
        int threadCount = Math.Clamp(threads ?? Environment.ProcessorCount, 1, 256);
        return Run($"Процессор, рендер (все потоки: {threadCount})", threadCount, duration, progress, token);
    }

    /// <summary>Однопоточный прогон.</summary>
    public static CpuRenderResult RunSingle(TimeSpan duration, IProgress<double>? progress, CancellationToken token) =>
        Run("Процессор, рендер (одно ядро)", 1, duration, progress, token);

    private static CpuRenderResult Run(string name, int threads, TimeSpan duration, IProgress<double>? progress, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        var scene = Scene.CreateDefault();

        // Постоянные рабочие потоки берут строки кадра из общего счётчика: полосы
        // разной стоимости (небо дешевле, объекты и пол дороже) распределяются сами,
        // поэтому все ядра загружены практически на 100 % без простоя на барьере.
        int workerCount = Math.Clamp(threads, 1, 256);
        int chunkRows = Math.Max(1, Height / (workerCount * 8));
        long sharedRow = 0;

        long[] rayCounters = new long[workerCount];
        long[] checksums = new long[workerCount];
        long[] rowCounters = new long[workerCount];

        Task[] workers = new Task[workerCount];
        for (int index = 0; index < workerCount; index++)
        {
            int worker = index;
            workers[index] = Task.Factory.StartNew(() =>
            {
                long rays = 0;
                long checksum = 0;
                long rowsDone = 0;
                var frameBuffer = new float[Width * chunkRows * 3];

                while (!token.IsCancellationRequested && stopwatch.Elapsed < duration)
                {
                    long start = Interlocked.Add(ref sharedRow, chunkRows) - chunkRows;
                    int rowStart = (int)(start % Height);
                    int rowEnd = Math.Min(Height, rowStart + chunkRows);

                    int rowsRendered = 0;
                    for (int y = rowStart; y < rowEnd; y++)
                    {
                        if (token.IsCancellationRequested) break;
                        for (int x = 0; x < Width; x++)
                        {
                            double u = (x + 0.5) / Width * 2 - 1;
                            double v = 1 - (y + 0.5) / Height * 2;
                            Ray ray = scene.Camera.CreateRay(u, v);
                            Vector3 color = scene.Trace(ray, Bounces);
                            int offset = ((y - rowStart) * Width + x) * 3;

                            // Страховка от NaN/бесконечности: иначе приведение к int
                            // в контрольной сумме выбросит OverflowException.
                            frameBuffer[offset] = Finite(color.X);
                            frameBuffer[offset + 1] = Finite(color.Y);
                            frameBuffer[offset + 2] = Finite(color.Z);
                        }
                        rowsRendered++;
                    }

                    rays += (long)Width * rowsRendered;
                    checksum = unchecked(checksum + FrameChecksum(frameBuffer, 0, rowsRendered));
                    rowsDone += rowsRendered;
                }

                rayCounters[worker] = rays;
                checksums[worker] = checksum;
                rowCounters[worker] = rowsDone;
            }, TaskCreationOptions.LongRunning);
        }

        using var progressTimer = new Timer(_ =>
                progress?.Report(Math.Clamp(stopwatch.Elapsed.TotalSeconds / Math.Max(0.001, duration.TotalSeconds), 0, 1)),
            null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));

        try
        {
            Task.WaitAll(workers);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            // Only cancellation may produce a partial result; computational failures must be surfaced.
        }

        stopwatch.Stop();

        // Суммы считаем без проверки переполнения: хеши намеренно большие.
        long totalRays = 0;
        long totalChecksum = 0;
        long totalRows = 0;
        for (int i = 0; i < workerCount; i++)
        {
            totalRays = unchecked(totalRays + rayCounters[i]);
            totalChecksum = unchecked(totalChecksum + checksums[i]);
            totalRows += rowCounters[i];
        }

        long frames = totalRows / Height;
        double seconds = Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        double raysPerSecond = totalRays / seconds;
        double score = raysPerSecond / 1_000_000 * 100;

        return new CpuRenderResult(
            name,
            workerCount,
            score,
            raysPerSecond,
            stopwatch.Elapsed,
            totalChecksum,
            $"кадров {frames}, лучей {totalRays / 1_000_000} млн, отражений {Bounces}, " +
            $"разрешение {Width}×{Height}, потоков {workerCount}, контрольная сумма {totalChecksum}");
    }

    private static long FrameChecksum(float[] frame, int y0, int y1)
    {
        long hash = 1469598103934665603;
        int start = y0 * Width * 3;
        int end = Math.Min(frame.Length, y1 * Width * 3);
        for (int i = start; i < end; i += 37)
        {
            float value = frame[i];
            int quantized = float.IsFinite(value) ? (int)Math.Clamp(value * 1000f, -1_000_000f, 1_000_000f) : 0;
            hash = (hash ^ (uint)quantized) * 1099511628211;
        }

        return hash;
    }

    /// <summary>Страховка от NaN/бесконечности: иначе приведение к int
    /// в контрольной сумме выбросит OverflowException.</summary>
    private static float Finite(double value) =>
        double.IsFinite(value) ? Math.Clamp((float)value, -100f, 100f) : 0f;

    /// <summary>
    /// Кадр в формате BGRA для предпросмотра: видно, как процессор считает изображение
    /// (тот же трассировщик, что и в бенчмарке, но в низком разрешении).
    /// </summary>
    public static byte[] RenderPreviewFrame(int width, int height, double time, int threads, CancellationToken token = default)
    {
        width = Math.Clamp(width, 32, 1920);
        height = Math.Clamp(height, 32, 1080);

        var scene = Scene.CreateDefault();
        var pixels = new byte[width * height * 4];
        int rows = height;

        Parallel.For(0, rows, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(threads, 1, 256), CancellationToken = token }, y =>
        {
            for (int x = 0; x < width; x++)
            {
                double u = (x + 0.5) / width * 2 - 1;
                double v = 1 - (y + 0.5) / height * 2;
                if ((x & 15) == 0) token.ThrowIfCancellationRequested();
                Ray ray = scene.Camera.CreateRay(u, v, width / (double)height);
                Vector3 color = scene.Trace(ray, Bounces);

                // Гамма-коррекция как в рендерах Cinebench.
                byte r = ToByte(Math.Pow(Math.Clamp(color.X, 0, 1), 1 / 2.2));
                byte g = ToByte(Math.Pow(Math.Clamp(color.Y, 0, 1), 1 / 2.2));
                byte b = ToByte(Math.Pow(Math.Clamp(color.Z, 0, 1), 1 / 2.2));

                int offset = (y * width + x) * 4;
                pixels[offset] = b;
                pixels[offset + 1] = g;
                pixels[offset + 2] = r;
                pixels[offset + 3] = 255;
            }
        });

        return pixels;
    }

    /// <summary>
    /// Full-thread sustained ray tracing, unbounded until cancellation. Dedicated workers never wait for UI frames.
    /// A progressively refreshed image is sampled from completed rows; counters describe actual primary rays,
    /// not recursive/shadow rays and not a Cinebench-comparable score. Call from a background coordinator.
    /// </summary>
    public static void RunContinuous(int threads, CancellationToken token, Action<CpuRenderSample>? report = null)
    {
        int workerCount = Math.Clamp(threads, 1, 256);
        token.ThrowIfCancellationRequested();
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
        var clock = Stopwatch.StartNew();
        var pixels = new byte[Width * Height * 4];
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        object imageLock = new();
        long nextRow = -1;
        long completedRows = 0;
        long[] rays = new long[workerCount];
        Task[] workers = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            int worker = i;
            workers[i] = Task.Factory.StartNew(() =>
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                var scene = Scene.CreateDefault();
                var rowPixels = new byte[Width * 4];
                try
                {
                    while (!stopping.IsCancellationRequested)
                    {
                        int y = (int)(Interlocked.Increment(ref nextRow) % Height);
                        for (int x = 0; x < Width; x++)
                        {
                            if ((x & 15) == 0 && stopping.IsCancellationRequested) return;
                            double u = (x + 0.5) / Width * 2 - 1;
                            double v = 1 - (y + 0.5) / Height * 2;
                            Vector3 color = scene.Trace(scene.Camera.CreateRay(u, v), Bounces);
                            int offset = x * 4;
                            rowPixels[offset] = ToByte(Math.Pow(Math.Clamp(color.Z, 0, 1), 1 / 2.2));
                            rowPixels[offset + 1] = ToByte(Math.Pow(Math.Clamp(color.Y, 0, 1), 1 / 2.2));
                            rowPixels[offset + 2] = ToByte(Math.Pow(Math.Clamp(color.X, 0, 1), 1 / 2.2));
                            rowPixels[offset + 3] = 255;
                        }
                        lock (imageLock) Buffer.BlockCopy(rowPixels, 0, pixels, y * Width * 4, rowPixels.Length);
                        Interlocked.Add(ref rays[worker], Width);
                        Interlocked.Increment(ref completedRows);
                    }
                }
                catch
                {
                    stopping.Cancel(); // stop peers too, then propagate the actual worker failure
                    throw;
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        try
        {
            while (!stopping.Token.WaitHandle.WaitOne(500))
            {
                long primaryRays = 0;
                for (int i = 0; i < rays.Length; i++) primaryRays += Interlocked.Read(ref rays[i]);
                byte[] snapshot;
                lock (imageLock) snapshot = (byte[])pixels.Clone();
                report?.Invoke(new CpuRenderSample(primaryRays, Interlocked.Read(ref completedRows),
                    primaryRays / Math.Max(0.001, clock.Elapsed.TotalSeconds), clock.Elapsed,
                    workerCount, Width, Height, snapshot));
            }
        }
        finally
        {
            stopping.Cancel();
            // Every dedicated worker has exited before returning; no detached load survives cancellation.
            Task.WaitAll(workers);
        }
        token.ThrowIfCancellationRequested();
    }

    private static byte ToByte(double value) => double.IsFinite(value) ? (byte)Math.Clamp(value * 255.0, 0, 255) : (byte)0;

    // ============================ Сцена ============================

    private readonly record struct Vector3(double X, double Y, double Z)
    {
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vector3 operator *(Vector3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
        public static Vector3 operator *(Vector3 a, Vector3 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z);

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

        public Vector3 Normalized()
        {
            double length = Length;
            return length <= 1e-12 ? this : new Vector3(X / length, Y / length, Z / length);
        }

        public static double Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static Vector3 Cross(Vector3 a, Vector3 b) => new(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);
    }

    private readonly record struct Ray(Vector3 Origin, Vector3 Direction);

    private sealed class Sphere
    {
        public required Vector3 Center { get; init; }
        public required double Radius { get; init; }
        public required Vector3 Color { get; init; }
        public double Reflectivity { get; init; } = 0.3;
    }

    private sealed class Camera
    {
        public Vector3 Origin { get; init; } = new(0, 1.2, -4.2);
        public Vector3 Target { get; init; } = new(0, 1.1, 0);
        public double FieldOfView { get; init; } = 45;

        public Ray CreateRay(double u, double v, double aspect = Width / (double)Height)
        {
            Vector3 forward = (Target - Origin).Normalized();
            Vector3 right = Vector3.Cross(forward, new Vector3(0, 1, 0)).Normalized();
            Vector3 up = Vector3.Cross(right, forward);

            double scale = Math.Tan(FieldOfView * Math.PI / 180 / 2);
            Vector3 direction = (forward + right * (u * scale * aspect) + up * (v * scale)).Normalized();
            return new Ray(Origin, direction);
        }
    }

    private sealed class Scene
    {
        private readonly List<Sphere> _spheres = new();
        private readonly List<(Vector3 Position, Vector3 Color)> _lights = new();

        public Camera Camera { get; } = new();

        public static Scene CreateDefault()
        {
            var scene = new Scene();
            scene._spheres.Add(new Sphere { Center = new Vector3(0, 1, 0), Radius = 1, Color = new Vector3(0.85, 0.25, 0.22) });
            scene._spheres.Add(new Sphere { Center = new Vector3(1.9, 0.6, 1.4), Radius = 0.6, Color = new Vector3(0.2, 0.55, 0.9), Reflectivity = 0.6 });
            scene._spheres.Add(new Sphere { Center = new Vector3(-1.8, 0.75, 1.1), Radius = 0.75, Color = new Vector3(0.95, 0.75, 0.2), Reflectivity = 0.45 });
            scene._spheres.Add(new Sphere { Center = new Vector3(0.6, 0.35, -1.6), Radius = 0.35, Color = new Vector3(0.35, 0.85, 0.4), Reflectivity = 0.7 });
            scene._lights.Add((new Vector3(4, 6, -3), new Vector3(1.0, 0.96, 0.9)));
            scene._lights.Add((new Vector3(-5, 3, 4), new Vector3(0.35, 0.45, 0.8)));
            return scene;
        }

        public Vector3 Trace(Ray ray, int bounces)
        {
            double closest = double.MaxValue;
            Sphere? hitSphere = null;

            foreach (Sphere sphere in _spheres)
            {
                Vector3 oc = ray.Origin - sphere.Center;
                double b = Vector3.Dot(oc, ray.Direction);
                double c = Vector3.Dot(oc, oc) - sphere.Radius * sphere.Radius;
                double discriminant = b * b - c;
                if (discriminant <= 0) continue;

                double sqrt = Math.Sqrt(discriminant);
                double t = -b - sqrt;
                if (t < 0.001) t = -b + sqrt;
                if (t < 0.001 || t >= closest) continue;

                closest = t;
                hitSphere = sphere;
            }

            // Плоскость пола.
            if (Math.Abs(ray.Direction.Y) > 1e-9)
            {
                double t = -ray.Origin.Y / ray.Direction.Y;
                if (t > 0.001 && t < closest)
                {
                    closest = t;
                    hitSphere = null;
                }
            }

            if (closest == double.MaxValue) return new Vector3(0.05, 0.06, 0.09);

            Vector3 point = ray.Origin + ray.Direction * closest;
            Vector3 normal = hitSphere is null
                ? new Vector3(0, 1, 0)
                : (point - hitSphere.Center).Normalized();
            Vector3 albedo = hitSphere?.Color ?? new Vector3(0.55, 0.55, 0.6);

            Vector3 color = new(0, 0, 0);
            foreach ((Vector3 lightPosition, Vector3 lightColor) in _lights)
            {
                Vector3 toLight = lightPosition - point;
                double distance = toLight.Length;
                Vector3 lightDirection = toLight.Normalized();
                double diffuse = Math.Max(0, Vector3.Dot(normal, lightDirection));
                double attenuation = 1.0 / (1 + distance * distance * 0.05);
                bool shadowed = IsShadowed(new Ray(point + normal * 0.002, lightDirection), distance);
                if (!shadowed) color += albedo * lightColor * (diffuse * attenuation);
            }

            color += albedo * 0.06;

            double reflectivity = hitSphere?.Reflectivity ?? 0.12;
            if (bounces > 0 && reflectivity > 0.01)
            {
                Vector3 reflected = ray.Direction - normal * (2 * Vector3.Dot(ray.Direction, normal));
                Vector3 recursive = Trace(new Ray(point + normal * 0.002, reflected.Normalized()), bounces - 1);
                color += recursive * reflectivity;
            }

            return new Vector3(Math.Min(1, color.X), Math.Min(1, color.Y), Math.Min(1, color.Z));
        }

        private bool IsShadowed(Ray ray, double maxDistance)
        {
            foreach (Sphere sphere in _spheres)
            {
                Vector3 oc = ray.Origin - sphere.Center;
                double b = Vector3.Dot(oc, ray.Direction);
                double c = Vector3.Dot(oc, oc) - sphere.Radius * sphere.Radius;
                double discriminant = b * b - c;
                if (discriminant <= 0) continue;

                double sqrt = Math.Sqrt(discriminant);
                double t = -b - sqrt;
                if (t > 0.01 && t < maxDistance) return true;
            }

            return false;
        }
    }
}
