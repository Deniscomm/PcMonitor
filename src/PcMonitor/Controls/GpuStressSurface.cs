using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace PcMonitor.Controls;

/// <summary>
/// Трёхмерная сцена для GPU-нагрузки в духе GPU: вращающийся «жгут» из
/// тысяч треугольников, счётчик FPS и снимок кадра для поиска артефактов.
/// </summary>
public sealed class GpuStressSurface : Grid
{
    private readonly Viewport3D _viewport = new();
    private readonly Model3DGroup _scene = new();
    private readonly List<GeometryModel3D> _models = new();
    private readonly AxisAngleRotation3D _spin = new(new Vector3D(0.35, 1, 0.15), 0);
    private readonly Random _random = new(20260101);

    private DateTime _fpsWindowStart = DateTime.UtcNow;
    private int _framesInWindow;
    private bool _running;
    private int _loadLevel = 5;

    public GpuStressSurface()
    {
        ClipToBounds = true;
        Background = new SolidColorBrush(Color.FromRgb(10, 12, 18));

        MeshGeometry3D mesh = TorusKnotMesh.Build(segments: 360, tubeSegments: 40, tubeRadius: 0.34);

        var camera = new PerspectiveCamera
        {
            Position = new Point3D(0, 0, 6.4),
            LookDirection = new Vector3D(0, 0, -1),
            UpDirection = new Vector3D(0, 1, 0),
            FieldOfView = 55,
        };

        _scene.Children.Add(new AmbientLight(Color.FromRgb(40, 42, 56)));
        _scene.Children.Add(new DirectionalLight(Color.FromRgb(170, 195, 255), new Vector3D(-1, -1, -1)));
        _scene.Children.Add(new DirectionalLight(Color.FromRgb(255, 150, 90), new Vector3D(1, 0.6, -0.4)));
        _scene.Children.Add(new DirectionalLight(Colors.White, new Vector3D(0.2, 1, 0.6)));

        for (int i = 0; i < 12; i++)
        {
            var transform = new Transform3DGroup();
            transform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(
                new Vector3D(_random.NextDouble() - 0.5, _random.NextDouble() - 0.5, _random.NextDouble() - 0.5),
                _random.Next(0, 360))));
            transform.Children.Add(new ScaleTransform3D(0.95, 0.95, 0.95));
            transform.Children.Add(new TranslateTransform3D(
                (_random.NextDouble() - 0.5) * 3.0,
                (_random.NextDouble() - 0.5) * 2.6,
                (_random.NextDouble() - 0.5) * 2.0));

            var model = new GeometryModel3D
            {
                Geometry = mesh,
                Material = CreateMaterial(i),
                BackMaterial = CreateMaterial(i),
                Transform = transform,
            };

