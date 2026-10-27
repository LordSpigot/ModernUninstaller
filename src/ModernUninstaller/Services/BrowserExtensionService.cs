using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ModernUninstaller.Models;

namespace ModernUninstaller.Services;

public static class BrowserExtensionService
{
    static readonly (string name, string exe, string scheme, string relPath)[] Chromium =
    {
        ("Google Chrome", "chrome.exe", "chrome", @"Google\Chrome\User Data"),
        ("Microsoft Edge", "msedge.exe", "edge", @"Microsoft\Edge\User Data"),
        ("Brave", "brave.exe", "brave", @"BraveSoftware\Brave-Browser\User Data"),
    };

    public static Task<List<ProgramItem>> ScanAsync() => Task.Run(() =>
    {
        var list = new List<ProgramItem>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        foreach (var (browser, _, _, rel) in Chromium)
        {
            var root = Path.Combine(local, rel);
            if (!Directory.Exists(root)) continue;
            foreach (var profile in Directory.EnumerateDirectories(root).Where(d => Path.GetFileName(d) is "Default" || Path.GetFileName(d).StartsWith("Profile ")))
            {
                var ext = Path.Combine(profile, "Extensions");
                if (!Directory.Exists(ext)) continue;
                foreach (var idDir in Directory.EnumerateDirectories(ext))
                {
                    try
                    {
                        var verDir = Directory.EnumerateDirectories(idDir).OrderByDescending(d => d).FirstOrDefault();
                        if (verDir == null) continue;
                        var manifest = Path.Combine(verDir, "manifest.json");
                        if (!File.Exists(manifest)) continue;
                        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                        var m = doc.RootElement;
                        var name = Resolve(m, "name", verDir) ?? Path.GetFileName(idDir);
                        list.Add(new ProgramItem
                        {
                            Kind = ItemKind.Extension,
                            Id = Path.GetFileName(idDir),
                            Name = name,
                            Version = m.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                            Publisher = browser,
                            Source = browser,
                            InstallLocation = verDir,
                            InstallDate = Directory.GetCreationTime(idDir),
                            SizeBytes = ProgramScanner.DirSize(verDir),
                            Icon = LoadIcon(m, verDir),
                        });
                    }
                    catch { }
                }
            }
        }

        var ffProfiles = Path.Combine(roaming, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(ffProfiles))
        {
            foreach (var profile in Directory.EnumerateDirectories(ffProfiles))
            {
                try
                {
                    var file = Path.Combine(profile, "extensions.json");
                    if (!File.Exists(file)) continue;
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (!doc.RootElement.TryGetProperty("addons", out var addons)) continue;
                    foreach (var a in addons.EnumerateArray())
                    {
                        if (a.TryGetProperty("type", out var t) && t.GetString() != "extension") continue;
                        if (a.TryGetProperty("location", out var loc) && loc.GetString() != "app-profile") continue;
                        var name = a.TryGetProperty("defaultLocale", out var dl) && dl.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (string.IsNullOrEmpty(name)) continue;
                        list.Add(new ProgramItem
                        {
                            Kind = ItemKind.Extension,
                            Id = a.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                            Name = name,
                            Version = a.TryGetProperty("version", out var ver) ? ver.GetString() ?? "" : "",
                            Publisher = "Mozilla Firefox",
                            Source = "Mozilla Firefox",
                            InstallDate = a.TryGetProperty("installDate", out var idt) && idt.TryGetInt64(out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : null,
                        });
                    }
                }
                catch { }
            }
        }
        return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    });

    /// <summary>Löst "__MSG_key__" über _locales (de, en) auf.</summary>
    static string? Resolve(JsonElement manifest, string prop, string verDir)
    {
        if (!manifest.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.String) return null;
        var s = el.GetString();
        if (s == null || !s.StartsWith("__MSG_")) return s;
        var key = s.Substring(6).TrimEnd('_');
        foreach (var lang in new[] { "de", "en", "en_US", "en_GB" })
        {
            var f = Path.Combine(verDir, "_locales", lang, "messages.json");
            if (!File.Exists(f)) continue;
            try
            {
                using var d = JsonDocument.Parse(File.ReadAllText(f));
                foreach (var p in d.RootElement.EnumerateObject())
                    if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase) && p.Value.TryGetProperty("message", out var msg))
                        return msg.GetString();
            }
            catch { }
        }
        return null;
    }

    static System.Windows.Media.ImageSource? LoadIcon(JsonElement m, string verDir)
    {
        try
        {
            if (!m.TryGetProperty("icons", out var icons) || icons.ValueKind != JsonValueKind.Object) return null;
            var best = icons.EnumerateObject().OrderByDescending(p => int.TryParse(p.Name, out var n) ? n : 0).FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.String);
            if (best.Value.ValueKind != JsonValueKind.String) return null;
            var file = Path.Combine(verDir, best.Value.GetString()!.Replace('/', '\\'));
            if (!File.Exists(file)) return null;
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 48;
            bmp.UriSource = new Uri(file);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>Entfernen geht sicher nur über den Browser selbst: öffnet die Detailseite der Erweiterung.</summary>
    public static bool OpenInBrowser(ProgramItem ext)
    {
        try
        {
            var c = Chromium.FirstOrDefault(b => b.name == ext.Source);
            if (c.name != null)
            {
                Process.Start(new ProcessStartInfo(c.exe, $"{c.scheme}://extensions/?id={ext.Id}") { UseShellExecute = true });
                return true;
            }
            Process.Start(new ProcessStartInfo("firefox.exe", "about:addons") { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }
}
