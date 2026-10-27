using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using ModernUninstaller.Models;

namespace ModernUninstaller.Services;

public sealed record CleanResult(int Removed, int Failed, int ScheduledForReboot, List<string> Errors);

public static class LeftoverService
{
    static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "common files", "system", "system32", "temp", "packages", "programs", "data", "setup", "installer", "app", "apps",
        "google", "intel", "nvidia", "amd", "realtek", "adobe", "java", "oracle", "mozilla", "bin", "program", "programs", "x64", "x86", "win32",
        "win64", "release", "debug", "lib", "libs", "resources", "common", "shared", "plugins", "tools", "update", "updates", "cache", "local", "roaming",
    };

    // ---------- Namenskandidaten ----------

    static string Clean(string s)
    {
        s = Regex.Replace(s, @"\([^)]*\)", " ");                       // (arm64), (64-bit)
        s = Regex.Replace(s, @"\b(x64|x86|arm64|64-bit|32-bit)\b", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"(?<=^|\s)v?\d+([._]\d+)*(?=\s|$)", " ");   // Versionsnummern als eigenes Wort ("26.05", "16"), nicht "7" in "7-Zip"
        return Regex.Replace(s, @"\s+", " ").Trim(' ', '-', '_', '.');
    }

    /// <summary>Namen, unter denen das Programm Ordner und Registry-Schlüssel anlegt: Anzeigename, Ordnername der Installation, Schlüsselname.</summary>
    public static List<string> Candidates(ProgramItem p)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string raw)
        {
            var n = Clean(raw);
            if (n.Length < 4 || Generic.Contains(n)) return;
            set.Add(n);
            set.Add(n.Replace(" ", ""));
        }
        Add(p.Name);
        var loc = p.InstallLocation.TrimEnd('\\');
        if (loc.Length > 0 && !IsProtectedDir(loc)) Add(Path.GetFileName(loc));
        if (p.Id.Length > 0 && p.Id != "forced" && !p.Id.StartsWith("{") && !p.Id.Contains('\\') && p.Kind == ItemKind.Win32) Add(p.Id);
        return set.ToList();
    }

    /// <summary>Installationsordner nur verwenden, wenn er eindeutig dem Programm gehört (nicht ein Systemordner oder ein Sammelordner).</summary>
    static bool UsableLocation(string loc)
    {
        if (loc.Length <= 10 || IsProtectedDir(loc)) return false;
        if (loc.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length < 3) return false;
        return !Generic.Contains(Path.GetFileName(loc));
    }

    // ---------- Scan ----------

    public static Task<List<Leftover>> ScanAsync(ProgramItem p, IProgress<string>? progress = null) => Task.Run(() => Scan(p, progress));

    static List<Leftover> Scan(ProgramItem p, IProgress<string>? progress)
    {
        var result = new List<Leftover>();
        var names = Candidates(p);
        var loc = p.InstallLocation.TrimEnd('\\');
        var useLoc = UsableLocation(loc);

        progress?.Report("Dateien …");
        ScanFiles(p, names, loc, useLoc, result);
        if (useLoc)
        {
            progress?.Report("Verknüpfungen …");
            ScanShortcuts(loc, result);
        }
        progress?.Report("Registry …");
        ScanRegistry(p, names, loc, useLoc, result);
        if (useLoc)
        {
            progress?.Report("Dateizuordnungen und Kontextmenüs …");
            foreach (var (hive, view, sub) in new[]
                     {
                         (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Classes"),
                         (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\Classes"),
                         (RegistryHive.CurrentUser, RegistryView.Registry64, @"Software\Classes"),
                     })
                ScanClasses(hive, view, sub, loc, result);
            ScanAppPaths(loc, result);
            ScanFirewall(loc, result);
            ScanEnvPath(loc, result);
        }
        progress?.Report("Aufgaben …");
        ScanTasks(names, loc, useLoc, result);
        progress?.Report("Dienste …");
        ScanServices(p, loc, useLoc, result);
        progress?.Report("DLL-Registrierungen …");
        if (useLoc) ScanDlls(loc, result);
        result.AddRange(MonitorStore.LeftoversFor(p));      // vom Installations-Monitor protokollierte Elemente

        var unique = result.GroupBy(l => (l.Kind, l.Path, l.ValueName, l.Detail), new KeyComparer()).Select(g => g.First()).ToList();
        AddEmptyParents(unique);
        return unique;
    }

    sealed class KeyComparer : IEqualityComparer<(LeftoverKind, string, string, string)>
    {
        public bool Equals((LeftoverKind, string, string, string) a, (LeftoverKind, string, string, string) b) =>
            a.Item1 == b.Item1 && string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase) && a.Item3 == b.Item3 && a.Item4 == b.Item4;
        public int GetHashCode((LeftoverKind, string, string, string) o) => HashCode.Combine(o.Item1, o.Item2.ToLowerInvariant(), o.Item3, o.Item4);
    }

    // ---------- Dateien ----------

    static void ScanFiles(ProgramItem p, List<string> names, string loc, bool useLoc, List<Leftover> result)
    {
        if (useLoc && Directory.Exists(loc))
            result.Add(new Leftover { Kind = LeftoverKind.Folder, Path = loc });

        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        var roots = new[]
        {
            F(Environment.SpecialFolder.ProgramFiles),
            F(Environment.SpecialFolder.ProgramFilesX86),
            F(Environment.SpecialFolder.CommonApplicationData),
            F(Environment.SpecialFolder.ApplicationData),
            local,
            Path.Combine(local, "Programs"),
            Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow"),
            Path.Combine(F(Environment.SpecialFolder.ProgramFiles), "Common Files"),
            Path.Combine(F(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(F(Environment.SpecialFolder.StartMenu), "Programs"),
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var root in roots)
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root))
                    if (names.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase)) AddDir(dir, result);

                // Herausgeberordner → nur Unterordner mit dem Produktnamen (der Herausgeberordner selbst folgt über AddEmptyParents)
                if (p.Publisher.Length > 3 && !Generic.Contains(p.Publisher))
                {
                    var pubDir = Path.Combine(root, p.Publisher);
                    if (Directory.Exists(pubDir))
                        foreach (var dir in Directory.EnumerateDirectories(pubDir))
                            if (names.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase)) AddDir(dir, result);
                }

                if (root.Contains("Start Menu", StringComparison.OrdinalIgnoreCase))
                    foreach (var f in Directory.EnumerateFiles(root, "*.lnk"))
                        if (names.Any(n => string.Equals(Path.GetFileNameWithoutExtension(f), n, StringComparison.OrdinalIgnoreCase)))
                            result.Add(new Leftover { Kind = LeftoverKind.File, Path = f });
            }
            catch { /* kein Zugriff */ }
        }

        foreach (var desktop in new[] { F(Environment.SpecialFolder.DesktopDirectory), F(Environment.SpecialFolder.CommonDesktopDirectory) })
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(desktop, "*.lnk"))
                    if (names.Any(n => string.Equals(Path.GetFileNameWithoutExtension(f), n, StringComparison.OrdinalIgnoreCase)))
                        result.Add(new Leftover { Kind = LeftoverKind.File, Path = f });
            }
            catch { }
        }

        // Persönliche Daten (Dokumente) nur anzeigen, nicht vorauswählen
        try
        {
            var docs = F(Environment.SpecialFolder.MyDocuments);
            if (Directory.Exists(docs))
                foreach (var dir in Directory.EnumerateDirectories(docs))
                    if (names.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase))
                        result.Add(new Leftover { Kind = LeftoverKind.Folder, Path = dir, IsPersonalData = true, IsChecked = false });
        }
        catch { }
    }

    static void AddDir(string dir, List<Leftover> result)
    {
        if (IsProtectedDir(dir)) return;
        result.Add(new Leftover { Kind = LeftoverKind.Folder, Path = dir });
    }

    /// <summary>Verknüpfungen (Startmenü, Desktop, Autostart, Taskleiste), deren Ziel im Installationsordner liegt.</summary>
    static void ScanShortcuts(string loc, List<Leftover> result)
    {
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned"),
        }.Where(Directory.Exists).ToList();

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        dynamic? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var d in dirs)
                foreach (var lnk in Directory.EnumerateFiles(d, "*.lnk", opts))
                {
                    try
                    {
                        string target = shell!.CreateShortcut(lnk).TargetPath;
                        if (target.Length > 0 && target.StartsWith(loc + "\\", StringComparison.OrdinalIgnoreCase))
                            result.Add(new Leftover { Kind = LeftoverKind.File, Path = lnk });
                    }
                    catch { }
                }
        }
        catch { }
        finally { if (shell != null) try { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); } catch { } }
    }

    /// <summary>Wenn nach dem Löschen ein Elternordner (z. B. Herausgeberordner, Startmenü-Gruppe) leer wäre, ihn gleich mit anbieten.</summary>
    static void AddEmptyParents(List<Leftover> list)
    {
        var paths = new HashSet<string>(list.Where(l => l.Kind is LeftoverKind.File or LeftoverKind.Folder).Select(l => l.Path.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
        bool grew = true;
        int rounds = 0;
        while (grew && rounds++ < 3)
        {
            grew = false;
            foreach (var p in paths.ToList())
            {
                var parent = Path.GetDirectoryName(p);
                if (parent == null || paths.Contains(parent) || IsProtectedDir(parent) || !Directory.Exists(parent)) continue;
                try
                {
                    if (Directory.EnumerateFileSystemEntries(parent).All(e => paths.Contains(e.TrimEnd('\\'))))
                    {
                        paths.Add(parent);
                        list.Add(new Leftover { Kind = LeftoverKind.Folder, Path = parent });
                        grew = true;
                    }
                }
                catch { }
            }
        }
    }

    // ---------- Registry ----------

    static void ScanRegistry(ProgramItem p, List<string> names, string loc, bool useLoc, List<Leftover> result)
    {
        // Eigener Uninstall-Eintrag, falls der Original-Uninstaller ihn nicht entfernt hat
        if (p.RegistryPath.Length > 0)
        {
            var (hive, view, sub) = ParseReg(p.RegistryPath);
            using var bk = RegistryKey.OpenBaseKey(hive, view);
            using var k = bk.OpenSubKey(sub);
            if (k != null) result.Add(new Leftover { Kind = LeftoverKind.RegistryKey, Path = p.RegistryPath });
        }

        var softwareRoots = new (RegistryHive, RegistryView, string)[]
        {
            (RegistryHive.CurrentUser, RegistryView.Registry64, "Software"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, "SOFTWARE"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, "SOFTWARE"),
        };
        foreach (var (hive, view, sub) in softwareRoots)
        {
            try
            {
                using var bk = RegistryKey.OpenBaseKey(hive, view);
                using var root = bk.OpenSubKey(sub);
                if (root == null) continue;
                var existing = root.GetSubKeyNames();
                foreach (var n in names)
                    foreach (var match in existing.Where(e => string.Equals(e, n, StringComparison.OrdinalIgnoreCase)))
                        result.Add(new Leftover { Kind = LeftoverKind.RegistryKey, Path = RegPath(hive, view, sub + "\\" + match) });

                if (p.Publisher.Length > 3 && !Generic.Contains(p.Publisher))
                {
                    var pub = existing.FirstOrDefault(e => string.Equals(e, p.Publisher, StringComparison.OrdinalIgnoreCase));
                    if (pub != null)
                    {
                        using var pk = root.OpenSubKey(pub);
                        var children = pk?.GetSubKeyNames() ?? Array.Empty<string>();
                        var mine = children.Where(c => names.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
                        foreach (var child in mine)
                            result.Add(new Leftover { Kind = LeftoverKind.RegistryKey, Path = RegPath(hive, view, sub + "\\" + pub + "\\" + child) });
                        // Herausgeber-Schlüssel mit, wenn er sonst leer wäre
                        if (mine.Count > 0 && children.Length == mine.Count && (pk?.ValueCount ?? 0) == 0)
                            result.Add(new Leftover { Kind = LeftoverKind.RegistryKey, Path = RegPath(hive, view, sub + "\\" + pub) });
                    }
                }
            }
            catch { }
        }

        if (!useLoc) return;
        // Autostart-Werte, die in den Installationsordner zeigen
        foreach (var (hive, view) in new[] { (RegistryHive.CurrentUser, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32) })
            foreach (var run in new[] { @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\RunOnce" })
            {
                try
                {
                    using var bk = RegistryKey.OpenBaseKey(hive, view);
                    using var k = bk.OpenSubKey(run);
                    if (k == null) continue;
                    foreach (var v in k.GetValueNames())
                    {
                        var data = k.GetValue(v) as string ?? "";
                        if (data.Contains(loc, StringComparison.OrdinalIgnoreCase))
                        {
                            result.Add(new Leftover { Kind = LeftoverKind.RegistryValue, Path = RegPath(hive, view, run), ValueName = v });
                            // Windows merkt sich den Aktiviert/Deaktiviert-Status des Autostarts separat
                            const string approvedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
                            using var ak = bk.OpenSubKey(approvedKey);
                            if (run.EndsWith(@"\Run") && ak?.GetValue(v) != null)
                                result.Add(new Leftover { Kind = LeftoverKind.RegistryValue, Path = RegPath(hive, view, approvedKey), ValueName = v });
                        }
                    }
                }
                catch { }
            }
    }

    static readonly HashSet<string> ClassesSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLSID", "Interface", "TypeLib", "Installer", "AppID", "MIME", "Local Settings", "WOW6432Node", "Wow6432Node", "Extensions",
    };

    static readonly HashSet<string> ClassesContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "*", "Directory", "Folder", "Drive", "AllFilesystemObjects", "SystemFileAssociations", "DesktopBackground", "Applications", "Unknown", "AllFileSystemObjects",
    };

    /// <summary>ProgIDs, Dateizuordnungen und Shell-Verben, deren Werte in den Installationsordner zeigen.</summary>
    static void ScanClasses(RegistryHive hive, RegistryView view, string sub, string loc, List<Leftover> result)
    {
        try
        {
            using var bk = RegistryKey.OpenBaseKey(hive, view);
            using var root = bk.OpenSubKey(sub);
            if (root == null) return;
            foreach (var top in root.GetSubKeyNames())
            {
                if (ClassesSkip.Contains(top)) continue;
                try
                {
                    using var tk = root.OpenSubKey(top);
                    if (tk == null) continue;
                    var hits = new List<string>();
                    FindValueHits(tk, top, 0, loc, hits);
                    if (hits.Count == 0) continue;

                    bool container = ClassesContainers.Contains(top) || top.StartsWith('.');
                    if (!container)
                    {
                        result.Add(new Leftover { Kind = LeftoverKind.RegistryKey, Path = RegPath(hive, view, sub + "\\" + top) });
                        continue;
                    }
                    foreach (var h in hits.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var key = h;
                        if (key.EndsWith("\\command", StringComparison.OrdinalIgnoreCase)) key = key[..key.LastIndexOf('\\')];   // ganzes Verb entfernen
                        if (top.Equals("Applications", StringComparison.OrdinalIgnoreCase))
                        {
                            var parts = key.Split('\\');
                            if (parts.Length >= 2) key = parts[0] + "\\" + parts[1];
                        }
                        if (key.Contains('\\') || !container)                            // nie einen Top-Level-Container (z. B. "*", "Directory") löschen
                            result.Add(new Leftover { Kind = LeftoverKind.RegistryKey, Path = RegPath(hive, view, sub + "\\" + key) });
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    static void FindValueHits(RegistryKey key, string rel, int depth, string loc, List<string> hits)
    {
        try
        {
            foreach (var name in key.GetValueNames())
            {
                var v = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if ((v is string s && s.Contains(loc, StringComparison.OrdinalIgnoreCase)) ||
                    (v is string[] arr && arr.Any(a => a.Contains(loc, StringComparison.OrdinalIgnoreCase))))
                {
                    hits.Add(rel);
                    break;
                }
            }
            if (depth >= 5) return;
            foreach (var child in key.GetSubKeyNames())
            {
                using var ck = key.OpenSubKey(child);
                if (ck != null) FindValueHits(ck, rel + "\\" + child, depth + 1, loc, hits);
            }
        }
        catch { }
    }

    static void ScanAppPaths(string loc, List<Leftover> result)
    {
        const string appPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Registry64) })
        {
            try
            {
                using var bk = RegistryKey.OpenBaseKey(hive, view);
                using var k = bk.OpenSubKey(appPaths);
                foreach (var n in k?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var sk = k!.OpenSubKey(n);
                    var def = sk?.GetValue(null) as string ?? "";
                    var path = sk?.GetValue("Path") as string ?? "";
                    if (def.Contains(loc, StringComparison.OrdinalIgnoreCase) || path.Contains(loc, StringComparison.OrdinalIgnoreCase))
                        result.Add(new Leftover { Kind = LeftoverKind.RegistryKey, Path = RegPath(hive, view, appPaths + "\\" + n) });
                }
            }
            catch { }
        }
    }

    static void ScanFirewall(string loc, List<Leftover> result)
    {
        const string rules = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(rules);
            if (k == null) return;
            foreach (var n in k.GetValueNames())
                if ((k.GetValue(n) as string ?? "").Contains("App=" + loc, StringComparison.OrdinalIgnoreCase))
                    result.Add(new Leftover { Kind = LeftoverKind.RegistryValue, Path = RegPath(RegistryHive.LocalMachine, RegistryView.Registry64, rules), ValueName = n });
        }
        catch { }
    }

    static readonly (RegistryHive hive, string sub)[] EnvKeys =
    {
        (RegistryHive.CurrentUser, "Environment"),
        (RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"),
    };

    static void ScanEnvPath(string loc, List<Leftover> result)
    {
        foreach (var (hive, sub) in EnvKeys)
        {
            try
            {
                using var bk = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var k = bk.OpenSubKey(sub);
                var raw = k?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                if (raw == null) continue;
                foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var e = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"')).TrimEnd('\\');
                    if (e.Equals(loc, StringComparison.OrdinalIgnoreCase) || e.StartsWith(loc + "\\", StringComparison.OrdinalIgnoreCase))
                        result.Add(new Leftover { Kind = LeftoverKind.EnvPath, Path = RegPath(hive, RegistryView.Registry64, sub), ValueName = "Path", Detail = entry.Trim() });
                }
            }
            catch { }
        }
    }

    // ---------- Aufgaben, Dienste, DLLs ----------

    static void ScanTasks(List<string> names, string loc, bool useLoc, List<Leftover> result)
    {
        var tasks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks");
        if (!Directory.Exists(tasks)) return;
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var f in Directory.EnumerateFiles(tasks, "*", opts))
            {
                var rel = Path.GetRelativePath(tasks, f);
                var file = Path.GetFileName(f);
                bool hit = names.Any(n => file.Contains(n, StringComparison.OrdinalIgnoreCase));
                if (!hit && useLoc && new FileInfo(f).Length < 200_000)
                {
                    try { hit = File.ReadAllText(f).Contains(loc, StringComparison.OrdinalIgnoreCase); } catch { }
                }
                if (hit && !rel.StartsWith("Microsoft\\", StringComparison.OrdinalIgnoreCase))
                    result.Add(new Leftover { Kind = LeftoverKind.Task, Path = "\\" + rel });
            }
        }
        catch { }
    }

    static void ScanServices(ProgramItem p, string loc, bool useLoc, List<Leftover> result)
    {
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services == null) return;
            foreach (var s in services.GetSubKeyNames())
            {
                using var k = services.OpenSubKey(s);
                var image = k?.GetValue("ImagePath") as string ?? "";
                if (image.Length == 0) continue;
                if (useLoc && Environment.ExpandEnvironmentVariables(image).Contains(loc, StringComparison.OrdinalIgnoreCase))
                    result.Add(new Leftover { Kind = LeftoverKind.Service, Path = s });
            }
        }
        catch { }
    }

    static void ScanDlls(string loc, List<Leftover> result)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var bk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var clsid = bk.OpenSubKey(@"SOFTWARE\Classes\CLSID");
                if (clsid == null) continue;
                foreach (var id in clsid.GetSubKeyNames())
                    foreach (var server in new[] { "InprocServer32", "LocalServer32" })
                    {
                        using var s = clsid.OpenSubKey(id + "\\" + server);
                        var path = s?.GetValue(null) as string;
                        if (path != null && Environment.ExpandEnvironmentVariables(path).Contains(loc, StringComparison.OrdinalIgnoreCase))
                            result.Add(new Leftover { Kind = LeftoverKind.Dll, Path = RegPath(RegistryHive.LocalMachine, view, @"SOFTWARE\Classes\CLSID\" + id) });
                    }
            }
            catch { }
        }
        // Typbibliotheken
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var bk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var tl = bk.OpenSubKey(@"SOFTWARE\Classes\TypeLib");
                foreach (var guid in tl?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var g = tl!.OpenSubKey(guid);
                    bool mine = false;
                    foreach (var ver in g?.GetSubKeyNames() ?? Array.Empty<string>())
                    {
                        using var v = g!.OpenSubKey(ver);
                        foreach (var lcid in v?.GetSubKeyNames() ?? Array.Empty<string>())
                            foreach (var plat in new[] { "win32", "win64", "arm64" })
                            {
                                using var pk = v!.OpenSubKey(lcid + "\\" + plat);
                                if ((pk?.GetValue(null) as string ?? "").Contains(loc, StringComparison.OrdinalIgnoreCase)) mine = true;
                            }
                    }
                    if (mine) result.Add(new Leftover { Kind = LeftoverKind.Dll, Path = RegPath(RegistryHive.LocalMachine, view, @"SOFTWARE\Classes\TypeLib\" + guid) });
                }
            }
            catch { }
        }
    }

    // ---------- Löschen ----------

    public static Task<CleanResult> CleanAsync(IEnumerable<Leftover> items, bool toRecycleBin, IProgress<int>? progress = null) => Task.Run(() =>
    {
        int removed = 0, failed = 0, reboot = 0;
        var errors = new List<string>();
        var list = items.ToList();
        int i = 0;
        // Tiefste Pfade zuerst, damit Unterordner vor Elternordnern fallen
        foreach (var l in list.OrderByDescending(x => x.Path.Length))
        {
            progress?.Report(++i * 100 / Math.Max(1, list.Count));
            try
            {
                switch (l.Kind)
                {
                    case LeftoverKind.File:
                        if (File.Exists(l.Path)) DeleteFile(l.Path, toRecycleBin, ref reboot);
                        removed++; break;
                    case LeftoverKind.Folder:
                        if (!Directory.Exists(l.Path)) { removed++; break; }
                        if (!IsSafeToDelete(l.Path)) throw new InvalidOperationException("Geschützter Pfad");
                        var (left, sched) = DeleteDir(l.Path, toRecycleBin);
                        reboot += sched;
                        if (left > 0) { failed++; errors.Add($"{l.Path}: {left} Elemente nicht löschbar"); }
                        else removed++;
                        break;
                    case LeftoverKind.RegistryKey:
                    case LeftoverKind.Dll:
                        DeleteRegKey(l.Path); removed++; break;
                    case LeftoverKind.RegistryValue:
                        DeleteRegValue(l.Path, l.ValueName); removed++; break;
                    case LeftoverKind.EnvPath:
                        RemoveEnvPathEntry(l.Path, l.Detail); removed++; break;
                    case LeftoverKind.Task:
                        RunTool("schtasks.exe", $"/End /TN \"{l.Path}\"", ignoreFailure: true);
                        RunTool("schtasks.exe", $"/Delete /TN \"{l.Path}\" /F"); removed++; break;
                    case LeftoverKind.Service:
                        RunTool("sc.exe", $"stop \"{l.Path}\"", ignoreFailure: true);
                        RunTool("sc.exe", $"delete \"{l.Path}\""); removed++; break;
                }
            }
            catch (Exception ex)
            {
                failed++; errors.Add($"{l.Path}: {ex.Message}");
            }
        }
        return new CleanResult(removed, failed, reboot, errors);
    });

    static void DeleteFile(string path, bool recycle, ref int reboot)
    {
        try
        {
            if (recycle) { FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); return; }
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch
        {
            if (File.Exists(path))
            {
                if (Native.MoveFileEx(path, null, Native.MOVEFILE_DELAY_UNTIL_REBOOT)) reboot++;
                else throw;
            }
        }
    }

    /// <summary>Löscht einen Ordner möglichst vollständig: erst Papierkorb (falls gewünscht), sonst Datei für Datei.
    /// Nicht löschbare Dateien (in Benutzung) werden zum Löschen beim nächsten Neustart vorgemerkt.</summary>
    static (int left, int scheduled) DeleteDir(string path, bool recycle)
    {
        if (recycle)
        {
            try { FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); if (!Directory.Exists(path)) return (0, 0); }
            catch { /* weiter mit hartem Löschen */ }
        }

        int left = 0, scheduled = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        List<string> files, dirs;
        try
        {
            files = Directory.EnumerateFiles(path, "*", opts).ToList();
            dirs = Directory.EnumerateDirectories(path, "*", opts).OrderByDescending(d => d.Length).ToList();
        }
        catch { files = new(); dirs = new(); }

        foreach (var f in files)
        {
            try { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); }
            catch
            {
                if (File.Exists(f))
                {
                    if (Native.MoveFileEx(f, null, Native.MOVEFILE_DELAY_UNTIL_REBOOT)) scheduled++; else left++;
                }
            }
        }
        foreach (var d in dirs.Append(path))
        {
            try { Directory.Delete(d, false); }
            catch
            {
                if (Directory.Exists(d))
                {
                    // Ordner mit vorgemerkten Dateien lassen sich erst beim Neustart (in dieser Reihenfolge) entfernen
                    if (scheduled > 0 && Native.MoveFileEx(d, null, Native.MOVEFILE_DELAY_UNTIL_REBOOT)) scheduled++;
                    else left++;
                }
            }
        }
        return (left, scheduled);
    }

    static void DeleteRegKey(string full)
    {
        var (hive, view, sub) = ParseReg(full);
        using var bk = RegistryKey.OpenBaseKey(hive, view);
        bk.DeleteSubKeyTree(sub, false);
    }

    static void DeleteRegValue(string full, string name)
    {
        var (hive, view, sub) = ParseReg(full);
        using var bk = RegistryKey.OpenBaseKey(hive, view);
        using var k = bk.OpenSubKey(sub, true);
        k?.DeleteValue(name, false);
    }

    static void RemoveEnvPathEntry(string full, string entry)
    {
        var (hive, view, sub) = ParseReg(full);
        using var bk = RegistryKey.OpenBaseKey(hive, view);
        using var k = bk.OpenSubKey(sub, true);
        if (k == null) return;
        var raw = k.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (raw == null) return;
        var kind = k.GetValueKind("Path");
        var kept = raw.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(e => !string.Equals(e.Trim(), entry, StringComparison.OrdinalIgnoreCase));
        k.SetValue("Path", string.Join(';', kept), kind == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
    }

    static void RunTool(string exe, string args, bool ignoreFailure = false)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit(15_000);
        if (!ignoreFailure && p.ExitCode != 0) throw new InvalidOperationException($"{exe} Exitcode {p.ExitCode}");
    }

    // ---------- Pfadhilfen und Sicherheit ----------

    internal static string RegPath(RegistryHive hive, RegistryView view, string sub) =>
        $"{(hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM")}{(view == RegistryView.Registry32 ? "[32]" : "")}\\{sub}";

    internal static (RegistryHive, RegistryView, string) ParseReg(string full)
    {
        var parts = full.Split('\\', 2);
        var hive = parts[0].StartsWith("HKCU") ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
        var view = parts[0].EndsWith("[32]") ? RegistryView.Registry32 : RegistryView.Registry64;
        return (hive, view, parts[1]);
    }

    static bool IsProtectedDir(string dir)
    {
        var d = dir.TrimEnd('\\');
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f).TrimEnd('\\');
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        var protectedDirs = new[]
        {
            F(Environment.SpecialFolder.Windows), F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86),
            F(Environment.SpecialFolder.CommonApplicationData), F(Environment.SpecialFolder.ApplicationData), local,
            F(Environment.SpecialFolder.UserProfile), F(Environment.SpecialFolder.System), F(Environment.SpecialFolder.MyDocuments),
            F(Environment.SpecialFolder.DesktopDirectory), F(Environment.SpecialFolder.CommonDesktopDirectory),
            F(Environment.SpecialFolder.StartMenu), F(Environment.SpecialFolder.CommonStartMenu),
            F(Environment.SpecialFolder.Programs), F(Environment.SpecialFolder.CommonPrograms),
            F(Environment.SpecialFolder.Startup), F(Environment.SpecialFolder.CommonStartup),
            Path.Combine(F(Environment.SpecialFolder.ProgramFiles), "Common Files"),
            Path.Combine(F(Environment.SpecialFolder.ProgramFilesX86), "Common Files"),
            Path.Combine(local, "Programs"), Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow"),
            Path.GetPathRoot(d)?.TrimEnd('\\') ?? "C:",
        };
        if (protectedDirs.Any(x => string.Equals(x, d, StringComparison.OrdinalIgnoreCase))) return true;
        var win = F(Environment.SpecialFolder.Windows);
        return d.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsSafeToDelete(string dir) => !IsProtectedDir(dir) && dir.TrimEnd('\\').Count(c => c == '\\') >= 2;
}


