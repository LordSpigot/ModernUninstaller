using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using ModernUninstaller.Models;

namespace ModernUninstaller.Services;

public sealed class LogEntry
{
    public LeftoverKind Kind { get; set; }
    public string Path { get; set; } = "";
    public string ValueName { get; set; } = "";
}

public sealed class MonitorLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProgramName { get; set; } = "";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    /// <summary>Uninstall-Schlüssel im Format von ProgramItem.RegistryPath; leer, wenn keiner erkannt wurde.</summary>
    public string UninstallKey { get; set; } = "";
    public DateTime Date { get; set; } = DateTime.Now;
    public int TotalFiles { get; set; }
    public List<LogEntry> Items { get; set; } = new();
}

/// <summary>Speichert und findet Installationsprotokolle.</summary>
public static class MonitorStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    static string LogsDir => Path.Combine(Settings.DataDir, "monitor", "logs");

    public static List<MonitorLog> Load()
    {
        var list = new List<MonitorLog>();
        try
        {
            if (!Directory.Exists(LogsDir)) return list;
            foreach (var f in Directory.EnumerateFiles(LogsDir, "*.json"))
            {
                try { var l = JsonSerializer.Deserialize<MonitorLog>(File.ReadAllText(f), Json); if (l != null) list.Add(l); } catch { }
            }
        }
        catch { }
        return list.OrderByDescending(l => l.Date).ToList();
    }

    public static void Save(MonitorLog log)
    {
        Directory.CreateDirectory(LogsDir);
        File.WriteAllText(Path.Combine(LogsDir, log.Id + ".json"), JsonSerializer.Serialize(log, Json));
    }

    public static void Delete(Guid id)
    {
        try { File.Delete(Path.Combine(LogsDir, id + ".json")); } catch { }
    }

    public static MonitorLog? Find(ProgramItem p, List<MonitorLog>? logs = null)
    {
        logs ??= Load();
        return logs.FirstOrDefault(l => l.UninstallKey.Length > 0 && string.Equals(l.UninstallKey, p.RegistryPath, StringComparison.OrdinalIgnoreCase))
            ?? logs.FirstOrDefault(l => l.UninstallKey.Length == 0 && string.Equals(l.ProgramName, p.Name, StringComparison.OrdinalIgnoreCase));
    }

    public static void MarkLogged(IEnumerable<ProgramItem> items)
    {
        var logs = Load();
        if (logs.Count == 0) return;
        foreach (var p in items.Where(i => i.Kind == ItemKind.Win32))
        {
            var l = Find(p, logs);
            if (l != null) { p.HasLog = true; p.LogItemCount = l.Items.Count; }
        }
    }

    public static void DeleteFor(ProgramItem p)
    {
        var l = Find(p);
        if (l != null) Delete(l.Id);
    }

    /// <summary>Protokollierte Elemente, die noch existieren, als Reste-Kandidaten.</summary>
    public static IEnumerable<Leftover> LeftoversFor(ProgramItem p)
    {
        var log = Find(p);
        if (log == null) yield break;
        foreach (var e in log.Items)
            if (InstallMonitor.Exists(e))
                yield return new Leftover { Kind = e.Kind, Path = e.Path, ValueName = e.ValueName };
    }
}

/// <summary>Snapshot vor der Installation, Vergleich danach.
/// Erfasst werden neue Dateien/Ordner in den üblichen Installationsorten sowie Registry-Schlüssel (2 Ebenen unter Software),
/// Uninstall-Einträge, Autostart-Werte, Dienste und geplante Aufgaben. Geänderte (nicht neue) Elemente erfasst er nicht.</summary>
public static class InstallMonitor
{
    static string Dir => Path.Combine(Settings.DataDir, "monitor");
    static string SnapshotPath => Path.Combine(Dir, "snapshot.gz");

    public static bool HasSnapshot => File.Exists(SnapshotPath);
    public static DateTime? SnapshotDate => HasSnapshot ? File.GetLastWriteTime(SnapshotPath) : null;

    public static void DiscardSnapshot()
    {
        try { File.Delete(SnapshotPath); } catch { }
    }

    public static Task TakeSnapshotAsync(IProgress<string>? progress, CancellationToken ct) => Task.Run(() =>
    {
        var set = Collect(progress, ct);
        Directory.CreateDirectory(Dir);
        var tmp = SnapshotPath + ".tmp";
        using (var fs = File.Create(tmp))
        using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
        using (var w = new StreamWriter(gz, new UTF8Encoding(false)))
            foreach (var l in set) w.WriteLine(l);
        File.Move(tmp, SnapshotPath, true);
    }, ct);

