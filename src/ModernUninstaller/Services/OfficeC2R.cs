using System.Text.RegularExpressions;
using Microsoft.Win32;
using ModernUninstaller.Models;

namespace ModernUninstaller.Services;

/// <summary>
/// Microsoft 365 / Office Click-to-Run. Jede Sprache steht als eigener Eintrag in "Programme" und teilt sich den
/// Ordner "Microsoft Office". Diese Einträge lassen sich ohne Fenster entfernen (DisplayLevel=False).
/// </summary>
public static class OfficeC2R
{
    const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    static readonly Regex ProductRx = new(@"productstoremove=(?<p>[^\s""]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsC2R(ProgramItem p) => IsC2R(p.UninstallString);

    public static bool IsC2R(string uninstallString) =>
        uninstallString.Contains("OfficeClickToRun.exe", StringComparison.OrdinalIgnoreCase) &&
        uninstallString.Contains("productstoremove=", StringComparison.OrdinalIgnoreCase);

    /// <summary>z. B. "O365ProPlusRetail.16_de-de_x-none"</summary>
    public static string ProductToRemove(string uninstallString)
    {
        var m = ProductRx.Match(uninstallString);
        return m.Success ? m.Groups["p"].Value : "";
    }

    /// <summary>Produkt ohne Sprache, z. B. "O365ProPlusRetail".</summary>
    public static string ProductId(string uninstallString)
    {
        var p = ProductToRemove(uninstallString);
        var dot = p.IndexOf('.');
        return dot > 0 ? p[..dot] : p;
    }

    /// <summary>Befehl ohne Office-Oberfläche. Rückfragen entfallen, der Fortschritt erscheint nicht.</summary>
    public static string SilentCommand(string uninstallString)
    {
        var cmd = uninstallString.Trim();
        if (!Regex.IsMatch(cmd, @"\bdisplaylevel=", RegexOptions.IgnoreCase)) cmd += " DisplayLevel=False";
        else cmd = Regex.Replace(cmd, @"\bdisplaylevel=\S+", "DisplayLevel=False", RegexOptions.IgnoreCase);
        return cmd;
    }

    /// <summary>Alle aktuell installierten Click-to-Run-Einträge: (Registry-Pfad, Uninstall-Befehl).</summary>
    public static List<(string registryPath, string uninstall)> InstalledEntries()
    {
        var list = new List<(string, string)>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var bk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uk = bk.OpenSubKey(UninstallPath);
                if (uk == null) continue;
                foreach (var sub in uk.GetSubKeyNames())
                {
                    using var k = uk.OpenSubKey(sub);
                    var u = k?.GetValue("UninstallString") as string ?? "";
                    if (IsC2R(u)) list.Add(($"HKLM{(view == RegistryView.Registry32 ? "[32]" : "")}\\{UninstallPath}\\{sub}", u));
                }
            }
            catch { }
        }
        return list.DistinctBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Produkte, die nach dieser Auswahl gar keine Sprache mehr hätten (= Office wird ganz entfernt).</summary>
    public static List<string> ProductsRemovedCompletely(IEnumerable<ProgramItem> selection)
    {
        var selected = selection.Where(IsC2R).ToList();
        if (selected.Count == 0) return new();
        var installed = InstalledEntries();
        var chosen = new HashSet<string>(selected.Select(s => ProductToRemove(s.UninstallString)), StringComparer.OrdinalIgnoreCase);
        return selected.Select(s => ProductId(s.UninstallString)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(pid => installed.Where(i => ProductId(i.uninstall).Equals(pid, StringComparison.OrdinalIgnoreCase))
                                   .All(i => chosen.Contains(ProductToRemove(i.uninstall))))
            .ToList();
    }

    /// <summary>Bleibt nach dem Entfernen dieses Eintrags noch ein anderer Click-to-Run-Eintrag übrig?</summary>
    public static bool OthersRemain(ProgramItem p) =>
        InstalledEntries().Any(i => !i.registryPath.Equals(p.RegistryPath, StringComparison.OrdinalIgnoreCase));
}
