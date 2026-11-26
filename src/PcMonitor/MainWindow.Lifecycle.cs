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
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            StartupDiagnostics.Mark("window.loaded");
            _service.Start();
            StartupDiagnostics.Mark("sensor.polling.started");
            if (_settings.StressTestsAccepted is not null) return;

            var consent = new SafetyConsentWindow(_settings.Language) { Owner = this };
            bool accepted = consent.ShowDialog() == true && consent.Accepted;
            _settings.Language = consent.SelectedLanguage;
            _settings.StressTestsAccepted = accepted;
            _settings.Save();
            ApplyLanguage();
            ApplyTestingAccess();

            if (!accepted)
            {
                Tabs.SelectedIndex = 0;
                SetStatus(LocalizationManager.Get(LocalizationKeys.MonitoringOnly, _settings.Language));
            }
        }
        private void MainWindow_ContentRendered(object? sender, EventArgs e)
        {
            StartupDiagnostics.Mark("first.render");
            StartupDiagnostics.Mark("window.visible");
        }
        private void ConsentButton_Click(object sender, RoutedEventArgs e)
        {
            var consent = new SafetyConsentWindow(_settings.Language) { Owner = this };
            bool accepted = consent.ShowDialog() == true && consent.Accepted;
            _settings.Language = consent.SelectedLanguage;
            _settings.StressTestsAccepted = accepted;
            _settings.Save();
            ApplyLanguage();
            ApplyTestingAccess();

            if (accepted)
                SetStatus(LocalizationManager.Get(LocalizationKeys.TestingEnabled, _settings.Language));
        }
        private void SafetyInfoButton_Click(object sender, RoutedEventArgs e)
        {
            var info = new SafetyConsentWindow(_settings.Language, acceptedInfoOnly: true) { Owner = this };
            info.ShowDialog();
        }
        private void EnsureTabLoaded(int index)
        {
            if (_context is null || !_loadedTabs.Add(index)) return;
            StartupDiagnostics.Mark($"tab.{index}.loading");
            switch (index)
            {
                case 0:
                    Overview.Attach(_context);
                    break;
                case 1:
                    Sensors.Attach(_context);
                    Sensors.StatusChanged += SetStatus;
                    Sensors.TileRequested += AddTileForSensor;
                    break;
                case 2:
                    SystemOverview.Attach(_context);
                    break;
                case 3:
                    AutoTest.Attach(_context);
                    AutoTest.StatusChanged += SetStatus;
                    break;
                case 4:
                    Stress.Attach(_context);
                    break;
                case 5:
                    Renders.Attach(_context);
                    Renders.StatusChanged += SetStatus;
                    break;
                case 6:
                    SettingsPane.Attach(_context);
                    SettingsPane.StatusChanged += HandleSettingsStatus;
                    SettingsPane.SettingsChanged += OnSettingsChanged;
                    SettingsPane.LanguageChanged += OnLanguageChanged;
                    SettingsPane.ApplyLanguage(_settings.Language);
                    SettingsPane.RestoreLanguageSelection(_settings.Language);
                    SettingsPane.RestoreThemeSelection(_settings.Theme);
                    break;
                case 7:
                    AboutPane.ApplyLanguage(_settings.Language);
                    break;
            }
            StartupDiagnostics.Mark($"tab.{index}.loaded");
        }
        private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.OriginalSource is not TabItem && !ReferenceEquals(e.OriginalSource, Tabs)) return;
            if (!_ready) return;
            if (Tabs.SelectedItem is not TabItem selectedTab) return;
            EnsureTabLoaded(Tabs.SelectedIndex);
            foreach (TabItem tab in Tabs.Items.OfType<TabItem>())
                tab.IsSelected = ReferenceEquals(tab, selectedTab);
            UpdateSidebarSelection();
            // A view may have applied its own language to the shared manager; if it no
            // longer matches the stored setting, re-apply the whole window so the UI
            // never ends up half translated.
            if (!string.Equals(LocalizationManager.Language, _settings.Language, StringComparison.Ordinal))
                ApplyLanguage();
            else if (Tabs.SelectedContent is DependencyObject selectedContent)
                LocalizationManager.Apply(selectedContent, _settings.Language);
            _settings.LastTab = Tabs.SelectedIndex;
            _settings.Save();

            // Рендеры считаются только пока вкладка видна — иначе они бы занимали видеокарту.
            bool rendersVisible = Tabs.SelectedIndex >= 0 && Tabs.SelectedIndex < Tabs.Items.Count &&
                                  ReferenceEquals(((TabItem)Tabs.Items[Tabs.SelectedIndex]).Content, Renders);
            Renders.IsActive = rendersVisible;
            if (!rendersVisible) Renders.Stop();
        }
        protected override void OnClosing(CancelEventArgs e)
        {
            _ready = false;
            LocalizationManager.Release(this);
            foreach (DependencyObject view in new DependencyObject[]
            {
                Overview, Sensors, SystemOverview, AutoTest, Stress, Renders, SettingsPane, AboutPane,
            })
            {
                LocalizationManager.Release(view);
            }
            _settings.LastTab = Tabs.SelectedIndex;
            _settings.Save();
            StopLogging();

            try
            {
                UnregisterHotKey(new WindowInteropHelper(this).Handle, HotKeyId);
                UnregisterHotKey(new WindowInteropHelper(this).Handle, StopAllHotKeyId);
            }
            catch
            {
                // Горячая клавиша могла не зарегистрироваться — это не критично.
            }

            if (_tray is not null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }

            _osd?.Close();
            _discordPresence?.Dispose();
            _discordPresence = null;
            _service.Dispose();
            base.OnClosing(e);
        }
}
