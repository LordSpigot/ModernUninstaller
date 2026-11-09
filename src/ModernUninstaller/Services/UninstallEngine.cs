using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Microsoft.Win32;
using ModernUninstaller.Models;
using ModernUninstaller.Services.Ml;

namespace ModernUninstaller.Services;

public sealed record UninstallOutcome(bool Started, bool KeyGone, string Message);

public enum AssistChoice { Perform, NothingToClick, Manual, StopAssistant }

/// <summary>Antwort des Benutzers auf eine Rückfrage des Dialog-Assistenten.</summary>
public sealed record AssistAnswer(AssistChoice Choice, int Index = -1);

/// <summary>Wird aus einem Hintergrund-Thread aufgerufen und blockiert, bis der Benutzer geantwortet hat.</summary>
public delegate AssistAnswer AssistPrompt(DialogSnapshot window, AssistDecision decision);

public static class UninstallEngine
{
    /// <summary>Wiederherstellungspunkt anlegen (braucht Administratorrechte und aktivierten Systemschutz).</summary>
    public static async Task<(bool ok, string message)> CreateRestorePointAsync(string description)
    {
        var d = description.Replace("'", "''");
        var r = await PowerShellRunner.RunAsync($"Checkpoint-Computer -Description 'ModernUninstaller: {d}' -RestorePointType APPLICATION_UNINSTALL -ErrorAction Stop", 90_000);
        return r.ExitCode == 0 ? (true, "") : (false, (r.Error.Length > 0 ? r.Error : r.Output).Trim());
    }

    /// <param name="assist">Dialog-Assistent: bedient die Fenster des Original-Uninstallers.</param>
    /// <param name="prompt">Rückfrage bei Unsicherheit; null = unsichere Fenster dem Benutzer überlassen.</param>
    public static async Task<UninstallOutcome> RunOriginalAsync(ProgramItem p, bool assist, AssistPrompt? prompt, IProgress<string>? log, CancellationToken ct)
    {
        if (OfficeC2R.IsC2R(p)) return await RunOfficeAsync(p, silent: true, log, ct);

        var cmd = !string.IsNullOrEmpty(p.QuietUninstallString) && assist ? p.QuietUninstallString : p.UninstallString;
        cmd = FixMsi(cmd);
        return await RunCommandAsync(p, cmd, runAs: false, assist, prompt, log, ct);
    }

    /// <summary>
    /// Startet einen Deinstallationsbefehl, holt dessen Fenster nach vorne, zeigt den Zustand an und wartet,
    /// bis der ganze Prozessbaum fertig ist (viele Uninstaller kopieren sich nach %TEMP% und beenden den Starter sofort).
    /// </summary>
    static async Task<UninstallOutcome> RunCommandAsync(ProgramItem p, string cmd, bool runAs, bool assist, AssistPrompt? prompt, IProgress<string>? log, CancellationToken ct)
    {
        var (file, args) = Split(cmd);
        if (string.IsNullOrEmpty(file)) return new UninstallOutcome(false, false, "Kein Deinstallationsbefehl hinterlegt.");

        UninstallLog.Write($"[{p.Name}] Start: \"{file}\" {args}" + (runAs ? "  (als Administrator)" : ""));
        log?.Report("Starte Original-Deinstallation …");
        Process? proc;
        try
        {
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);   // gestartetes Programm darf sein Fenster in den Vordergrund bringen
            var psi = new ProcessStartInfo(file, args) { UseShellExecute = true };
            if (runAs && !Native.IsElevated()) psi.Verb = "runas";
            proc = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            UninstallLog.Write($"[{p.Name}] Administratorfreigabe abgelehnt.");
            return new UninstallOutcome(false, false, "Die Administratorfreigabe (UAC) wurde abgelehnt.");
        }
        catch (Exception ex)
        {
            UninstallLog.Write($"[{p.Name}] Startfehler: {ex.Message}");
            return new UninstallOutcome(false, false, ex.Message + "\n\nBefehl: " + cmd);
        }
        if (proc == null) return new UninstallOutcome(false, false, "Prozess konnte nicht gestartet werden.");

