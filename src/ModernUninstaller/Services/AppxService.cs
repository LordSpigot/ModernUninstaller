using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModernUninstaller.Models;

namespace ModernUninstaller.Services;

public static class AppxService
{
    public static async Task<List<ProgramItem>> ScanAsync()
    {
        var list = new List<ProgramItem>();
        var r = await PowerShellRunner.RunAsync(
            "Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.IsResourcePackage -and $_.SignatureKind -ne 'System' } | " +
            "Select-Object Name,PackageFullName,Publisher,@{n='Version';e={$_.Version.ToString()}},InstallLocation,NonRemovable | ConvertTo-Json -Compress");
        if (r.ExitCode != 0 || string.IsNullOrWhiteSpace(r.Output)) return list;

        using var doc = JsonDocument.Parse(r.Output);
        var elems = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
        foreach (var e in elems)
        {
            string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var name = S("Name");
            if (name.Length == 0) continue;
            // Edge und WebView2 werden wie im Original nicht als entfernbare App gelistet
            if (name.StartsWith("Microsoft.MicrosoftEdge", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft.Win32WebViewHost", StringComparison.OrdinalIgnoreCase)) continue;
            if (e.TryGetProperty("NonRemovable", out var nr) && nr.ValueKind == JsonValueKind.True) continue;
            var publisher = S("Publisher");
            var location = S("InstallLocation");
            list.Add(new ProgramItem
            {
                Kind = ItemKind.Appx,
                Id = S("PackageFullName"),
                Name = Pretty(name),
                Version = S("Version"),
                Publisher = Regex.Match(publisher, "CN=([^,]+)").Groups[1].Value is { Length: > 0 } cn ? cn : publisher,
                InstallLocation = location,
                InstallDate = TryDate(location),
                IsMicrosoft = publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase),
                Is64Bit = S("PackageFullName").Contains("_x64_") || S("PackageFullName").Contains("_arm64_"),
                Icon = null,
            });
        }
        return list.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static DateTime? TryDate(string location)
    {
        try { return Directory.Exists(location) ? Directory.GetCreationTime(location) : null; } catch { return null; }
    }

    /// <summary>"Microsoft.WindowsCalculator" → "Windows Calculator".</summary>
    static string Pretty(string name)
    {
        var n = Regex.Replace(name, @"^(Microsoft|MicrosoftCorporationII|AnthropicPBC)\.", "");
        n = Regex.Replace(n, "(?<=[a-z])(?=[A-Z])", " ");
        return n.Replace('.', ' ');
    }

    public static async Task<(bool ok, string message)> RemoveAsync(ProgramItem app)
    {
        var r = await PowerShellRunner.RunAsync($"Remove-AppxPackage -Package '{app.Id.Replace("'", "''")}' -ErrorAction Stop", 180_000);
        return r.ExitCode == 0 ? (true, "") : (false, (r.Error.Length > 0 ? r.Error : r.Output).Trim());
    }
}
