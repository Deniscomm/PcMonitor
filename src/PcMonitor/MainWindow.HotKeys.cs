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
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            var source = PresentationSource.FromVisual(this) as HwndSource;
            source?.AddHook(WndProc);

            IntPtr handle = new WindowInteropHelper(this).Handle;
            RegisterHotKey(handle, HotKeyId, ModControl | ModAlt | ModNoRepeat, 0x4F); // Ctrl+Alt+O
            RegisterHotKey(handle, StopAllHotKeyId, ModControl | ModAlt | ModWin | ModNoRepeat, 0x53); // Ctrl+Win+Alt+S
        }
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotKey)
            {
                if (wParam.ToInt32() == HotKeyId) ToggleOsd(!_settings.OsdEnabled);
                else if (wParam.ToInt32() == StopAllHotKeyId) StopAllTests();
                handled = true;
            }

            return IntPtr.Zero;
        }
        private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
            }
        }
        private void ToggleFullscreen()
        {
            if (WindowStyle == WindowStyle.None)
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                WindowState = WindowState.Normal;
                Topmost = _settings.TopMost;
                SetStatus(LocalizationManager.Get(LocalizationKeys.FullscreenOff));
            }
            else
            {
                WindowStyle = WindowStyle.None;
                WindowState = WindowState.Maximized;
                Topmost = false;
                SetStatus(LocalizationManager.Get(LocalizationKeys.FullscreenOn));
            }
        }
        private void ToggleOsd_Click(object sender, RoutedEventArgs e) => ToggleOsd(OsdButton.IsChecked == true);
        private void ToggleOsd(bool show)
        {
            if (show)
            {
                EnsureOsd();
                _osd!.Show();
                _osd.Rebuild();
                SetStatus(LocalizationManager.Get(LocalizationKeys.OsdEnabled), Theme.Good);
            }
            else
            {
                _osd?.Hide();
                SetStatus(LocalizationManager.Get(LocalizationKeys.OsdHidden));
            }

            OsdButton.IsChecked = show;
            _settings.OsdEnabled = show;
            _settings.Save();
        }
        private void EnsureOsd()
        {
            if (_osd is not null) return;

            _osd = new OsdWindow();
            _osd.Attach(_context);
        }
}
