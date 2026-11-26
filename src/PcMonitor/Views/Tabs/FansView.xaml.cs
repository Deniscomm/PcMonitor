using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LibreHardwareMonitor.Hardware;
using PcMonitor.Core;

namespace PcMonitor.Views;

public sealed record FanSensorChoice(string Id, string Display)
{
    public override string ToString() => Display;
}

public sealed class FanRow : INotifyPropertyChanged
{
    private readonly Action<FanRow> _changed;
    private bool _enabled;
    private string _status = "Управление BIOS. Профиль не включён.";
    private string _rpm = "— об/мин";
    private string _actual = "— %";
    public FanRow(FanControlInfo info, FanProfile profile, Action<FanRow> changed,
        ObservableCollection<FanSensorChoice> temperatures, ObservableCollection<FanSensorChoice> rpms)
    {
        Id = info.Id;
        Hardware = info.Hardware;
        Profile = profile.Copy();
        Profile.Normalize();
        if (string.IsNullOrWhiteSpace(Profile.Name)) Profile.Name = info.Name;
        _changed = changed;
        Temperatures = temperatures;
        Rpms = rpms;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; }
    public string Hardware { get; }
    public FanProfile Profile { get; }
    public string[] Modes { get; } = [FanProfile.Bios, FanProfile.Manual, FanProfile.Linear, FanProfile.Graph, FanProfile.Mix];
    public string[] MixFunctions { get; } = ["Максимум", "Минимум", "Среднее"];
    public ObservableCollection<FanSensorChoice> Temperatures { get; }
    public ObservableCollection<FanSensorChoice> Rpms { get; }
    public string Name { get => Profile.Name; set { if (Profile.Name == value) return; Profile.Name = value; Change(); } }
    public bool Enabled { get => _enabled; set { if (_enabled == value) return; _enabled = value; Change(); } }
    public string Mode
    {
        get => Profile.Mode;
        set { if (Profile.Mode == value) return; Profile.Mode = value; _enabled = value != FanProfile.Bios; Raise(nameof(Enabled)); Change(); }
    }
    public double SliderValue
    {
        get => Profile.ManualPercent;
        set
        {
            if (Math.Abs(Profile.ManualPercent - value) < 0.01) return;
            Profile.ManualPercent = value;
            Profile.Mode = FanProfile.Manual;
            _enabled = true;
            Raise(nameof(Mode)); Raise(nameof(Enabled)); Raise(nameof(PercentText)); Change();
        }
    }
    public string PercentText => $"{Profile.ManualPercent:0} %";
    public string SensorId { get => Profile.SensorId; set { if (value is null || value == Profile.SensorId) return; Profile.SensorId = value; Change(); } }
    public string SecondSensorId { get => Profile.SecondSensorId; set { if (value is null || value == Profile.SecondSensorId) return; Profile.SecondSensorId = value; Change(); } }
    public string RpmSensorId { get => Profile.RpmSensorId; set { if (value is null || value == Profile.RpmSensorId) return; Profile.RpmSensorId = value; Change(); } }
    public string MixFunction { get => Profile.MixFunction; set { if (value is null || value == Profile.MixFunction) return; Profile.MixFunction = value; Change(); } }
    public string Points { get => Profile.Points; set { if (value == Profile.Points) return; Profile.Points = value; Change(); } }
    public double MinimumPercent { get => Profile.MinimumPercent; set { Profile.MinimumPercent = value; Change(); } }
    public double MaximumPercent { get => Profile.MaximumPercent; set { Profile.MaximumPercent = value; Change(); } }
    public double LowTemperature { get => Profile.LowTemperature; set { Profile.LowTemperature = value; Change(); } }
    public double HighTemperature { get => Profile.HighTemperature; set { Profile.HighTemperature = value; Change(); } }
    public double CriticalTemperature { get => Profile.CriticalTemperature; set { Profile.CriticalTemperature = value; Change(); } }
    public double Hysteresis { get => Profile.Hysteresis; set { Profile.Hysteresis = value; Change(); } }
    public double StepUpPerSecond { get => Profile.StepUpPerSecond; set { Profile.StepUpPerSecond = value; Change(); } }
    public double StepDownPerSecond { get => Profile.StepDownPerSecond; set { Profile.StepDownPerSecond = value; Change(); } }
    public double DownDelaySeconds { get => Profile.DownDelaySeconds; set { Profile.DownDelaySeconds = value; Change(); } }
    public bool AllowStop { get => Profile.AllowStop; set { Profile.AllowStop = value; Change(); } }
    public Visibility ManualVisibility => Profile.Mode == FanProfile.Manual ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CurveVisibility => Profile.Mode is FanProfile.Linear or FanProfile.Graph or FanProfile.Mix ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LinearVisibility => Profile.Mode == FanProfile.Linear ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GraphVisibility => Profile.Mode is FanProfile.Graph or FanProfile.Mix ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MixVisibility => Profile.Mode == FanProfile.Mix ? Visibility.Visible : Visibility.Collapsed;
    public string Status { get => _status; set { _status = value; Raise(); } }
    public string RpmText => _rpm;
    public string ActualText => _actual;
    public PointCollection CurvePreview
    {
        get
        {
            FanPoint[] points = FanCurve.TryParse(Profile.Points, out FanPoint[] parsed) && Profile.Mode != FanProfile.Linear
                ? parsed : [new(Profile.LowTemperature, Profile.MinimumPercent), new(Profile.HighTemperature, Profile.MaximumPercent)];
            return new PointCollection(points.Select(p => new Point(Math.Clamp(p.Temperature / 110 * 290, 0, 290), 80 - p.Percent * 0.8)));
        }
    }
    public void Update(FanControlInfo info, MonitorContext context)
    {
        string rpmId = string.IsNullOrWhiteSpace(Profile.RpmSensorId) ? info.RpmSensorId : Profile.RpmSensorId;
        float? rpm = context.ById.TryGetValue(rpmId, out SensorRow? sensor) && sensor.Type == SensorType.Fan ? sensor.Value : info.Rpm;
        _rpm = rpm.HasValue && float.IsFinite(rpm.Value) ? $"{rpm:0} об/мин" : "— об/мин";
        _actual = float.IsFinite(info.Value) && info.Mode.Contains("Software", StringComparison.OrdinalIgnoreCase)
            ? $"{info.Value:0} % PWM" : "BIOS";
        Raise(nameof(RpmText)); Raise(nameof(ActualText));
        if (_enabled && !string.IsNullOrWhiteSpace(info.Status)) Status = info.Status;
        else if (!_enabled) Status = "BIOS · выберите режим или включите профиль";
    }
    public void BiosOnly() { _enabled = false; Raise(nameof(Enabled)); Status = "BIOS"; }
    private void Change([CallerMemberName] string? property = null)
    {
        Raise(property); Raise(nameof(CurvePreview));
        Raise(nameof(ManualVisibility)); Raise(nameof(CurveVisibility)); Raise(nameof(LinearVisibility));
        Raise(nameof(GraphVisibility)); Raise(nameof(MixVisibility));
        _changed(this);
    }
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class FansView : UserControl
{
    private readonly ObservableCollection<FanRow> _fans = new();
    private readonly ObservableCollection<FanSensorChoice> _temperatures = new()
    {
        new("@cpu", "CPU · максимальная температура"), new("@gpu", "GPU · максимальная температура"),
    };
    private readonly ObservableCollection<FanSensorChoice> _rpms = new() { new("", "Авто · тот же аппаратный канал") };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private MonitorContext? _context;
    private bool _syncing;
    public FansView()
    {
        InitializeComponent();
        FanList.ItemsSource = _fans;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _context?.Settings.Save(); };
    }
    public event Action<string>? StatusChanged;
    public void Attach(MonitorContext context)
    {
        if (_context is not null) _context.Updated -= Reload;
        _context = context;
        _context.Updated += Reload;
        Reload();
    }
    public void Stop()
    {
        _saveTimer.Stop();
        _context?.Settings.Save();
        if (_context is not null) _context.Updated -= Reload;
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();
    private void Reload()
    {
        if (_context is null) return;
        _syncing = true;
        try
        {
            foreach (SensorRow sensor in _context.Rows)
            {
                if (sensor.Type == SensorType.Temperature && !sensor.IsThreshold && sensor.IsPlausible
                    && !_temperatures.Any(s => s.Id == sensor.Id))
                    _temperatures.Add(new(sensor.Id, $"{sensor.OwnName} · {sensor.DisplayName}"));
                if (sensor.Type == SensorType.Fan && !_rpms.Any(s => s.Id == sensor.Id))
                    _rpms.Add(new(sensor.Id, $"{sensor.OwnName} · {sensor.DisplayName}"));
            }
            IReadOnlyList<FanControlInfo> controls = _context.Service.FanControls();
            foreach (FanControlInfo info in controls)
            {
                FanRow? row = _fans.FirstOrDefault(f => f.Id == info.Id);
                if (row is null)
                {
                    FanProfile profile = _context.Settings.FanProfiles.TryGetValue(info.Id, out FanProfile? saved)
                        ? saved : new FanProfile();
                    row = new FanRow(info, profile, ProfileChanged, _temperatures, _rpms);
                    _fans.Add(row);
                }
                row.Update(info, _context);
            }
            foreach (FanRow row in _fans.Where(f => !controls.Any(c => c.Id == f.Id)))
                row.Status = "Контроллер недоступен. PWM не применяется.";
            SummaryText.Text = controls.Count > 0
                ? $"{controls.Count} контроллеров · изменения PWM сразу · кривые работают независимо от вкладки"
                : "Контроллеры пока не найдены. Нужны поддерживаемые разъёмы платы/видеокарты, PawnIO и права администратора. RPM-датчик без контроллера доступен во вкладке «Датчики».";
        }
        finally { _syncing = false; }
    }
    private void ProfileChanged(FanRow row)
    {
        if (_syncing || _context is null) return;
        if (row.Profile.Mode is FanProfile.Graph or FanProfile.Mix && !FanCurve.TryParse(row.Profile.Points, out _))
        {
            row.Status = "Некорректные точки: пример 30:30; 60:60; 85:100. Последняя рабочая кривая сохранена.";
            return;
        }
        row.Profile.Normalize();
        string result = row.Enabled && row.Mode != FanProfile.Bios
            ? _context.Service.ConfigureFan(row.Id, row.Profile)
            : _context.Service.RestoreFan(row.Id);
        row.Status = result;
        _context.Settings.FanProfiles[row.Id] = row.Profile.Copy();
        _saveTimer.Stop(); _saveTimer.Start();
        StatusChanged?.Invoke(result);
    }
    private void AllAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_context is null) return;
        foreach (FanRow row in _fans) row.BiosOnly();
        StatusChanged?.Invoke(_context.Service.RestoreAllFans());
    }
    private void All100_Click(object sender, RoutedEventArgs e)
    {
        foreach (FanRow row in _fans)
        {
            row.SliderValue = 100;
            row.Mode = FanProfile.Manual;
            row.Enabled = true;
        }
    }
    private void Auto_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FanRow row || _context is null) return;
        row.BiosOnly();
        StatusChanged?.Invoke(_context.Service.RestoreFan(row.Id));
    }
}
