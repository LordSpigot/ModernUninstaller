using System.Windows;
using ModernUninstaller.Models;
using ModernUninstaller.Services;
using ModernUninstaller.Services.Ml;

namespace ModernUninstaller.Dialogs;

/// <summary>Führt Wiederherstellungspunkt → Original-Uninstaller → Reste-Scan → Auswahl → Löschen aus.</summary>
public partial class UninstallFlowDialog : DialogWindow
{
    readonly IReadOnlyList<ProgramItem> _items;
    readonly bool _restorePoint, _autoLeftovers, _skipOriginal;
    readonly CancellationTokenSource _cts = new();
    TaskCompletionSource<bool>? _decision;
    int _removedTotal, _failedTotal, _rebootTotal;
    readonly List<string> _errors = new();
    int _done;

    public UninstallFlowDialog(IReadOnlyList<ProgramItem> items, bool restorePoint, bool autoLeftovers, bool skipOriginal = false)
    {
        InitializeComponent();
        _items = items; _restorePoint = restorePoint; _autoLeftovers = autoLeftovers; _skipOriginal = skipOriginal;
        BusyTitle.Text = items.Count == 1 ? items[0].Name : $"{items.Count} Programme";
        if (Settings.Current.QuickUninstall)
            BusyHint.Text = "Der Dialog-Assistent bedient den Original-Uninstaller und fragt nach, wenn er unsicher ist.";
        Loaded += async (_, _) => await RunAsync();
        Closed += (_, _) => _cts.Cancel();
    }

    void Step(string text) => Dispatcher.Invoke(() => BusyStep.Text = text);

