using System.Globalization;
using System.IO;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ModernUninstaller.Models;

namespace ModernUninstaller.Services;

public static class IconLoader
{
    static readonly Regex Spec = new("^\"?(?<p>[^\",]+?)\"?(,\\s*(?<i>-?\\d+))?$", RegexOptions.Compiled);

    public static ImageSource? Load(string? spec, string? fallbackDir)
    {
        try
        {
            string? path = null; int idx = 0;
            if (!string.IsNullOrWhiteSpace(spec))
            {
                var m = Spec.Match(Environment.ExpandEnvironmentVariables(spec.Trim()));
                if (m.Success)
                {
                    path = m.Groups["p"].Value;
                    if (m.Groups["i"].Success) idx = int.Parse(m.Groups["i"].Value, CultureInfo.InvariantCulture);
                }
            }
            if ((path == null || !File.Exists(path)) && !string.IsNullOrEmpty(fallbackDir) && Directory.Exists(fallbackDir))
            {
                path = Directory.EnumerateFiles(fallbackDir, "*.exe")
                    .FirstOrDefault(f => !Path.GetFileName(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase));
                idx = 0;
            }
            if (path == null || !File.Exists(path)) return null;

            var large = new IntPtr[1];
            if (Native.ExtractIconEx(path, idx, large, null, 1) == 0 && idx != 0)
                Native.ExtractIconEx(path, 0, large, null, 1);
            if (large[0] == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { Native.DestroyIcon(large[0]); }
        }
        catch { return null; }
    }
}

public static class ProgramScanner
{
    const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    static readonly Regex RuntimePattern = new(
        @"\.NET (Runtime|SDK|Core|Host|Windows Desktop|AppHost|Targeting)|Visual C\+\+ .*(Redistributable|Runtime)|XNA Framework|Windows Desktop Runtime|ASP\.NET|Windows Software Development Kit",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Heuristik: bekannte unerwünschte Beipack-Software (Toolbars, Suchmanipulation, "Optimizer").
    static readonly Regex BundlewarePattern = new(
        @"toolbar|conduit|babylon|search protect|webdiscover|mystart|ask\.com|yahoo! ?(toolbar|search)|delta (chrome|search)|wajam|crossrider|opencandy|driver ?(updater|booster) ?pro|pc ?(optimizer|cleaner) ?pro",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static Task<List<ProgramItem>> ScanAsync() => Task.Run(Scan);

    static List<ProgramItem> Scan()
    {
        var result = new List<ProgramItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var roots = new (RegistryHive hive, RegistryView view, string label)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM"),
            (RegistryHive.CurrentUser,  RegistryView.Registry64, "HKCU"),
            (RegistryHive.CurrentUser,  RegistryView.Registry32, "HKCU"),
        };

        foreach (var (hive, view, label) in roots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uk = baseKey.OpenSubKey(UninstallPath);
                if (uk == null) continue;
                foreach (var sub in uk.GetSubKeyNames())
                {
                    try
                    {
                        using var k = uk.OpenSubKey(sub);
                        if (k == null) continue;
                        var item = Read(k, sub, hive, view, label);
                        if (item == null) continue;
                        // WOW6432Node und 64-bit-Sicht können denselben Eintrag spiegeln (HKCU).
                        if (!seen.Add(item.Name + "|" + item.Version + "|" + item.Publisher + "|" + item.InstallLocation)) continue;
                        result.Add(item);
                    }
                    catch { /* defekter Schlüssel */ }
                }
            }
            catch { /* Hive nicht lesbar */ }
        }

        MarkBundleware(result);
        MonitorStore.MarkLogged(result);
        return result.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static ProgramItem? Read(RegistryKey k, string keyName, RegistryHive hive, RegistryView view, string label)
    {
        var name = (k.GetValue("DisplayName") as string)?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        if (k.GetValue("SystemComponent") is int sc && sc == 1) return null;
        if (!string.IsNullOrEmpty(k.GetValue("ParentKeyName") as string)) return null;
        var releaseType = k.GetValue("ReleaseType") as string ?? "";
        if (releaseType is "Update" or "Hotfix" or "Security Update") return null;
        if (Regex.IsMatch(name, @"^(Security )?Update for |^KB\d{6,}|^Hotfix for ", RegexOptions.IgnoreCase)) return null;

        var uninstall = (k.GetValue("UninstallString") as string ?? "").Trim();
        var quiet = (k.GetValue("QuietUninstallString") as string ?? "").Trim();
        if (uninstall.Length == 0 && quiet.Length == 0) return null;

        var location = (k.GetValue("InstallLocation") as string ?? "").Trim().Trim('"');
        var iconSpec = k.GetValue("DisplayIcon") as string;

        if (location.Length == 0 || !Directory.Exists(location))
        {
            // Fallback: Ordner der Uninstall-Datei bzw. des Icons
            var guess = GuessLocation(iconSpec) ?? GuessLocation(uninstall);
            if (guess != null) location = guess;
        }

        DateTime? date = null;
        if (DateTime.TryParseExact(k.GetValue("InstallDate") as string, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) date = d;
        else if (location.Length > 0 && Directory.Exists(location)) date = Directory.GetCreationTime(location);

        long size = 0;
        if (k.GetValue("EstimatedSize") is int kb && kb > 0) size = kb * 1024L;

        var publisher = (k.GetValue("Publisher") as string ?? "").Trim();
        return new ProgramItem
        {
            Id = keyName,
            Name = name,
            Version = (k.GetValue("DisplayVersion") as string ?? "").Trim(),
            Publisher = publisher,
            InstallLocation = location,
            InstallDate = date,
            LastUsed = LastUsed(location),
            UninstallString = uninstall,
            QuietUninstallString = quiet,
            RegistryPath = $"{label}{(view == RegistryView.Registry32 ? "[32]" : "")}\\{UninstallPath}\\{keyName}",
            Is64Bit = view == RegistryView.Registry64 && hive == RegistryHive.LocalMachine || (hive == RegistryHive.CurrentUser && view == RegistryView.Registry64),
            IsRuntime = RuntimePattern.IsMatch(name),
            IsMicrosoft = publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase),
            SizeBytes = size,
            Icon = IconLoader.Load(iconSpec, location),
        };
    }

    static string? GuessLocation(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var m = Regex.Match(Environment.ExpandEnvironmentVariables(spec.Trim()), "^\"?(?<p>[A-Za-z]:\\\\[^\",]+?\\.(exe|ico|dll))", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var dir = Path.GetDirectoryName(m.Groups["p"].Value);
        if (dir == null || !Directory.Exists(dir)) return null;
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (dir.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return null;
        return dir;
    }

    /// <summary>Näherung: jüngstes Zugriffs-/Schreibdatum der EXE-Dateien im Installationsordner (Zugriffsdatum kann von Windows deaktiviert sein).</summary>
    static DateTime? LastUsed(string location)
    {
        try
        {
            if (location.Length == 0 || !Directory.Exists(location)) return null;
            DateTime? best = null;
            foreach (var f in Directory.EnumerateFiles(location, "*.exe").Take(40))
            {
                var fi = new FileInfo(f);
                var t = fi.LastAccessTime > fi.LastWriteTime ? fi.LastAccessTime : fi.LastWriteTime;
                if (best == null || t > best) best = t;
            }
            return best;
        }
        catch { return null; }
    }

    static void MarkBundleware(List<ProgramItem> items)
    {
        foreach (var p in items)
            if (BundlewarePattern.IsMatch(p.Name)) p.IsBundleware = true;
    }

    /// <summary>Ordnergröße im Hintergrund nachladen, wenn die Registry keinen Wert liefert.</summary>
    // Keine identischen Verzeichnisse mehrfach voll durchsuchen (z.B. 32-/64-bit Einträge).
    // Höchstens zwei I/O-Worker, damit die Oberfläche auf HDDs/SSDs bedienbar bleibt.
    static readonly ConcurrentDictionary<string, long> SizeCache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task FillSizesAsync(IEnumerable<ProgramItem> items, CancellationToken ct = default)
    {
        var pending = items.Where(i => i.Kind == ItemKind.Win32 && i.SizeBytes <= 0 &&
                                       !string.IsNullOrWhiteSpace(i.InstallLocation))
                           .GroupBy(i => i.InstallLocation, StringComparer.OrdinalIgnoreCase)
                           .ToArray();
        using var limit = new SemaphoreSlim(2);
        var tasks = pending.Select(async group =>
        {
            await limit.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                var size = await Task.Run(() =>
                {
                    if (SizeCache.TryGetValue(group.Key, out var cached)) return cached;
                    var measured = DirSize(group.Key, ct);
                    if (!ct.IsCancellationRequested && measured > 0)
                        SizeCache.TryAdd(group.Key, measured);
                    return measured;
                }, ct);
                if (ct.IsCancellationRequested) return;
                foreach (var p in group) p.SizeBytes = size;
            }
            finally { limit.Release(); }
        });
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { /* Seite wurde neu geladen */ }
    }

    public static long DirSize(string dir, CancellationToken ct = default)
    {
        long total = 0;
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opts))
            {
                if (ct.IsCancellationRequested) break;
                total += f.Length;
            }
        }
        catch { }
        return total;
    }
}

