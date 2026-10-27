using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using ModernUninstaller.Models;
using ModernUninstaller.Services;

namespace ModernUninstaller.Dialogs;

public partial class MonitorDialog : DialogWindow
{
    readonly CancellationTokenSource _cts = new();
    MonitorLog? _pending;
    List<(Leftover row, LogEntry entry)> _rows = new();
    List<MonitorLog> _logs = new();

    public MonitorDialog()
    {
        InitializeComponent();
        Closed += (_, _) => _cts.Cancel();
        ShowIdle();
    }

    // ---------- Zustände ----------

    void ShowIdle()
    {
        IdlePanel.Visibility = Visibility.Visible;
        BusyPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Collapsed;

        var has = InstallMonitor.HasSnapshot;
        SnapshotCard.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        StartCard.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        if (has) SnapshotTitle.Text = $"Snapshot vom {InstallMonitor.SnapshotDate:dd.MM.yyyy HH:mm} ist bereit";

        _logs = MonitorStore.Load();
        LogList.ItemsSource = _logs.Select(l => new ToolRow
        {
            Title = l.ProgramName.Length > 0 ? $"{l.ProgramName} {l.Version}".Trim() : "(ohne Namen)",
            Sub = $"{l.Date:dd.MM.yyyy HH:mm} · {l.Items.Count} Elemente · {l.TotalFiles} neue Dateien",
            Tag = l,
        }).ToList();
        NoLogs.Visibility = _logs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeleteLogBtn.IsEnabled = _logs.Count > 0;
    }

    void ShowBusy(string title)
    {
        IdlePanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Collapsed;
        BusyPanel.Visibility = Visibility.Visible;
        BusyTitle.Text = title;
        BusyText.Text = "";
    }

    IProgress<string> BusyProgress => new Progress<string>(s => BusyText.Text = s);

    // ---------- Aktionen ----------

    async Task<bool> SnapshotAsync()
    {
        ShowBusy("Snapshot wird erstellt …");
        try
        {
            await InstallMonitor.TakeSnapshotAsync(BusyProgress, _cts.Token);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Der Snapshot konnte nicht erstellt werden:\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    async void SnapshotOnly_Click(object sender, RoutedEventArgs e)
    {
        if (await SnapshotAsync()) Status.Text = "Snapshot erstellt. Jetzt das Programm installieren.";
        ShowIdle();
    }

    async void RunInstaller_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Installer wählen", Filter = "Installer|*.exe;*.msi|Alle Dateien|*.*" };
        if (dlg.ShowDialog(this) != true) return;

        if (!await SnapshotAsync()) { ShowIdle(); return; }
        try
        {
            var psi = dlg.FileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo("msiexec.exe", $"/i \"{dlg.FileName}\"") { UseShellExecute = true }
                : new ProcessStartInfo(dlg.FileName) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(dlg.FileName) };
            Process.Start(psi);
            Status.Text = "Installer gestartet. Danach hier „Installation abschließen und vergleichen“ wählen.";
        }
        catch (Exception ex)
        {
            Status.Text = "Installer konnte nicht gestartet werden: " + ex.Message;
        }
        ShowIdle();
    }

    async void Compare_Click(object sender, RoutedEventArgs e)
    {
        ShowBusy("Vergleiche mit dem Snapshot …");
        try
        {
            _pending = await InstallMonitor.CompareAsync(BusyProgress, _cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Der Vergleich ist fehlgeschlagen:\n" + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowIdle();
            return;
        }
        ShowResult(_pending);
    }

    void ShowResult(MonitorLog log)
    {
        IdlePanel.Visibility = Visibility.Collapsed;
        BusyPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;

        NameBox.Text = log.ProgramName;
        _rows = log.Items
            .OrderBy(i => new Leftover { Kind = i.Kind }.Category).ThenBy(i => i.Path)
            .Select(i => (new Leftover { Kind = i.Kind, Path = i.Path, ValueName = i.ValueName }, i)).ToList();
        ResultList.ItemsSource = _rows.Select(r => r.row).ToList();
        Summary.Text = log.Items.Count == 0
            ? "Seit dem Snapshot wurden keine neuen Elemente erkannt."
            : $"{log.Items.Count} neue Elemente ({log.TotalFiles} Dateien). Nehmen Sie den Haken bei allem weg, was nicht zur Installation gehört, zum Beispiel Dateien anderer Programme, die zwischendurch liefen.";
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_pending == null) return;
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { Status.Text = "Bitte einen Namen eingeben."; return; }
        _pending.ProgramName = name;
        _pending.Items = _rows.Where(r => r.row.IsChecked).Select(r => r.entry).ToList();
        if (_pending.Items.Count == 0) { Status.Text = "Es ist nichts ausgewählt."; return; }

        MonitorStore.Save(_pending);
        InstallMonitor.DiscardSnapshot();
        Status.Text = $"Protokoll für „{name}“ gespeichert.";
        _pending = null;
        ShowIdle();
    }

    void DiscardResult_Click(object sender, RoutedEventArgs e)
    {
        InstallMonitor.DiscardSnapshot();
        _pending = null;
        Status.Text = "Verworfen.";
        ShowIdle();
    }

    void Discard_Click(object sender, RoutedEventArgs e)
    {
        InstallMonitor.DiscardSnapshot();
        Status.Text = "Snapshot verworfen.";
        ShowIdle();
    }

    void DeleteLog_Click(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is not ToolRow { Tag: MonitorLog log }) { Status.Text = "Bitte zuerst ein Protokoll auswählen."; return; }
        MonitorStore.Delete(log.Id);
        Status.Text = "Protokoll gelöscht.";
        ShowIdle();
    }

    void Close_Click(object sender, RoutedEventArgs e) => CloseDialog(true);
}
