using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace ModernUninstaller.Services;

public sealed record HistoryEntry(DateTime Date, string Name, string Version, string Publisher, string Result, int LeftoversRemoved);

public static class History
{
    static string FilePath => Path.Combine(Settings.DataDir, "history.json");

    public static List<HistoryEntry> Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }

    public static void Add(HistoryEntry e)
    {
        try
        {
            var all = Load();
            all.Insert(0, e);
            Directory.CreateDirectory(Settings.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all.Take(500).ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }
}

public enum ShredMode { Quick = 1, Advanced = 3, Professional = 7 }

public static class Shredder
{
    /// <summary>Überschreibt Dateien mehrfach mit Zufallsdaten, setzt die Größe auf 0, benennt um und löscht.
    /// Auf SSDs und Dateisystemen mit Wear-Leveling oder Snapshots garantiert das kein physisches Löschen.</summary>
    public static Task<(int ok, int failed)> ShredAsync(IEnumerable<string> paths, ShredMode mode, IProgress<string>? log, CancellationToken ct) => Task.Run(() =>
    {
        int ok = 0, failed = 0;
        var files = new List<string>();
        var dirs = new List<string>();
        foreach (var p in paths)
        {
            if (File.Exists(p)) files.Add(p);
            else if (Directory.Exists(p))
            {
                dirs.Add(p);
                files.AddRange(Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }));
            }
        }
        foreach (var f in files)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                log?.Report(f);
                ShredFile(f, (int)mode);
                ok++;
            }
            catch { failed++; }
        }
        foreach (var d in dirs.OrderByDescending(x => x.Length))
        {
            try { Directory.Delete(d, true); } catch { }
        }
        return (ok, failed);
    }, ct);

    static void ShredFile(string path, int passes)
    {
        File.SetAttributes(path, FileAttributes.Normal);
        var len = new FileInfo(path).Length;
        var buf = new byte[1024 * 1024];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            for (int pass = 0; pass < passes; pass++)
            {
                fs.Position = 0;
                for (long written = 0; written < len;)
                {
                    var n = (int)Math.Min(buf.Length, len - written);
                    if (pass == passes - 1 && passes > 1) Array.Clear(buf, 0, n);   // letzter Durchgang: Nullen
                    else RandomNumberGenerator.Fill(buf.AsSpan(0, n));
                    fs.Write(buf, 0, n);
                    written += n;
                }
                fs.Flush(true);
            }
            fs.SetLength(0);
        }
        var renamed = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N"));
        File.Move(path, renamed);
        File.Delete(renamed);
    }
}
