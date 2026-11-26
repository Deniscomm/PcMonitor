using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
using PcMonitor.Core.Integrations;
using PcMonitor.Views;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace PcMonitor;

/// <summary>Функциональная часть главного окна, выделенная для независимых изменений.</summary>
public partial class MainWindow
{
        private void OnSettingsChanged()
        {
            try
            {
                UpdateDiscordPresence();
                TopMostButton.IsChecked = _settings.TopMost;
                Topmost = _settings.TopMost;
                ThemeManager.Apply(_settings.Theme);
                if (_loadedTabs.Contains(2)) SystemOverview.ApplyTheme();

                OsdButton.IsChecked = _settings.OsdEnabled;
                if (_settings.OsdEnabled)
                {
                    EnsureOsd();
                    if (!_osd!.IsVisible) _osd.Show();
                    _osd.Rebuild();
                }
                else if (_osd is not null)
                {
                    if (_osd.IsVisible) _osd.Hide();
                    _osd.Rebuild();
                }

                UpdateStatus(DateTime.Now);
            }
            catch (Exception ex)
            {
                ShowErrorToast(LocalizationManager.Get(LocalizationKeys.ChangeLanguageFailed, _settings.Language));
                SetStatus(ex.Message, Theme.Bad);
            }
        }

        private void UpdateDiscordPresence()
        {
            string applicationId = _settings.DiscordApplicationId.Trim();
            bool validId = applicationId.Length is >= 17 and <= 20 &&
                           applicationId.All(char.IsAsciiDigit) &&
                           ulong.TryParse(applicationId, NumberStyles.None, CultureInfo.InvariantCulture, out _);
            if (!_settings.DiscordPresenceEnabled || !validId)
            {
                _discordPresence?.Dispose();
                _discordPresence = null;
                return;
            }

            if (_discordPresence?.ApplicationId == applicationId) return;

            _discordPresence?.Dispose();
            _discordPresence = new DiscordPresenceService(
                applicationId,
                LocalizationManager.Get("Запуск мониторинга ПК", _settings.Language),
                LocalizationManager.Get("Мониторинг температуры и датчиков", _settings.Language));
            _discordPresence.Start();
        }

        private void OnLanguageChanged(string language)
        {
            _settings.Language = language;
            _discordPresence?.Dispose();
            _discordPresence = null;
            UpdateDiscordPresence();
            ApplyLanguage();
            if (_loadedTabs.Contains(6)) SettingsPane.RestoreLanguageSelection(language);
        }
        private void HandleSettingsStatus(string message)
        {
            SetStatus(message);
            if (message.StartsWith("Не удалось", StringComparison.OrdinalIgnoreCase))
                ShowErrorToast(LocalizationManager.Get(LocalizationKeys.ApplySettingsFailed, _settings.Language));
        }
        private async void ShowErrorToast(string message)
        {
            ErrorToastText.Text = message;
            ErrorToast.Visibility = Visibility.Visible;
            if (ErrorToast.RenderTransform is not TranslateTransform transform)
            {
                transform = new TranslateTransform(360, 0);
                ErrorToast.RenderTransform = transform;
            }

            transform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(360, 0, TimeSpan.FromMilliseconds(220)));
            await Task.Delay(2500);
            transform.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(220)));
            await Task.Delay(240);
            ErrorToast.Visibility = Visibility.Collapsed;
        }
        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            bool paused = PauseButton.IsChecked == true;
            _service.IsPaused = paused;
            PauseButton.Content = LocalizationManager.Get(paused ? LocalizationKeys.ActionResume : LocalizationKeys.ActionPause);
            SetStatus(LocalizationManager.Get(paused ? LocalizationKeys.PollingPaused : LocalizationKeys.PollingResumed),
                paused ? Theme.Warn : Theme.TextDim);
        }
        private void ResetMinMax_Click(object sender, RoutedEventArgs e)
        {
            foreach (SensorRow row in _context.Rows)
                row.ResetMinMax();

            SetStatus(LocalizationManager.Get(LocalizationKeys.MinMaxReset));
        }
        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Экспорт показаний датчиков",
                FileName = $"pc-monitor-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
                Filter = "CSV (*.csv)|*.csv|Все файлы (*.*)|*.*",
                InitialDirectory = AppSettings.ReportsDirectory,
            };

            if (dialog.ShowDialog(this) != true) return;

            try
            {
                CsvWriter.ExportTable(dialog.FileName, _context.Rows, DateTime.Now);
                SetStatus(string.Format(LocalizationManager.Get(LocalizationKeys.SensorExported), _context.Rows.Count, dialog.FileName), Theme.Good);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, LocalizationManager.Get(LocalizationKeys.SaveFileFailed) + ":\n" + ex.Message, LocalizationManager.Get(LocalizationKeys.AppTitle),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        private void Log_Click(object sender, RoutedEventArgs e)
        {
            if (LogButton.IsChecked == true) StartLogging();
            else StopLogging();

            _settings.LogToCsv = _logger is not null;
            _settings.Save();
            UpdateStatus(DateTime.Now);
        }
        private void StartLogging()
        {
            if (_logger is not null) return;

            try
            {
                string logDirectory = _settings.EnsureLogDirectory();
                string path = Path.Combine(logDirectory, $"sensors-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
                _logger = new CsvWriter(path);
                LogButton.IsChecked = true;
            }
            catch (Exception ex)
            {
                LogButton.IsChecked = false;
                MessageBox.Show(this, LocalizationManager.Get(LocalizationKeys.StartCsvFailed) + ":\n" + ex.Message, LocalizationManager.Get(LocalizationKeys.AppTitle),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        private void StopLogging()
        {
            _logger?.Dispose();
            _logger = null;
            LogButton.IsChecked = false;
        }
        private void OpenLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string logDirectory = _settings.EnsureLogDirectory();
                Process.Start(new ProcessStartInfo("explorer.exe", logDirectory) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, LocalizationManager.Get(LocalizationKeys.OpenFolderFailed) + ":\n" + ex.Message, LocalizationManager.Get(LocalizationKeys.AppTitle),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        private void TopMost_Click(object sender, RoutedEventArgs e)
        {
            Topmost = TopMostButton.IsChecked == true;
            _settings.TopMost = Topmost;
            _settings.Save();
        }
        private void RestartElevated_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? exe = Environment.ProcessPath;
                if (exe is null) return;

                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, LocalizationManager.Get(LocalizationKeys.RestartAdminFailed) + ":\n" + ex.Message,
                    LocalizationManager.Get(LocalizationKeys.AppTitle), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
}