    public static Task<MonitorLog> CompareAsync(IProgress<string>? progress, CancellationToken ct) => Task.Run(() =>
    {
        var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var fs = File.OpenRead(SnapshotPath))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        using (var r = new StreamReader(gz, Encoding.UTF8))
            for (string? line; (line = r.ReadLine()) != null;) before.Add(line);

        var after = Collect(progress, ct);
        progress?.Report("Vergleiche …");
        var added = after.Where(l => !before.Contains(l)).ToList();
        return BuildLog(added);
    }, ct);

    // ---------- Erfassen ----------

    static readonly HashSet<string> SkipDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Temp", "INetCache", "Cache", "Code Cache", "GPUCache", "CrashDumps", "D3DSCache", "Crashpad",
        "ModernUninstaller", "WindowsApps", "User Data", "Profiles", "$Recycle.Bin",
    };

    static List<string> NoisePrefixes()
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        return new[]
        {
            Path.Combine(F(Environment.SpecialFolder.CommonApplicationData), "Microsoft"),
            Path.Combine(F(Environment.SpecialFolder.CommonApplicationData), "Package Cache"),
            Path.Combine(F(Environment.SpecialFolder.LocalApplicationData), "Microsoft"),
            Path.Combine(F(Environment.SpecialFolder.LocalApplicationData), "Packages"),
            Path.Combine(F(Environment.SpecialFolder.ApplicationData), "Microsoft"),
            Path.Combine(F(Environment.SpecialFolder.ProgramFiles), "Windows Defender"),
            Path.Combine(F(Environment.SpecialFolder.ProgramFiles), "Windows Defender Advanced Threat Protection"),
        }.ToList();
    }

    static HashSet<string> Collect(IProgress<string>? progress, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var roots = new[]
        {
            F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86),
            F(Environment.SpecialFolder.CommonApplicationData), F(Environment.SpecialFolder.ApplicationData),
            F(Environment.SpecialFolder.LocalApplicationData),
            F(Environment.SpecialFolder.CommonStartMenu), F(Environment.SpecialFolder.StartMenu),
            F(Environment.SpecialFolder.DesktopDirectory), F(Environment.SpecialFolder.CommonDesktopDirectory),
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var noise = NoisePrefixes();
        int count = 0;
        foreach (var root in roots)
        {
            progress?.Report($"Dateien: {root}");
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                try
                {
                    foreach (var e in Directory.EnumerateFileSystemEntries(dir))
                    {
                        FileAttributes a;
                        try { a = File.GetAttributes(e); } catch { continue; }
                        if ((a & FileAttributes.ReparsePoint) != 0) continue;
                        if ((a & FileAttributes.Directory) != 0)
                        {
                            if (SkipDirNames.Contains(Path.GetFileName(e))) continue;
                            if (noise.Any(n => e.Equals(n, StringComparison.OrdinalIgnoreCase) || e.StartsWith(n + "\\", StringComparison.OrdinalIgnoreCase))) continue;
                            set.Add("F|" + e);
                            stack.Push(e);
                        }
                        else set.Add("F|" + e);
                        if (++count % 5000 == 0) progress?.Report($"Dateien: {count:N0} Einträge …");
                    }
                }
                catch { /* kein Zugriff */ }
            }
        }

        progress?.Report("Registry …");
        CollectRegistry(set, ct);
        progress?.Report("Dienste und Aufgaben …");
        CollectServicesAndTasks(set, ct);
        return set;
    }

    static void CollectRegistry(HashSet<string> set, CancellationToken ct)
    {
        var roots = new (RegistryHive hive, RegistryView view, string sub)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, "SOFTWARE"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, "SOFTWARE"),
            (RegistryHive.CurrentUser, RegistryView.Registry64, "Software"),
        };
        var skipTop = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Classes", "Microsoft", "Policies", "WOW6432Node", "RegisteredApplications" };

        foreach (var (hive, view, sub) in roots)
        {
            try
            {
                using var bk = RegistryKey.OpenBaseKey(hive, view);
                using var root = bk.OpenSubKey(sub);
                if (root == null) continue;
                foreach (var child in root.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();
                    if (skipTop.Contains(child)) continue;
                    set.Add("R|" + LeftoverService.RegPath(hive, view, sub + "\\" + child));
                    try
                    {
                        using var ck = root.OpenSubKey(child);
                        foreach (var g in ck?.GetSubKeyNames() ?? Array.Empty<string>())
                            set.Add("R|" + LeftoverService.RegPath(hive, view, sub + "\\" + child + "\\" + g));
                    }
                    catch { }
                }
            }
            catch { }
        }

        const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Registry64) })
        {
            try
            {
                using var bk = RegistryKey.OpenBaseKey(hive, view);
                using var k = bk.OpenSubKey(uninstall);
                foreach (var n in k?.GetSubKeyNames() ?? Array.Empty<string>())
                    set.Add("U|" + LeftoverService.RegPath(hive, view, uninstall + "\\" + n));
            }
            catch { }
        }

        foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
        {
            const string run = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
            try
            {
                using var bk = RegistryKey.OpenBaseKey(hive, view);
                using var k = bk.OpenSubKey(run);
                foreach (var v in k?.GetValueNames() ?? Array.Empty<string>())
                    set.Add("V|" + LeftoverService.RegPath(hive, view, run) + "|" + v);
            }
            catch { }
        }
    }

    static void CollectServicesAndTasks(HashSet<string> set, CancellationToken ct)
    {
        try
        {
            using var s = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            foreach (var n in s?.GetSubKeyNames() ?? Array.Empty<string>()) set.Add("S|" + n);
        }
        catch { }

        var tasks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks");
        try
        {
            if (!Directory.Exists(tasks)) return;
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var f in Directory.EnumerateFiles(tasks, "*", opts))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(tasks, f);
                if (!rel.StartsWith("Microsoft\\", StringComparison.OrdinalIgnoreCase)) set.Add("T|\\" + rel);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    // ---------- Protokoll bauen ----------

    static MonitorLog BuildLog(List<string> added)
    {
        var log = new MonitorLog();
        var items = log.Items;

        var newFiles = added.Where(l => l.StartsWith("F|")).Select(l => l[2..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in newFiles)
        {
            bool isDir = Directory.Exists(f);
            if (!isDir) log.TotalFiles++;
            var parent = Path.GetDirectoryName(f);
            if (parent != null && newFiles.Contains(parent)) continue;      // Elternordner deckt es ab
            items.Add(new LogEntry { Kind = isDir ? LeftoverKind.Folder : LeftoverKind.File, Path = f });
        }

        var newKeys = added.Where(l => l.StartsWith("R|")).Select(l => l[2..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var k in newKeys)
        {
            var i = k.LastIndexOf('\\');
            if (i > 0 && newKeys.Contains(k[..i])) continue;
            items.Add(new LogEntry { Kind = LeftoverKind.RegistryKey, Path = k });
        }

        var uninstallKeys = added.Where(l => l.StartsWith("U|")).Select(l => l[2..]).ToList();
        foreach (var u in uninstallKeys)
        {
            items.Add(new LogEntry { Kind = LeftoverKind.RegistryKey, Path = u });
            if (log.ProgramName.Length > 0) continue;
            try
            {
                var (hive, view, sub) = LeftoverService.ParseReg(u);
                using var bk = RegistryKey.OpenBaseKey(hive, view);
                using var k = bk.OpenSubKey(sub);
                var name = (k?.GetValue("DisplayName") as string)?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                log.ProgramName = name;
                log.Version = (k!.GetValue("DisplayVersion") as string ?? "").Trim();
                log.Publisher = (k.GetValue("Publisher") as string ?? "").Trim();
                log.UninstallKey = u;
            }
            catch { }
        }

        foreach (var v in added.Where(l => l.StartsWith("V|")))
        {
            var rest = v[2..];
            var i = rest.IndexOf('|');
            if (i > 0) items.Add(new LogEntry { Kind = LeftoverKind.RegistryValue, Path = rest[..i], ValueName = rest[(i + 1)..] });
        }
        foreach (var s in added.Where(l => l.StartsWith("S|"))) items.Add(new LogEntry { Kind = LeftoverKind.Service, Path = s[2..] });
        foreach (var t in added.Where(l => l.StartsWith("T|"))) items.Add(new LogEntry { Kind = LeftoverKind.Task, Path = t[2..] });

        return log;
    }

    // ---------- Existenzprüfung für gespeicherte Einträge ----------

    public static bool Exists(LogEntry e)
    {
        try
        {
            switch (e.Kind)
            {
                case LeftoverKind.File: return File.Exists(e.Path);
                case LeftoverKind.Folder: return Directory.Exists(e.Path);
                case LeftoverKind.RegistryKey:
                case LeftoverKind.Dll:
                {
                    var (hive, view, sub) = LeftoverService.ParseReg(e.Path);
                    using var bk = RegistryKey.OpenBaseKey(hive, view);
                    using var k = bk.OpenSubKey(sub);
                    return k != null;
                }
                case LeftoverKind.RegistryValue:
                {
                    var (hive, view, sub) = LeftoverService.ParseReg(e.Path);
                    using var bk = RegistryKey.OpenBaseKey(hive, view);
                    using var k = bk.OpenSubKey(sub);
                    return k?.GetValue(e.ValueName) != null;
                }
                case LeftoverKind.Service:
                {
                    using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + e.Path);
                    return k != null;
                }
                case LeftoverKind.Task:
                    return File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks") + e.Path);
            }
        }
        catch { }
        return false;
    }
}
