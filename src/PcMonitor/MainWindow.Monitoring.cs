using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Win32;
using PcMonitor.Core;
using PcMonitor.Core.Diagnostics;
using PcMonitor.Views;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace PcMonitor;

/// <summary>Функциональная часть главного окна, выделенная для независимых изменений.</summary>
public partial class MainWindow
{
        private void OnSnapshotReady(IReadOnlyList<SensorReading> readings, DateTime timestamp)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action<IReadOnlyList<SensorReading>, DateTime>(ApplySnapshot), readings, timestamp);
                return;
            }

            ApplySnapshot(readings, timestamp);
        }
        private void ApplySnapshot(IReadOnlyList<SensorReading> readings, DateTime timestamp)
        {
            if (Interlocked.Exchange(ref _firstSnapshotApplied, 1) == 0)
                StartupDiagnostics.Mark($"first.snapshot.applied ({readings.Count} sensors)");

            bool structureChanged = false;

            foreach (SensorReading reading in readings)
            {
                if (_context.ById.TryGetValue(reading.Id, out SensorRow? row))
                {
                    row.Apply(reading, _settings);
                    continue;
                }

                _settings.SensorOverrides.TryGetValue(reading.Id, out SensorOverride? rule);
                row = new SensorRow(reading, _settings, rule);
                _context.ById[reading.Id] = row;
                _context.Rows.Add(row);
                structureChanged = true;
            }

            if (structureChanged) Sensors.NotifyStructureChanged();

            _context.LastUpdate = timestamp;
            // Каталог метрик сам применяет правила IsMeasurement. Передаём полный
            // снимок, чтобы агрегаты не терялись из-за раннего фильтра источника.
            List<SensorRow> measurements = _context.Rows.ToList();
            foreach (TileViewModel tile in _context.Tiles)
                tile.Update(measurements, _settings);

            _context.RaiseUpdated();
            UpdateStatus(timestamp);
            CheckAlarms();

            if (_logger is not null)
            {
                try
                {
                    _logger.WriteSnapshot(readings, timestamp);
                }
                catch (Exception ex)
                {
                    StopLogging();
                    SetStatus(LocalizationManager.Get(LocalizationKeys.CsvStopped).Replace("{0}", ex.Message, StringComparison.Ordinal));
                }
            }
        }
        private void OnServiceFailed(string message) =>
            Dispatcher.BeginInvoke(new Action(() => SetStatus(string.Format(LocalizationManager.Get(LocalizationKeys.SensorError), message), Theme.Bad)));

        private void SetStatus(string text) => SetStatus(text, Theme.TextDim);

        private void SetStatus(string text, System.Windows.Media.Brush brush)
        {
            StatusLeft.Text = text;
            StatusLeft.Foreground = brush;
        }
        private void UpdateStatus(DateTime timestamp)
        {
            if (_service.IsPaused)
            {
                StatusLeft.Text = string.Format(LocalizationManager.Get(LocalizationKeys.Paused), _context.Rows.Count, timestamp.ToString("HH:mm:ss"));
                return;
            }

            int withValues = _context.Rows.Count(r => r.HasValue);
            StatusLeft.Foreground = Theme.TextDim;
            StatusLeft.Text = string.Format(LocalizationManager.Get(LocalizationKeys.MonitoringSummary), _context.Rows.Count, withValues, _context.Tiles.Count, timestamp.ToString("HH:mm:ss"), _settings.Interval.TotalSeconds.ToString("0.#"));
            if (_logger is not null && (LocalizationManager.Language is "ru" or "ru-RU"))
                StatusLeft.Text += $" · запись: {Path.GetFileName(_logger.FilePath)}";
        }
        private void CheckAlarms()
        {
            List<SensorRow> alerts = _context.Rows.Where(r => r.IsAlert).ToList();
            if (alerts.Count == 0)
            {
                StatusRight.Text = LocalizationManager.Get(LocalizationKeys.TemperaturesNormal);
                StatusRight.Foreground = Theme.Good;
                return;
            }

            double max = alerts.Max(r => r.DisplayValue ?? 0);
            StatusRight.Text = string.Format(LocalizationManager.Get(LocalizationKeys.TemperatureExceeded), alerts.Count, max);
            StatusRight.Foreground = Theme.Bad;

            if (_settings.AlarmSound && (DateTime.Now - _lastAlarm).TotalSeconds > 30)
            {
                _lastAlarm = DateTime.Now;
                try
                {
                    SystemSounds.Exclamation.Play();
                }
                catch
                {
                    // Звук может быть недоступен — это не критично.
                }
            }
        }
        private void AddTileForSensor(SensorRow row)
        {
            var proposed = new TileConfig
            {
                MetricKey = MetricCatalog.SensorPrefix + row.Id,
                Title = row.DisplayName,
                Visual = row.HasBar ? TileVisual.Bar : TileVisual.Value,
            };

            var editor = new TileEditorWindow(_context.Rows, proposed) { Owner = this };
            if (editor.ShowDialog() != true || editor.Result is null) return;

            _context.AddTile(editor.Result);
            SetStatus(string.Format(LocalizationManager.Get(LocalizationKeys.SensorTileAdded), row.DisplayName), Theme.Good);
            Tabs.SelectedIndex = 1;
        }
}