    async Task RunAsync()
    {
        var ct = _cts.Token;
        try
        {
            if (_restorePoint)
            {
                Step("Erstelle Wiederherstellungspunkt …");
                var (ok, msg) = await UninstallEngine.CreateRestorePointAsync(_items.Count == 1 ? _items[0].Name : "Mehrere Programme");
                if (!ok)
                {
                    var cont = MessageBox.Show(this, "Der Wiederherstellungspunkt konnte nicht erstellt werden (Administratorrechte und Systemschutz nötig).\n\n" + Trim(msg) + "\n\nTrotzdem fortfahren?",
                        "Wiederherstellungspunkt", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (cont != MessageBoxResult.Yes) { CloseDialog(false); return; }
                }
            }

            // Office Click-to-Run: Werden alle Sprachen eines Produkts gewählt, verschwindet Office komplett.
            var whole = await Task.Run(() => OfficeC2R.ProductsRemovedCompletely(_items));
            if (whole.Count > 0 && !_skipOriginal)
            {
                var ans = MessageBox.Show(this, "Es sind alle installierten Sprachen von Microsoft 365 / Office gewählt (" + string.Join(", ", whole) + ").\n\n" +
                    "Damit werden Word, Excel, Outlook usw. vollständig entfernt.\n\nFortfahren?", "Microsoft 365", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (ans != MessageBoxResult.Yes) { CloseDialog(false); return; }
            }
            bool previousWasOffice = false;

            foreach (var item in _items)
            {
                ct.ThrowIfCancellationRequested();
                Bar.Value = _done * 100.0 / _items.Count;
                BusyTitle.Text = item.Name;
                string result = "Deinstalliert";

                await StopRunningAsync(item);
                var isOffice = OfficeC2R.IsC2R(item);
                if (!_skipOriginal)
                {
                    // Click-to-Run nimmt erst wieder Aufträge an, wenn der vorige vollständig abgeschlossen ist.
                    if (isOffice && previousWasOffice) { Step("Kurze Pause für Office …"); await Task.Delay(5000, ct); }
                    previousWasOffice = isOffice;
                    BusyHint.Text = isOffice
                        ? "Office arbeitet im Hintergrund, dafür erscheint kein Fenster. Bitte die Administrator-Abfrage (UAC) bestätigen."
                        : Settings.Current.QuickUninstall
                            ? "Der Dialog-Assistent bedient den Original-Uninstaller und fragt nach, wenn er unsicher ist."
                            : "Bitte die Dialoge des Original-Uninstallers beantworten. Sein Fenster wird nach vorne geholt.";
                    Step(isOffice ? "Office entfernt das Sprachpaket im Hintergrund …" : "Original-Deinstallation läuft …");
                    var outcome = await UninstallEngine.RunOriginalAsync(item, Settings.Current.QuickUninstall, Settings.Current.QuickUninstall ? new AssistPrompt(AskUser) : null, new Progress<string>(Step), ct);
                    if (isOffice && outcome.Started && !outcome.KeyGone && !ct.IsCancellationRequested)
                    {
                        var fb = MessageBox.Show(this, outcome.Message + "\n\nSoll stattdessen der Office-eigene Deinstallationsdialog geöffnet werden?",
                            item.Name, MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (fb == MessageBoxResult.Yes)
                        {
                            Step("Office-Deinstallationsdialog wird geöffnet …");
                            outcome = await UninstallEngine.RunOfficeAsync(item, silent: false, new Progress<string>(Step), ct);
                        }
                    }
                    if (!outcome.Started)
                    {
                        MessageBox.Show(this, "Die Deinstallation konnte nicht gestartet werden:\n" + outcome.Message + "\n\nProtokoll: " + UninstallLog.FilePath, item.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
                        History.Add(new HistoryEntry(DateTime.Now, item.Name, item.Version, item.Publisher, "Fehlgeschlagen: " + outcome.Message, 0));
                        _done++;
                        continue;
                    }
                    if (!outcome.KeyGone)
                    {
                        var ans = MessageBox.Show(this, "Der Eintrag des Programms ist noch vorhanden. Wurde die Deinstallation abgebrochen oder ist sie noch nicht fertig?" +
                            (outcome.Message.Length > 0 ? "\n" + outcome.Message : "") +
                            "\n\nJa = fortfahren und nach Resten suchen\nNein = dieses Programm auslassen\n\nProtokoll: " + UninstallLog.FilePath,
                            item.Name, MessageBoxButton.YesNo, MessageBoxImage.Question);
                        if (ans != MessageBoxResult.Yes)
                        {
                            History.Add(new HistoryEntry(DateTime.Now, item.Name, item.Version, item.Publisher, "Abgebrochen", 0));
                            _done++;
                            continue;
                        }
                    }
                }

                // Office-Sprachpakete teilen sich alle Dateien. Solange noch ein Office-Eintrag existiert, gibt es keine eigenen Reste.
                if (isOffice && await Task.Run(() => OfficeC2R.OthersRemain(item)))
                {
                    History.Add(new HistoryEntry(DateTime.Now, item.Name, item.Version, item.Publisher, result, 0));
                    _done++;
                    continue;
                }

                Step("Suche nach Resten (Dateien, Registry, Aufgaben, Dienste, DLLs) …");
                var leftovers = await LeftoverService.ScanAsync(item, new Progress<string>(s => Step("Suche nach Resten: " + s)));
                int removed = 0;
                if (leftovers.Count > 0)
                {
                    List<Leftover> selected;
                    if (_autoLeftovers) selected = leftovers.Where(l => !l.IsPersonalData).ToList();
                    else selected = await AskAsync(item, leftovers);
                    if (selected.Count > 0)
                    {
                        await StopRunningAsync(item);
                        Step("Lösche Reste …");
                        var res = await LeftoverService.CleanAsync(selected, Settings.Current.LeftoversToRecycleBin);
                        _removedTotal += res.Removed; _failedTotal += res.Failed; _rebootTotal += res.ScheduledForReboot; _errors.AddRange(res.Errors);
                        removed = res.Removed;
                    }
                }
                History.Add(new HistoryEntry(DateTime.Now, item.Name, item.Version, item.Publisher, result, removed));
                if (removed > 0 || leftovers.Count == 0) MonitorStore.DeleteFor(item);
                _done++;
            }

            ShowDone();
        }
        catch (OperationCanceledException) { CloseDialog(false); }
    }

    /// <summary>Fragt, ob noch laufende Prozesse des Programms beendet werden sollen, und beendet sie bei Ja.</summary>
    async Task StopRunningAsync(ProgramItem item)
    {
        if (item.InstallLocation.Length == 0) return;
        var running = await Task.Run(() => UninstallEngine.FindRunning(item.InstallLocation));
        if (running.Count == 0) return;
        var names = string.Join(", ", running.Select(p => p.ProcessName).Distinct());
        var ans = MessageBox.Show(this, $"Von „{item.Name}“ laufen noch Prozesse: {names}.\n\nBeenden, damit sich das Programm restlos entfernen lässt?",
            item.Name, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ans == MessageBoxResult.Yes) await Task.Run(() => UninstallEngine.KillAll(running));
        else foreach (var p in running) p.Dispose();
    }

    /// <summary>Rückfrage des Dialog-Assistenten (wird aus dem Hintergrund-Thread aufgerufen und blockiert ihn).</summary>
    AssistAnswer AskUser(DialogSnapshot window, AssistDecision decision) =>
        Dispatcher.Invoke(() =>
        {
            var dlg = new AssistDialog(window, decision) { Owner = this };
            dlg.ShowDialog();
            return dlg.Answer;
        });

    static string Trim(string s) => s.Length > 400 ? s[..400] + "…" : s;

    async Task<List<Leftover>> AskAsync(ProgramItem item, List<Leftover> leftovers)
    {
        BusyPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        ResultTitle.Text = $"Reste von {item.Name}";
        ResultHint.Text = $"{leftovers.Count} Elemente gefunden. Bitte prüfen, bevor etwas gelöscht wird.";
        ResultList.ItemsSource = leftovers.OrderBy(l => l.Category).ThenBy(l => l.Path).ToList();
        AllCheck.IsChecked = true;
        _decision = new TaskCompletionSource<bool>();
        var delete = await _decision.Task;
        ResultPanel.Visibility = Visibility.Collapsed;
        BusyPanel.Visibility = Visibility.Visible;
        return delete ? leftovers.Where(l => l.IsChecked).ToList() : new List<Leftover>();
    }

    void ShowDone()
    {
        BusyPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Visible;
        DoneTitle.Text = "Fertig";
        var text = $"{_items.Count} Eintrag/Einträge bearbeitet, {_removedTotal} Reste entfernt";
        if (_failedTotal > 0) text += $", {_failedTotal} konnten nicht gelöscht werden";
        text += ".";
        if (_rebootTotal > 0) text += $"\n{_rebootTotal} Elemente werden beim nächsten Neustart gelöscht. Bitte neu starten, damit nichts zurückbleibt.";
        if (_errors.Count > 0) text += "\n\nNicht gelöscht:\n" + string.Join("\n", _errors.Take(5).Select(e => e.Length > 110 ? e[..110] + "…" : e)) + (_errors.Count > 5 ? $"\n… und {_errors.Count - 5} weitere" : "");
        DoneText.Text = text;
    }

    void AllCheck_Click(object sender, RoutedEventArgs e)
    {
        var on = AllCheck.IsChecked == true;
        if (ResultList.ItemsSource is IEnumerable<Leftover> l) foreach (var i in l) i.IsChecked = on;
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ResultList.ItemsSource is IEnumerable<Leftover> l && l.Any(i => i.IsChecked && i.IsPersonalData) &&
            MessageBox.Show(this, "Persönliche Dateien sind markiert. Wirklich löschen?", "Achtung", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _decision?.TrySetResult(true);
    }

    void Skip_Click(object sender, RoutedEventArgs e) => _decision?.TrySetResult(false);
    void CancelRun_Click(object sender, RoutedEventArgs e) { _cts.Cancel(); _decision?.TrySetResult(false); }
    void Done_Click(object sender, RoutedEventArgs e) => CloseDialog(true);
}







