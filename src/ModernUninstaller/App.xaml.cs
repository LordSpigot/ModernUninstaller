using System.Windows;
using ModernUninstaller.Services;

namespace ModernUninstaller;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ev) =>
        {
            MessageBox.Show(ev.Exception.Message, "Unerwarteter Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            ev.Handled = true;
        };
        Settings.Load();
        ThemeService.Apply(Settings.Current.Theme);
        // Nur vordefinierte Software-Checks aus Befehlszeilenparametern akzeptieren.
        IReadOnlyCollection<string>? healthChecks = null;
        if (e.Args.Length == 2 && e.Args[0] == "--health-scan")
        {
            var allowed = new[] { HealthService.BrokenEntries, HealthService.InstallerFiles, HealthService.TempFiles };
            var selected = e.Args[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (selected.Length > 0 && selected.All(allowed.Contains)) healthChecks = selected;
        }
        new MainWindow(healthChecks).Show();
    }
}
