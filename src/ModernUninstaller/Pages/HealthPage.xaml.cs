using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ModernUninstaller.Models;
using ModernUninstaller.Services;

namespace ModernUninstaller.Pages;

public sealed class HealthCheck : INotifyPropertyChanged
{
    bool _checked = true;
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Sub { get; init; }
    public bool IsChecked { get => _checked; set { _checked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class HealthPage : UserControl, IPage
{
    readonly Func<IReadOnlyList<ProgramItem>> _programs;
    readonly Func<bool> _isAdmin;
    readonly Func<IReadOnlyList<string>, bool> _restartElevated;
    List<Leftover> _findings = new();
    bool _showingResults;

    public string SearchHint => "Software-Check";

    public event Action? ProgramsChanged;

    public HealthPage(Func<IReadOnlyList<ProgramItem>> programs, Func<bool> isAdmin,
                      Func<IReadOnlyList<string>, bool> restartElevated)
    {
        InitializeComponent();
        _programs = programs;
        _isAdmin = isAdmin;
        _restartElevated = restartElevated;
        Checks.ItemsSource = new List<HealthCheck>
        {
            new() { Key = HealthService.BrokenEntries, Title = "Defekte Uninstall-Einträge", Sub = "Programme, deren Uninstaller und Ordner nicht mehr existieren" },
            new() { Key = HealthService.InstallerFiles, Title = "Installationsdateien", Sub = "Setup- und MSI-Dateien im Downloads-Ordner" },
            new() { Key = HealthService.TempFiles, Title = "Temporäre Dateien", Sub = "Dateien im Temp-Ordner, die seit über 3 Tagen unverändert sind" },
        };
    }

    public Task LoadAsync() => Task.CompletedTask;
    public void Search(string text) { }

    async void Action_Click(object sender, RoutedEventArgs e)
    {
        ActionBtn.IsEnabled = false;
        try
        {
            if (!_showingResults) await ScanAsync();
            else await CleanAsync();
        }
        finally { ActionBtn.IsEnabled = !_showingResults || _findings.Count > 0; }
    }

    /// <summary>Nach dem einen UAC-Prompt die zuvor gewählten Checks automatisch starten.</summary>
    public async Task ScanAfterElevationAsync(IReadOnlyCollection<string> keys)
    {
        foreach (var c in (List<HealthCheck>)Checks.ItemsSource)
            c.IsChecked = keys.Contains(c.Key);
        ActionBtn.IsEnabled = false;
        try { await ScanAsync(); }
        finally { ActionBtn.IsEnabled = !_showingResults || _findings.Count > 0; }
    }

    async Task ScanAsync()
    {
        var chosen = ((List<HealthCheck>)Checks.ItemsSource).Where(c => c.IsChecked).ToList();
        if (chosen.Count == 0) return;
        if (!_isAdmin())
        {
            // Einmalige Erhöhung vor dem kompletten Software-Check. Die Auswahl
            // übergeben, damit nach dem Neustart keine zweite Interaktion nötig ist.
            StatusText.Text = "Administratorfreigabe erforderlich …";
            if (!_restartElevated(chosen.Select(c => c.Key).ToList()))
                StatusText.Text = "Administratorfreigabe abgebrochen";
            return;
        }
        StatusText.Text = "Scanne …";
        // Alle gewählten Prüfungen parallel; kein mehrfaches Warten auf unabhängige Scans.
        var programs = _programs();
        var scans = chosen.Select(c => HealthService.ScanAsync(c.Key, programs));
        var results = await Task.WhenAll(scans);
        _findings = results.SelectMany(result => result).ToList();
        ShowResults();
    }

    void ShowResults()
    {
        _showingResults = true;
        Checks.Visibility = Visibility.Collapsed;
        ResultGrid.Visibility = Visibility.Visible;
        ResultTitle.Text = _findings.Count == 0 ? "Alles in Ordnung – nichts gefunden." : $"{_findings.Count} Probleme gefunden";
        ResultList.ItemsSource = _findings;
        StatusText.Text = _findings.Count == 0 ? "Alles sauber" : $"{_findings.Count} Probleme";
        HintTitle.Text = "Bereinigen";
        HintText.Text = "Entfernt die angehakten Einträge. Dateien aus dem Downloads-Ordner gehen in den Papierkorb.";
        ActionBtn.Content = "Bereinigen";
        ActionBtn.IsEnabled = _findings.Count > 0;
        BackBtn.Visibility = Visibility.Visible;
    }

    async Task CleanAsync()
    {
        var sel = _findings.Where(f => f.IsChecked).ToList();
        if (sel.Count == 0) return;
        if (MessageBox.Show(Window.GetWindow(this), $"{sel.Count} Einträge entfernen?", "Bereinigen", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        StatusText.Text = "Bereinige …";
        var res = await LeftoverService.CleanAsync(sel, toRecycleBin: true);
        StatusText.Text = $"{res.Removed} entfernt";
        MessageBox.Show(Window.GetWindow(this),
            $"{res.Removed} Einträge entfernt" + (res.Failed > 0 ? $", {res.Failed} fehlgeschlagen" : "") + (res.ScheduledForReboot > 0 ? $", {res.ScheduledForReboot} beim nächsten Neustart" : "") + ".",
            "Software-Check", MessageBoxButton.OK, MessageBoxImage.Information);
        ProgramsChanged?.Invoke();
        Back_Click(this, new RoutedEventArgs());
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        _showingResults = false;
        Checks.Visibility = Visibility.Visible;
        ResultGrid.Visibility = Visibility.Collapsed;
        HintTitle.Text = "Jetzt scannen";
        HintText.Text = "Findet defekte Uninstall-Einträge, alte Installationsdateien und temporäre Dateien. Es wird nichts gelöscht, bevor Sie die Liste bestätigt haben.";
        ActionBtn.Content = "Scannen";
        ActionBtn.IsEnabled = true;
        BackBtn.Visibility = Visibility.Collapsed;
        if (StatusText.Text is "Scanne …" or "Bereinige …") StatusText.Text = "Nie gescannt";
    }
}