            _models.Add(model);
            _scene.Children.Add(model);
        }

        _viewport.Camera = camera;
        _viewport.Children.Add(new ModelVisual3D { Content = _scene, Transform = new RotateTransform3D(_spin) });
        Children.Add(_viewport);

        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
    }

    /// <summary>Сколько экземпляров модели рисуется: 1 — минимум, 12 — максимум нагрузки.</summary>
    public int LoadLevel
    {
        get => _loadLevel;
        set
        {
            _loadLevel = Math.Clamp(value, 1, _models.Count);            for (int i = 0; i < _models.Count; i++)
            {
                bool visible = i < _loadLevel;
                bool present = _scene.Children.Contains(_models[i]);

                if (visible && !present) _scene.Children.Add(_models[i]);
                else if (!visible && present) _scene.Children.Remove(_models[i]);
            }
        }
    }

    /// <summary>Текущая частота кадров.</summary>
    public double Fps { get; private set; }

    /// <summary>Вызывается примерно раз в секунду с новым значением FPS.</summary>
    public event Action<double>? FpsUpdated;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _fpsWindowStart = DateTime.UtcNow;
        _framesInWindow = 0;
        CompositionTarget.Rendering += OnRendering;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        _spin.Angle = (_spin.Angle + 1.1) % 360;
        _framesInWindow++;

        TimeSpan elapsed = DateTime.UtcNow - _fpsWindowStart;
        if (elapsed.TotalSeconds >= 1)
        {
            Fps = _framesInWindow / elapsed.TotalSeconds;
            _framesInWindow = 0;
            _fpsWindowStart = DateTime.UtcNow;
            FpsUpdated?.Invoke(Fps);
        }
    }

    /// <summary>
    /// Снимок кадра в детерминированном состоянии: используется для поиска артефактов
    /// (сравнение с эталонным кадром пиксель в пиксель).
    /// </summary>
    public byte[] CaptureDeterministicFrame(int size = 192)
    {
        double savedAngle = _spin.Angle;
        _spin.Angle = 0;
        UpdateLayout();

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);

        int stride = size * 4;
        var pixels = new byte[size * stride];
        bitmap.CopyPixels(pixels, stride, 0);

        _spin.Angle = savedAngle;
        return pixels;
    }

    /// <summary>
    /// Частота кадров без вертикальной синхронизации: кадр рисуется в память
    /// (RenderTargetBitmap) в плотном цикле. Такая оценка не упирается в 60 FPS
    /// и хорошо показывает разницу между настройками и разгоном.
    /// </summary>
    public double MeasureOffscreenFps(int size, TimeSpan duration)
    {
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        double savedAngle = _spin.Angle;
        int frames = 0;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (stopwatch.Elapsed < duration)
        {
            _spin.Angle = (_spin.Angle + 3) % 360;
            bitmap.Render(this);
            frames++;
        }

        double seconds = stopwatch.Elapsed.TotalSeconds;
        _spin.Angle = savedAngle;
        return seconds > 0 ? frames / seconds : 0;
    }

    private static Material CreateMaterial(int index)
    {
        Color[] palette =
        {
            Color.FromRgb(255, 120, 60),
            Color.FromRgb(90, 170, 255),
            Color.FromRgb(120, 230, 160),
            Color.FromRgb(220, 120, 255),
            Color.FromRgb(255, 210, 90),
        };

        Color color = palette[index % palette.Length];
        var brush = new LinearGradientBrush(
            Color.FromRgb((byte)(color.R * 0.35), (byte)(color.G * 0.35), (byte)(color.B * 0.35)),
            color,
            new Point(0, 0),
            new Point(1, 1));
        brush.Freeze();

        var group = new MaterialGroup();
        group.Children.Add(new DiffuseMaterial(brush));
        group.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(200, 200, 200)), 45));
        group.Freeze();
        return group;
    }
}

/// <summary>Генератор геометрии торического узла (трубка вокруг узла (2,3)).</summary>
public static class TorusKnotMesh
{
    public static MeshGeometry3D Build(int segments, int tubeSegments, double tubeRadius, int p = 2, int q = 3)
    {
        var mesh = new MeshGeometry3D();
        var positions = new Point3DCollection(segments * tubeSegments);
        var normals = new Vector3DCollection(segments * tubeSegments);
        var indices = new Int32Collection(segments * tubeSegments * 6);

        Point3D Curve(double u)
        {
            double r = 2 + Math.Cos(q * u);
            return new Point3D(r * Math.Cos(p * u), r * Math.Sin(p * u), Math.Sin(q * u) * 1.4);
        }

        for (int i = 0; i < segments; i++)
        {
            double u = i / (double)segments * Math.PI * 2;
            Point3D center = Curve(u);
            Vector3D tangent = Curve(u + 0.001) - Curve(u - 0.001);
            tangent.Normalize();

            var reference = new Vector3D(0, 0, 1);
            if (Math.Abs(Vector3D.DotProduct(tangent, reference)) > 0.95) reference = new Vector3D(0, 1, 0);

            Vector3D normal = Vector3D.CrossProduct(tangent, reference);
            normal.Normalize();
            Vector3D binormal = Vector3D.CrossProduct(tangent, normal);
            binormal.Normalize();

            for (int j = 0; j < tubeSegments; j++)
            {
                double v = j / (double)tubeSegments * Math.PI * 2;
                Vector3D offset = normal * (Math.Cos(v) * tubeRadius) + binormal * (Math.Sin(v) * tubeRadius);
                positions.Add(center + offset);
                normals.Add(offset);
            }
        }

        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % segments;
            for (int j = 0; j < tubeSegments; j++)
            {
                int nextTube = (j + 1) % tubeSegments;
                int a = i * tubeSegments + j;
                int b = next * tubeSegments + j;
                int c = next * tubeSegments + nextTube;
                int d = i * tubeSegments + nextTube;

                indices.Add(a); indices.Add(b); indices.Add(c);
                indices.Add(a); indices.Add(c); indices.Add(d);
            }
        }

        mesh.Positions = positions;
        mesh.Normals = normals;
        mesh.TriangleIndices = indices;
        mesh.Freeze();
        return mesh;
    }
}