        int? exitCode = null;
        using (proc)
        {
            var rootPid = proc.Id;
            UninstallLog.Write($"[{p.Name}] PID {rootPid}");
            using var stopAssist = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var assistant = assist ? Task.Run(() => AssistLoop(rootPid, prompt, log, stopAssist.Token), stopAssist.Token) : Task.CompletedTask;
            var fronted = new HashSet<IntPtr>();
            bool sawWindow = false;
            try
            {
                var started = DateTime.UtcNow;
                DateTime? goneAt = null;
                while (!ct.IsCancellationRequested && DateTime.UtcNow - started < TimeSpan.FromMinutes(30))
                {
                    var live = Native.LiveTree(rootPid);
                    if (Exited(proc) && exitCode == null)
                    {
                        try { exitCode = proc.ExitCode; } catch { exitCode = -1; }
                        UninstallLog.Write($"[{p.Name}] Starter beendet, Code {exitCode}, weitere Prozesse: {live.Count}");
                    }
                    if (live.Count == 0) break;

                    // Neue Fenster des Uninstallers nach vorne holen, damit sie nicht hinter unserem Dialog verschwinden.
                    var wins = Native.VisibleWindowsOf(live);
                    foreach (var h in wins)
                        if (fronted.Add(h)) { Native.BringToFront(h); sawWindow = true; }

                    var el = DateTime.UtcNow - started;
                    var time = $"{(int)el.TotalMinutes}:{el.Seconds:00}";
                    if (wins.Count > 0) log?.Report($"Original-Uninstaller läuft ({time}) – bitte dessen Fenster beantworten.");
                    else if (el > TimeSpan.FromSeconds(10))
                        log?.Report($"Uninstaller läuft ({time}), zeigt aber kein Fenster. Prüfen Sie die Taskleiste auf eine Administrator-Abfrage (UAC) oder ein verstecktes Fenster.");

                    if (KeyGone(p)) { goneAt ??= DateTime.UtcNow; if (DateTime.UtcNow - goneAt > TimeSpan.FromSeconds(60)) break; }
                    await Task.Delay(700, ct);
                }
                if (exitCode == null && Exited(proc)) { try { exitCode = proc.ExitCode; } catch { } }
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    if (KeyGone(p)) break;
                    await Task.Delay(500, ct);
                }
            }
            catch (OperationCanceledException) { }
            stopAssist.Cancel();
            await Task.WhenAny(assistant, Task.Delay(1500));
            UninstallLog.Write($"[{p.Name}] Ende: Fenster gesehen={sawWindow}, Code={exitCode?.ToString() ?? "?"}, Eintrag entfernt={KeyGone(p)}");
        }
        var gone = KeyGone(p);
        var msg = gone ? "" : exitCode is int c && c != 0 ? $"Der Uninstaller hat mit Code {c} beendet." : "";
        return new UninstallOutcome(true, gone, msg);
    }

    /// <summary>
    /// Office Click-to-Run. <paramref name="silent"/>: ohne Fenster (DisplayLevel=False), sonst mit Office-eigenem Dialog.
    /// Office braucht Administratorrechte; ohne sie bricht der stille Modus lautlos ab, daher wird immer per UAC gestartet.
    /// </summary>
    public static async Task<UninstallOutcome> RunOfficeAsync(ProgramItem p, bool silent, IProgress<string>? log, CancellationToken ct)
    {
        var cmd = silent ? OfficeC2R.SilentCommand(p.UninstallString) : p.UninstallString;
        if (!silent) return await RunCommandAsync(p, cmd, runAs: true, assist: false, prompt: null, log, ct);

        var (file, args) = Split(cmd);
        UninstallLog.Write($"[{p.Name}] Office still: \"{file}\" {args}");
        log?.Report($"Office entfernt „{p.Name}“ im Hintergrund …");
        Process? proc;
        try
        {
            var psi = new ProcessStartInfo(file, args) { UseShellExecute = true };
            if (!Native.IsElevated()) psi.Verb = "runas";
            proc = Process.Start(psi);
            if (proc == null) return new UninstallOutcome(false, false, "OfficeClickToRun.exe konnte nicht gestartet werden.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new UninstallOutcome(false, false, "Die Administratorfreigabe (UAC) wurde abgelehnt. Office lässt sich nur mit Administratorrechten entfernen.");
        }
        catch (Exception ex) { return new UninstallOutcome(false, false, ex.Message); }

        int? exitCode = null;
        var started = DateTime.UtcNow;
        DateTime lastBusy = DateTime.UtcNow;
        using (proc)
        {
            try
            {
                while (!ct.IsCancellationRequested && DateTime.UtcNow - started < TimeSpan.FromMinutes(20))
                {
                    if (KeyGone(p)) break;
                    if (Exited(proc) && exitCode == null)
                    {
                        try { exitCode = proc.ExitCode; } catch { exitCode = -1; }
                        UninstallLog.Write($"[{p.Name}] OfficeClickToRun beendet, Code {exitCode}");
                    }
                    if (!Exited(proc) || OfficeBusy()) lastBusy = DateTime.UtcNow;
                    // Arbeitet Office seit 90 s nicht mehr und der Eintrag ist noch da, ist es fehlgeschlagen.
                    if (DateTime.UtcNow - lastBusy > TimeSpan.FromSeconds(90)) break;
                    var el = DateTime.UtcNow - started;
                    log?.Report($"Office entfernt „{p.Name}“ im Hintergrund … {(int)el.TotalMinutes}:{el.Seconds:00}");
                    await Task.Delay(1000, ct);
                }
            }
            catch (OperationCanceledException) { }
        }
        var gone = KeyGone(p);
        UninstallLog.Write($"[{p.Name}] Office still Ende: Code={exitCode?.ToString() ?? "?"}, Eintrag entfernt={gone}");
        return new UninstallOutcome(true, gone, gone ? "" : $"Office hat das Sprachpaket im Hintergrund nicht entfernt (Code {exitCode?.ToString() ?? "unbekannt"}).");
    }

    static bool Exited(Process proc)
    {
        try { return proc.HasExited; } catch { return false; }   // z. B. Zugriff verweigert bei erhöhtem Prozess
    }

    /// <summary>Läuft gerade ein Office-Auftrag? (Der Click-to-Run-Dienst selbst läuft immer, daher zählt erst ein zweiter Prozess.)</summary>
    static bool OfficeBusy()
    {
        try
        {
            if (Process.GetProcessesByName("OfficeC2RClient").Length > 0) return true;
            return Process.GetProcessesByName("OfficeClickToRun").Length > 1;
        }
        catch { return false; }
    }

    /// <summary>Prozesse, deren EXE im Installationsordner liegt (laufende Programme blockieren sonst Dateien).</summary>
    public static List<Process> FindRunning(string location)
    {
        var list = new List<Process>();
        if (location.Length < 10) return list;
        var prefix = location.TrimEnd('\\') + "\\";
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path != null && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && p.Id != Environment.ProcessId) { list.Add(p); continue; }
            }
            catch { /* Zugriff verweigert (z. B. erhöhter Prozess) */ }
            p.Dispose();
        }
        return list;
    }

    public static void KillAll(IEnumerable<Process> processes)
    {
        foreach (var p in processes)
        {
            try { p.Kill(true); p.WaitForExit(4000); } catch { }
            p.Dispose();
        }
    }

    /// <summary>
    /// Bedient die Fenster des Original-Uninstallers mit dem lernenden Dialog-Assistenten.
    /// Sichere Fälle werden geklickt, unsichere dem Benutzer vorgelegt; seine Antwort wird gelernt.
    /// </summary>
    static void AssistLoop(int rootPid, AssistPrompt? prompt, IProgress<string>? log, CancellationToken ct)
    {
        var model = DialogAssistant.Model;
        var actedOn = new Dictionary<string, int>();         // Signatur -> Anzahl Aktionen (Schleifenschutz)
        var waitingSince = new Dictionary<string, DateTime>();
        var leaveAlone = new HashSet<string>();               // Fenster, die der Benutzer selbst bedient
        var lastActivity = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            var tree = Native.ProcessTree(rootPid);
            if (Native.LiveTree(rootPid).Count == 0 && DateTime.UtcNow - lastActivity > TimeSpan.FromSeconds(60)) break;
            try
            {
                var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
                foreach (AutomationElement w in windows)
                {
                    if (ct.IsCancellationRequested) return;
                    if (!tree.Contains(w.Current.ProcessId)) continue;
                    var cap = DialogAutomation.Capture(w);
                    if (cap == null || cap.Snapshot.Controls.Count == 0) continue;
                    var snap = cap.Snapshot;
                    var sig = snap.Signature();
                    if (leaveAlone.Contains(sig)) continue;

                    var d = model.Decide(snap);
                    actedOn.TryGetValue(sig, out var n);
                    if (d.Action == AssistAction.Click && n >= 2) d = d with { Action = AssistAction.Ask, Reason = "Fenster ändert sich nicht" };
                    if (d.Action == AssistAction.Wait)
                    {
                        if (!waitingSince.TryGetValue(sig, out var since)) waitingSince[sig] = since = DateTime.UtcNow;
                        // Steht ein Fenster lange still, obwohl etwas anklickbar ist, lieber nachfragen.
                        if (DateTime.UtcNow - since > TimeSpan.FromSeconds(25) && d.Ranking.Any(r => r.Index >= 0 && r.P > 0.15))
                            d = d with { Action = AssistAction.Ask, Index = d.Ranking.First(r => r.Index >= 0).Index, Reason = "wartet schon lange" };
                        else { lastActivity = DateTime.UtcNow; continue; }
                    }

                    int index = d.Index;
                    bool learn = false;
                    if (d.Action == AssistAction.Ask)
                    {
                        if (prompt == null) { leaveAlone.Add(sig); continue; }
                        log?.Report("Assistent ist unsicher – Rückfrage …");
                        var answer = prompt(snap, d);
                        lastActivity = DateTime.UtcNow;
                        switch (answer.Choice)
                        {
                            case AssistChoice.StopAssistant: log?.Report("Assistent beendet – bitte selbst weiterklicken."); return;
                            case AssistChoice.Manual: leaveAlone.Add(sig); continue;
                            case AssistChoice.NothingToClick: DialogAssistant.Learn(snap, -1); model = DialogAssistant.Model; leaveAlone.Add(sig); continue;
                        }
                        index = answer.Index;
                        learn = true;
                    }
                    if (index < 0 || index >= cap.Elements.Count) continue;

                    var c = snap.Controls[index];
                    if (DialogAutomation.Perform(cap.Elements[index], c.Kind))
                    {
                        actedOn[sig] = n + 1;
                        lastActivity = DateTime.UtcNow;
                        log?.Report(d.Action == AssistAction.Click
                            ? $"Assistent: „{c.Label}“ ({d.Confidence:P0})"
                            : $"Assistent: „{c.Label}“ (von Ihnen gewählt, gelernt)");
                        if (learn) { DialogAssistant.Learn(snap, index); model = DialogAssistant.Model; }
                        Thread.Sleep(400);
                        break;   // Fensterliste neu einlesen
                    }
                }
            }
            catch { /* Fenster verschwindet gerade */ }
            Thread.Sleep(600);
        }
    }

    static bool KeyGone(ProgramItem p)
    {
        try
        {
            var parts = p.RegistryPath.Split('\\', 2);
            var hive = parts[0].StartsWith("HKCU") ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
            var view = parts[0].EndsWith("[32]") ? RegistryView.Registry32 : RegistryView.Registry64;
            using var bk = RegistryKey.OpenBaseKey(hive, view);
            using var k = bk.OpenSubKey(parts[1]);
            return k == null;
        }
        catch { return false; }
    }

    static string FixMsi(string cmd) =>
        Regex.IsMatch(cmd, @"msiexec(\.exe)?\s+/I\s*\{", RegexOptions.IgnoreCase)
            ? Regex.Replace(cmd, @"/I\s*\{", "/X{", RegexOptions.IgnoreCase)
            : cmd;

    static (string file, string args) Split(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        if (cmd.Length == 0) return ("", "");
        if (cmd[0] == '"')
        {
            var end = cmd.IndexOf('"', 1);
            return end < 0 ? (cmd.Trim('"'), "") : (cmd.Substring(1, end - 1), cmd[(end + 1)..].Trim());
        }
        var i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return i < 0 ? (cmd, "") : (cmd[..(i + 4)], cmd[(i + 4)..].Trim());
    }
}

