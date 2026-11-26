using System.Windows;
using System.Windows.Threading;
using PcMonitor.Core.Diagnostics;

namespace PcMonitor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        StartupDiagnostics.Begin();
        StartupDiagnostics.Mark("app.startup.begin");
        base.OnStartup(e);
        StartupDiagnostics.Mark("wpf.application.initialized");
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var window = new MainWindow();
        StartupDiagnostics.Mark("mainwindow.constructed");
        MainWindow = window;
        window.Show();
        StartupDiagnostics.Mark("window.show.called");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            "Непредвиденная ошибка приложения:\n\n" + e.Exception.Message,
            "Мониторинг ПК",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
