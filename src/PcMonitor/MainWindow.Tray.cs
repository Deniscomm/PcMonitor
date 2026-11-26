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
        private void SetupTray()
        {
            try
            {
                Drawing.Icon icon = LoadTrayIcon();
                var menu = new WinForms.ContextMenuStrip { ShowImageMargin = false };

                menu.Items.Add(LocalizationManager.Get(LocalizationKeys.TrayOpen, _settings.Language), null, (_, _) => RestoreFromTray());
                menu.Items.Add(new WinForms.ToolStripSeparator());
                menu.Items.Add(LocalizationManager.Get(LocalizationKeys.NavOverview, _settings.Language), null, (_, _) => ShowTab(0));
                menu.Items.Add(LocalizationManager.Get(LocalizationKeys.NavSensors, _settings.Language), null, (_, _) => ShowTab(1));
                menu.Items.Add(LocalizationManager.Get(LocalizationKeys.NavSystem, _settings.Language), null, (_, _) => ShowTab(2));
                menu.Items.Add(LocalizationManager.Get(LocalizationKeys.NavAutoTest, _settings.Language), null, (_, _) => ShowTab(3));
                menu.Items.Add(LocalizationManager.Get(LocalizationKeys.NavRenders, _settings.Language), null, (_, _) => ShowTab(5));
                menu.Items.Add(LocalizationManager.Get(LocalizationKeys.NavAbout, _settings.Language), null, (_, _) => ShowTab(7));

                _trayOsdItem = new WinForms.ToolStripMenuItem(LocalizationManager.Get(LocalizationKeys.TrayOsd, _settings.Language))
                {
                    CheckOnClick = true,
                    Checked = _settings.OsdEnabled,
                };
                _trayOsdItem.CheckedChanged += (_, _) =>
                {
                    _settings.OsdEnabled = _trayOsdItem.Checked;
                    _settings.Save();
                    ToggleOsd(_trayOsdItem.Checked);
                };
                menu.Items.Add(_trayOsdItem);

                menu.Items.Add(new WinForms.ToolStripSeparator());
                menu.Items.Add(LocalizationManager.Get(LocalizationKeys.TrayExit, _settings.Language), null, (_, _) =>
                {
                    Close();
                });

                _tray = new WinForms.NotifyIcon
                {
                    Icon = icon,
                    Text = LocalizationManager.Get(LocalizationKeys.TrayTitle, _settings.Language),
                    Visible = true,
                    ContextMenuStrip = menu,
                };

                _tray.DoubleClick += (_, _) => RestoreFromTray();
                _tray.BalloonTipClicked += (_, _) => RestoreFromTray();
            }
            catch (Exception ex)
            {
                SetStatus(LocalizationManager.Get(LocalizationKeys.TrayUnavailable, _settings.Language).Replace("{0}", ex.Message, StringComparison.Ordinal));
            }
        }
        private static Drawing.Icon LoadTrayIcon()
        {
            try
            {
                string? path = Environment.ProcessPath;
                if (path is not null)
                {
                    Drawing.Icon? extracted = Drawing.Icon.ExtractAssociatedIcon(path);
                    if (extracted is not null) return extracted;
                }
            }
            catch
            {
                // Ниже — запасной вариант со стандартным значком.
            }

            return Drawing.SystemIcons.Application;
        }
        private void HideToTray(string? hint = null)
        {
            Hide();
            ShowInTaskbar = false;

            if (!_settings.TrayNotifications || _tray is null || _trayHintShown) return;

            _trayHintShown = true;
            _tray.ShowBalloonTip(4000, "Мониторинг ПК",
                hint ?? LocalizationManager.Get(LocalizationKeys.TrayHint, _settings.Language),
                WinForms.ToolTipIcon.Info);
        }
        private void RestoreFromTray()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = WindowState.Normal;
            Activate();
            Topmost = _settings.TopMost;
        }
        private void ShowTab(int index)
        {
            RestoreFromTray();
            if (index >= 0 && index < Tabs.Items.Count) Tabs.SelectedIndex = index;
        }
        private void Sidebar_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string tag } && int.TryParse(tag, out int index))
                ShowTab(index);
        }
}
