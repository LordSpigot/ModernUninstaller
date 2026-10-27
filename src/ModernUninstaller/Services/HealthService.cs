using System.IO;
using System.Text.RegularExpressions;
using ModernUninstaller.Models;

namespace ModernUninstaller.Services;

public static class HealthService
{
    public const string BrokenEntries = "broken";
    public const string InstallerFiles = "installers";
    public const string TempFiles = "temp";

    static readonly Regex InstallerName = new(@"(setup|install|installer|_x64|_x86)[^\\]*\.(exe|msi)$|\.msi$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Task<List<Leftover>> ScanAsync(string check, IReadOnlyList<ProgramItem> programs) => Task.Run(() => check switch
    {
        BrokenEntries => Broken(programs),
        InstallerFiles => Installers(),
        TempFiles => Temp(),
        _ => new List<Leftover>(),
    });

    /// <summary>Uninstall-Einträge, deren Uninstaller und Installationsordner nicht mehr existieren.</summary>
    static List<Leftover> Broken(IReadOnlyList<ProgramItem> programs)
    {
        var list = new List<Leftover>();
        foreach (var p in programs.Where(p => p.Kind == ItemKind.Win32))
        {
            var cmd = Environment.ExpandEnvironmentVariables(p.UninstallString.Length > 0 ? p.UninstallString : p.QuietUninstallString).Trim();
            var m = Regex.Match(cmd, "^\"?(?<f>[A-Za-z]:\\\\[^\"]*?\\.(exe|bat|cmd))", RegexOptions.IgnoreCase);
            if (!m.Success) continue;                                // z. B. msiexec – nicht prüfbar
            if (File.Exists(m.Groups["f"].Value)) continue;
            if (p.InstallLocation.Length > 0 && Directory.Exists(p.InstallLocation)) continue;
            list.Add(new Leftover { Kind = LeftoverKind.RegistryKey, Path = p.RegistryPath });
        }
        return list;
    }

    static List<Leftover> Installers()
    {
        var list = new List<Leftover>();
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(downloads)) return list;
        try
        {
            foreach (var f in Directory.EnumerateFiles(downloads, "*", SearchOption.TopDirectoryOnly))
                if (InstallerName.IsMatch(f)) list.Add(new Leftover { Kind = LeftoverKind.File, Path = f, IsPersonalData = true });
        }
        catch { }
        return list;
    }

    /// <summary>
    /// Im Temp-Ordner alte Dateien nach Größe bestimmen, ohne sämtliche Treffer im RAM zu behalten.
    /// LastWriteTimeUtc reicht für „seit 3 Tagen unverändert“; LastAccessTime ist häufig deaktiviert.
    /// </summary>
    static List<Leftover> Temp()
    {
        const int maxResults = 1000;
        var largest = new PriorityQueue<(string path, long size), long>();
        var temp = Path.GetTempPath().TrimEnd('\\');
        var cutoff = DateTime.UtcNow.AddDays(-3);
        try
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var fi in new DirectoryInfo(temp).EnumerateFiles("*", opts))
            {
                try
                {
                    if (fi.LastWriteTimeUtc >= cutoff) continue;
                    var size = fi.Length;
                    if (largest.Count < maxResults) largest.Enqueue((fi.FullName, size), size);
                    else if (largest.TryPeek(out _, out var minSize) && size > minSize)
                    {
                        largest.Dequeue();
                        largest.Enqueue((fi.FullName, size), size);
                    }
                }
                catch (IOException) { /* Datei wurde parallel gelöscht */ }
                catch (UnauthorizedAccessException) { /* Nur verfügbare Dateien berücksichtigen */ }
            }
        }
        catch (IOException) { /* Temp-Verzeichnis derzeit nicht lesbar */ }
        catch (UnauthorizedAccessException) { /* Kein Zugriff auf Temp */ }
        return largest.UnorderedItems
            .OrderByDescending(x => x.Element.size)
            .Select(x => new Leftover { Kind = LeftoverKind.File, Path = x.Element.path })
            .ToList();
    }

}
