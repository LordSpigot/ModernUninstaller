using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using Microsoft.Win32;
using ModernUninstaller.Models;

namespace ModernUninstaller.Services;

public sealed record UninstallOutcome(bool Started, bool KeyGone, string Message);

public static class UninstallEngine
{
    static readonly string[] ConfirmButtons =
    {
        "Ja", "Yes", "OK", "Weiter", "Next", "Deinstallieren", "Uninstall", "Entfernen", "Remove",
        "Fertig", "Finish", "Schließen", "Close", "Beenden",
    };

    /// <summary>Wiederherstellungspunkt anlegen (braucht Administratorrechte und aktivierten Systemschutz).</summary>
    public static async Task<(bool ok, string message)> CreateRestorePointAsync(string description)
    {
        var d = description.Replace("'", "''");
        var r = await PowerShellRunner.RunAsync($"Checkpoint-Computer -Description 'ModernUninstaller: {d}' -RestorePointType APPLICATION_UNINSTALL -ErrorAction Stop", 90_000);
        return r.ExitCode == 0 ? (true, "") : (false, (r.Error.Length > 0 ? r.Error : r.Output).Trim());
    }

    public static async Task<UninstallOutcome> RunOriginalAsync(ProgramItem p, bool quick, IProgress<string>? log, CancellationToken ct)
    {
        var cmd = !string.IsNullOrEmpty(p.QuietUninstallString) && quick ? p.QuietUninstallString : p.UninstallString;
        cmd = FixMsi(cmd);
        var (file, args) = Split(cmd);
        if (string.IsNullOrEmpty(file)) return new UninstallOutcome(false, false, "Kein Deinstallationsbefehl hinterlegt.");

        log?.Report("Starte Original-Deinstallation …");
        Process? proc;
        try
        {
            proc = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            return new UninstallOutcome(false, false, ex.Message);
        }
        if (proc == null) return new UninstallOutcome(false, false, "Prozess konnte nicht gestartet werden.");

        using (proc)
        {
            // Viele Uninstaller kopieren sich nach %TEMP% und beenden den Starter sofort.
            // Deshalb: auf den Prozessbaum warten und danach kurz prüfen, ob der Eintrag verschwindet.
            var confirmer = quick ? Task.Run(() => AutoConfirmLoop(proc.Id, ct), ct) : Task.CompletedTask;
            try
            {
                await proc.WaitForExitAsync(ct);
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    if (KeyGone(p)) break;
                    await Task.Delay(500, ct);
                }
            }
            catch (OperationCanceledException) { }
            await Task.WhenAny(confirmer, Task.Delay(200));
        }
        return new UninstallOutcome(true, KeyGone(p), "");
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

    static void AutoConfirmLoop(int rootPid, CancellationToken ct)
    {
        var lastActivity = DateTime.UtcNow;
        while (!ct.IsCancellationRequested && DateTime.UtcNow - lastActivity < TimeSpan.FromSeconds(60))
        {
            try
            {
                var tree = Native.ProcessTree(rootPid);
                var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
                foreach (AutomationElement w in windows)
                {
                    if (!tree.Contains(w.Current.ProcessId)) continue;
                    var buttons = w.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                    foreach (AutomationElement b in buttons)
                    {
                        var name = Regex.Replace(b.Current.Name ?? "", "&|\\.\\.\\.", "").Trim();
                        if (!b.Current.IsEnabled || !ConfirmButtons.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                        if (b.TryGetCurrentPattern(InvokePattern.Pattern, out var pat)) { ((InvokePattern)pat).Invoke(); lastActivity = DateTime.UtcNow; break; }
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

