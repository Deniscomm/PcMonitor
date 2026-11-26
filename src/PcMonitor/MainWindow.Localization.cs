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
        private void ApplyLanguage()
        {
            string language = _settings.Language;
            Title = LocalizationManager.Get(LocalizationKeys.AppTitle, language);
            EasyModeButton.Content = LocalizationManager.Get(LocalizationKeys.ModeEasy, language);
            ProModeButton.Content = LocalizationManager.Get(LocalizationKeys.ModePro, language);
            OverviewTab.Header = LocalizationManager.Get(LocalizationKeys.NavOverview, language);
            SensorsTab.Header = LocalizationManager.Get(LocalizationKeys.NavSensors, language);
            SystemTab.Header = LocalizationManager.Get(LocalizationKeys.NavSystem, language);
            AutoTestTab.Header = LocalizationManager.Get(LocalizationKeys.NavAutoTest, language);
            StressTab.Header = LocalizationManager.Get(LocalizationKeys.NavStress, language);
            RendersTab.Header = LocalizationManager.Get(LocalizationKeys.NavRenders, language);
            SettingsTab.Header = LocalizationManager.Get(LocalizationKeys.NavSettings, language);
            AboutTab.Header = LocalizationManager.Get(LocalizationKeys.NavAbout, language);
            LocalizationManager.Apply(this, language);
            if (_loadedTabs.Contains(0)) Overview.ApplyLanguage(language);
            if (_loadedTabs.Contains(1)) Sensors.ApplyLanguage();
            if (_loadedTabs.Contains(2)) SystemOverview.ApplyLanguage();
            if (_loadedTabs.Contains(3)) AutoTest.ApplyLanguage(language);
            if (_loadedTabs.Contains(4)) { LocalizationManager.Apply(Stress, language); Stress.ApplyLanguage(language); }
            if (_loadedTabs.Contains(5)) { LocalizationManager.Apply(Renders, language); Renders.ApplyLanguage(language); }
            if (_loadedTabs.Contains(6)) { LocalizationManager.Apply(SettingsPane, language); SettingsPane.ApplyLanguage(language); SettingsPane.RestoreLanguageSelection(language); SettingsPane.RestoreThemeSelection(_settings.Theme); }
            if (_loadedTabs.Contains(7)) AboutPane.ApplyLanguage(language);
            SidebarOverview.Content = LocalizationManager.Get(LocalizationKeys.NavOverview, language);
            SidebarSensors.Content = LocalizationManager.Get(LocalizationKeys.NavSensors, language);
            SidebarSystem.Content = LocalizationManager.Get(LocalizationKeys.NavSystem, language);
            SidebarAutoTest.Content = LocalizationManager.Get(LocalizationKeys.NavAutoTest, language);
            SidebarStress.Content = LocalizationManager.Get(LocalizationKeys.NavStress, language);
            SidebarRenders.Content = LocalizationManager.Get(LocalizationKeys.NavRenders, language);
            SidebarSettings.Content = LocalizationManager.Get(LocalizationKeys.NavSettings, language);
            SidebarAbout.Content = LocalizationManager.Get(LocalizationKeys.NavAbout, language);
            ConsentButton.Content = LocalizationManager.Get(LocalizationKeys.Disclaimer, language);
            SafetyInfoButton.Content = LocalizationManager.Get(LocalizationKeys.Safety, language);
            UpdateSidebarSelection();
            _osd?.ApplyLanguage(language);
        }
        private void UpdateSidebarSelection()
        {
            Button[] items = { SidebarOverview, SidebarSensors, SidebarSystem, SidebarAutoTest, SidebarStress, SidebarRenders, SidebarSettings, SidebarAbout };
            for (int i = 0; i < items.Length; i++)
            {
                Button item = items[i];
                item.Background = Brushes.Transparent;
                item.BorderBrush = Brushes.Transparent;
                item.BorderThickness = new Thickness(0);
                if (i == Tabs.SelectedIndex)
                {
                    item.SetResourceReference(Control.BackgroundProperty, "SelectedBg");
                    item.SetResourceReference(Control.BorderBrushProperty, "AccentBrush");
                    item.BorderThickness = new Thickness(3, 0, 0, 0);
                }
            }
        }
        private void EasyMode_Click(object sender, RoutedEventArgs e)
        {
            _settings.InterfaceMode = "Easy";
            _settings.Save();
            ApplyInterfaceMode();
        }
        private void PcMonitorLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
                // Ссылка остаётся видимой в подсказке, если браузер не удалось открыть.
            }
            e.Handled = true;
        }
        private void OpenDiscord_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo("https://discord.gg/sfx7XxrNJu") { UseShellExecute = true });
        }
        private void ProMode_Click(object sender, RoutedEventArgs e)
        {
            _settings.InterfaceMode = "Pro";
            _settings.Save();
            ApplyInterfaceMode();
        }
        private void ApplyInterfaceMode()
        {
            bool pro = string.Equals(_settings.InterfaceMode, "Pro", StringComparison.OrdinalIgnoreCase);
            EasyModeButton.FontWeight = pro ? FontWeights.Normal : FontWeights.SemiBold;
            ProModeButton.FontWeight = pro ? FontWeights.SemiBold : FontWeights.Normal;
            EasyModeButton.Opacity = pro ? 0.65 : 1.0;
            ProModeButton.Opacity = pro ? 1.0 : 0.65;
            EasyModeButton.SetResourceReference(Control.BackgroundProperty, pro ? "PanelAltBg" : "AccentBrush");
            ProModeButton.SetResourceReference(Control.BackgroundProperty, pro ? "AccentBrush" : "PanelAltBg");
            EasyModeButton.SetResourceReference(Control.BorderBrushProperty, pro ? "BorderBrush" : "AccentBrush");
            ProModeButton.SetResourceReference(Control.BorderBrushProperty, pro ? "AccentBrush" : "BorderBrush");
            EasyModeButton.BorderThickness = new Thickness(1);
            ProModeButton.BorderThickness = new Thickness(1);

            SensorsTab.Visibility = pro ? Visibility.Visible : Visibility.Collapsed;
            SystemTab.Visibility = Visibility.Visible;
            ApplyTestingAccess();
            if (!pro && Tabs.SelectedItem != OverviewTab && Tabs.SelectedItem != SettingsTab && Tabs.SelectedItem != AboutTab)
                Tabs.SelectedIndex = 0;

            Tabs.BeginAnimation(OpacityProperty, new DoubleAnimation(0.72, 1.0, TimeSpan.FromMilliseconds(180)));
        }
        private void ApplyTestingAccess()
        {
            bool allowed = _settings.StressTestsAccepted == true;
            Visibility visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
            ConsentButton.Visibility = allowed ? Visibility.Collapsed : Visibility.Visible;
            SafetyInfoButton.Visibility = allowed ? Visibility.Visible : Visibility.Collapsed;
            AutoTestTab.Visibility = visibility;
            StressTab.Visibility = visibility;
            RendersTab.Visibility = visibility;
            SidebarAutoTest.Visibility = visibility;
            SidebarStress.Visibility = visibility;
            SidebarRenders.Visibility = visibility;

            if (!allowed && (Tabs.SelectedItem == AutoTestTab || Tabs.SelectedItem == StressTab || Tabs.SelectedItem == RendersTab))
                Tabs.SelectedIndex = 0;
        }
        private void StopAllTests()
        {
            AutoTest.StopFromHotKey();
            Stress.StopFromHotKey();
            Renders.Stop();
            SetStatus(LocalizationManager.Get(LocalizationKeys.TestsStopped), Theme.Good);
        }
        private void RestoreUiState()
        {
            TopMostButton.IsChecked = _settings.TopMost;
            Topmost = _settings.TopMost;

            if (_settings.LogToCsv) StartLogging();

            Tabs.SelectedIndex = 0;
        }
}
