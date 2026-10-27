using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using ModernUninstaller.Dialogs;
using ModernUninstaller.Models;
using ModernUninstaller.Pages;
using ModernUninstaller.Services;

namespace ModernUninstaller;

public partial class MainWindow : Window
{
    readonly ListPage _programs, _apps, _extensions;
    readonly HealthPage _health;
    readonly ToolboxPage _toolbox;
    IPage _current;

    public MainWindow(IReadOnlyCollection<string>? autoHealthChecks = null)
    {
        InitializeComponent();
        ApplyScale();
        UpdateThemeButton();

        _programs = new ListPage(new ListPageConfig
        {
            Title = "Programme",
            SearchHint = "Alle installierten Programme durchsuchen …",
            Noun = "Programme",
            Glyph = "",
            ActionText = "Deinstallieren",
            ShowDriveFilter = true,
            ShowRuntimeToggle = true,
            Load = ProgramScanner.ScanAsync,
            Tabs = new[]
            {
                new ListTab { Label = "Alle", Match = _ => true },
                new ListTab { Label = "Bundleware", Match = p => p.IsBundleware, EmptyText = "Keine Bundleware erkannt." },
                new ListTab { Label = "Protokolle", Match = p => p.HasLog, EmptyText = "Programme, die mit dem Installations-Monitor überwacht wurden, können später gründlich deinstalliert werden.\nDen Monitor finden Sie in der Toolbox." },
                new ListTab { Label = "Übergroße Programme", Match = p => p.SizeBytes >= Settings.Current.OversizedMb * 1024L * 1024L, EmptyText = "Keine übergroßen Programme." },
                new ListTab { Label = "Selten genutzt", Match = p => p.LastUsed is { } d && d < DateTime.Now.AddMonths(-Settings.Current.RarelyUsedMonths), EmptyText = "Keine selten genutzten Programme." },
            },
        });
        _apps = new ListPage(new ListPageConfig
        {
            Title = "Windows Apps",
            SearchHint = "Alle Windows-Apps durchsuchen …",
            Noun = "Apps",
            Glyph = "",
            ActionText = "Deinstallieren",
            Load = AppxService.ScanAsync,
            Tabs = new[]
            {
                new ListTab { Label = "Alle", Match = _ => true },
                new ListTab { Label = "Microsoft-Apps", Match = p => p.IsMicrosoft, EmptyText = "Keine Microsoft-Apps entfernbar." },
                new ListTab { Label = "Drittanbieter-Apps", Match = p => !p.IsMicrosoft, EmptyText = "Keine Drittanbieter-Apps." },
            },
        });
        _extensions = new ListPage(new ListPageConfig
        {
            Title = "Browser-Erweiterungen",
            SearchHint = "Alle Browser-Erweiterungen durchsuchen …",
            Noun = "Erweiterungen",
            Glyph = "",
            ActionText = "Im Browser entfernen",
            Load = BrowserExtensionService.ScanAsync,
            Tabs = new[] { new ListTab { Label = "Alle", Match = _ => true, EmptyText = "Keine Erweiterungen gefunden." } },
        });
        _health = new HealthPage(() => _programs.AllItems, IsAdmin, RestartElevatedForHealthScan);
        _toolbox = new ToolboxPage();

        _programs.ActionRequested += async items => await UninstallProgramsAsync(items);
        _apps.ActionRequested += async items => await RemoveAppsAsync(items);
        _extensions.ActionRequested += items => OpenExtensions(items);
        _health.ProgramsChanged += () => _ = _programs.LoadAsync();
        _toolbox.ToolRequested += async key => await OpenToolAsync(key);

        _current = _programs;
        Loaded += async (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int round = 2;                                  // DWMWCP_ROUND
            Native.DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
            AdminBadge.Visibility = IsAdmin() ? Visibility.Collapsed : Visibility.Visible;
            if (autoHealthChecks is { Count: > 0 })
            {
                // Der neue Administratorprozess bekommt exakt dieselben Check-Optionen.
                if (autoHealthChecks.Contains(HealthService.BrokenEntries))
                    await Navigate(_programs);
                NavHealth.IsChecked = true;
                await Navigate(_health);
                await _health.ScanAfterElevationAsync(autoHealthChecks);
            }
            else await Navigate(_programs);
        };
    }

    // ---------- Navigation ----------

    async Task Navigate(IPage page)
    {
        _current = page;
        PageHost.Content = page;
        SearchBox.Tag = page.SearchHint;
        SearchBox.Text = "";
        await page.LoadAsyncIfNeeded();
    }

    async void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        IPage page = sender == NavPrograms ? _programs
            : sender == NavHealth ? _health
            : sender == NavExtensions ? _extensions
            : sender == NavApps ? _apps
            : _toolbox;
        await Navigate(page);
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e) => _current?.Search(SearchBox.Text);

    // ---------- Fenster ----------

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void Window_StateChanged(object? sender, EventArgs e)
    {
        var max = WindowState == WindowState.Maximized;
        MaxBtn.Content = max ? "" : "";
        Root.Margin = max ? new Thickness(7) : new Thickness(0);
    }

    void ApplyScale()
    {
        var s = ThemeService.FontScale(Settings.Current.FontScaleIndex);
        Root.LayoutTransform = new ScaleTransform(s, s);
    }

    void UpdateThemeButton() => ThemeBtn.Content = ThemeService.IsDark ? "" : "";

    void Theme_Click(object sender, RoutedEventArgs e)
    {
        Settings.Current.Theme = ThemeService.IsDark ? "Light" : "Dark";
        Settings.Save();
        ThemeService.Apply(Settings.Current.Theme);
        UpdateThemeButton();
    }

    // ---------- Menü ----------

    void Menu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        MenuItem Item(string glyph, string header, RoutedEventHandler click)
        {
            var mi = new MenuItem { Header = header, Tag = glyph };
            mi.Click += click;
            return mi;
        }
        menu.Items.Add(Item("", "Einstellungen …", (_, _) => OpenSettings()));
        menu.Items.Add(Item("", "Deinstallationsverlauf", async (_, _) => await OpenToolAsync("history")));
        menu.Items.Add(Item("", "Wiederherstellung", (_, _) => Launch("rstrui.exe")));
        menu.Items.Add(Item("", "Programmliste exportieren …", (_, _) => ExportList()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("", "Aktualisieren", async (_, _) => await _current.LoadAsync()));
        if (!IsAdmin()) menu.Items.Add(Item("", "Als Administrator neu starten", Elevate_Click));
        menu.Items.Add(Item("", "Info", (_, _) =>
            MessageBox.Show(this, $"Modern Uninstaller 0.2\nArchitektur: {RuntimeInformation.ProcessArchitecture}\n.NET {Environment.Version}", "Info", MessageBoxButton.OK, MessageBoxImage.Information)));
        menu.PlacementTarget = MenuBtn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.HorizontalOffset = -170;
        menu.IsOpen = true;
    }

    void OpenSettings()
    {
        var dlg = new SettingsDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;
        ThemeService.Apply(Settings.Current.Theme);
        UpdateThemeButton();
        ApplyScale();
        _programs.ReloadSettingsFilters();
    }

    static void Launch(string exe, string args = "")
    {
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }); } catch { }
    }

    static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    bool RestartElevatedForHealthScan(IReadOnlyList<string> checks)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
            };
            psi.ArgumentList.Add("--health-scan");
            psi.ArgumentList.Add(string.Join(',', checks));
            if (Process.Start(psi) == null) return false;
            Close();
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC bewusst abgebrochen; Hauptfenster bleibt benutzbar.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    void Elevate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Close();
        }
        catch { /* UAC abgebrochen */ }
    }

    void ExportList()
    {
        var dlg = new SaveFileDialog { Filter = "CSV-Datei|*.csv", FileName = "Programme.csv" };
        if (dlg.ShowDialog(this) != true) return;
        var sb = new StringBuilder("Name;Version;Herausgeber;Größe;Installiert am;Installationspfad;Architektur\r\n");
        foreach (var p in _programs.AllItems)
            sb.AppendLine(string.Join(';', new[] { p.Name, p.Version, p.Publisher, p.SizeText, p.DateText, p.InstallLocation, p.Arch }.Select(Csv)));
        File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
    }

    static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    // ---------- Aktionen ----------

    async Task UninstallProgramsAsync(IReadOnlyList<ProgramItem> items)
    {
        var confirm = new ConfirmDialog(items, items.Count == 1 ? "Das gewählte Programm deinstallieren?" : $"Die {items.Count} gewählten Programme deinstallieren?", "Deinstallieren", true) { Owner = this };
        if (confirm.ShowDialog() != true) return;
        await RunFlowAsync(items, confirm.CreateRestorePoint, confirm.AutoLeftovers, skipOriginal: false);
    }

    async Task RunFlowAsync(IReadOnlyList<ProgramItem> items, bool restore, bool auto, bool skipOriginal)
    {
        foreach (var i in items) i.IsChecked = false;
        new UninstallFlowDialog(items, restore, auto, skipOriginal) { Owner = this }.ShowDialog();
        await _programs.LoadAsync();
    }

    async Task RemoveAppsAsync(IReadOnlyList<ProgramItem> items)
    {
        var confirm = new ConfirmDialog(items, items.Count == 1 ? "Diese App entfernen?" : $"Diese {items.Count} Apps entfernen?", "Entfernen", false) { Owner = this };
        if (confirm.ShowDialog() != true) return;
        var errors = new List<string>();
        foreach (var app in items)
        {
            var (ok, msg) = await AppxService.RemoveAsync(app);
            History.Add(new HistoryEntry(DateTime.Now, app.Name, app.Version, app.Publisher, ok ? "App entfernt" : "Fehlgeschlagen", 0));
            if (!ok) errors.Add($"{app.Name}: {(msg.Length > 200 ? msg[..200] + "…" : msg)}");
        }
        if (errors.Count > 0) MessageBox.Show(this, string.Join("\n\n", errors), "Nicht alle Apps konnten entfernt werden", MessageBoxButton.OK, MessageBoxImage.Warning);
        await _apps.LoadAsync();
    }

    void OpenExtensions(IReadOnlyList<ProgramItem> items)
    {
        if (MessageBox.Show(this,
                "Browser lassen Erweiterungen nur selbst entfernen. Für jede gewählte Erweiterung wird die Seite im Browser geöffnet, dort auf „Entfernen“ klicken.\n\nFortfahren?",
                "Erweiterungen entfernen", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var ext in items.Take(5)) BrowserExtensionService.OpenInBrowser(ext);
    }

    // ---------- Toolbox ----------

    async Task OpenToolAsync(string key)
    {
        switch (key)
        {
            case "monitor":
                new MonitorDialog { Owner = this }.ShowDialog();
                await _programs.LoadAsync();
                break;

            case "shredder":
                new ShredderDialog { Owner = this }.ShowDialog();
                break;

            case "force":
            case "easy":
            {
                if (_programs.AllItems.Count == 0) await _programs.LoadAsync();
                var dlg = new ForceDialog(_programs.AllItems) { Owner = this };
                if (dlg.ShowDialog() != true || dlg.Result == null) break;
                if (dlg.Forced)
                    await RunFlowAsync(new[] { dlg.Result }, false, false, skipOriginal: true);
                else
                    await UninstallProgramsAsync(new[] { dlg.Result });
                break;
            }

            case "history":
                new ListToolDialog("Deinstallationsverlauf", "Alle Deinstallationen, die mit diesem Programm durchgeführt wurden.", "Verlauf löschen", "Noch keine Einträge.",
                    () => Task.FromResult(History.Load().Select(h => new ToolRow
                    {
                        Title = $"{h.Name} {h.Version}".Trim(),
                        Sub = $"{h.Date:dd.MM.yyyy HH:mm} · {h.Result}" + (h.LeftoversRemoved > 0 ? $" · {h.LeftoversRemoved} Reste entfernt" : ""),
                        ShowCheck = false,
                    }).ToList()),
                    (_, _) => { History.Clear(); return Task.FromResult("Verlauf gelöscht."); }) { Owner = this }.ShowDialog();
                break;

            case "winupd":
                new ListToolDialog("Windows-Update-Deinstallation", "Installierte Windows-Updates. Das Entfernen braucht Administratorrechte; einige Updates lassen sich nicht deinstallieren.",
                    "Deinstallieren", "Keine Updates gefunden.", LoadHotfixesAsync, UninstallHotfixesAsync) { Owner = this }.ShowDialog();
                break;

            case "bloat":
                new ListToolDialog("Bloatware-Entferner", "Vorinstallierte Store-Apps, die viele Nutzer nicht brauchen. Es werden nur die installierten angezeigt.",
                    "Entfernen", "Keine der bekannten Apps installiert.", LoadBloatAsync, RemoveBloatAsync) { Owner = this }.ShowDialog();
                break;

            case "updater":
                new ListToolDialog("Software-Updater", "Verfügbare Updates laut winget (Windows-Paketmanager).", "Aktualisieren", "Keine Updates verfügbar (oder winget ist nicht installiert).",
                    LoadWingetAsync, UpdateWingetAsync) { Owner = this }.ShowDialog();
                break;
        }
    }

    static async Task<List<ToolRow>> LoadHotfixesAsync()
    {
        var r = await PowerShellRunner.RunAsync("Get-HotFix | Sort-Object InstalledOn -Descending | ForEach-Object { '{0}|{1}|{2}' -f $_.HotFixID,$_.Description,$_.InstalledOn.ToString('yyyy-MM-dd') }");
        return r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('|'))
            .Where(p => p.Length >= 3 && p[0].StartsWith("KB", StringComparison.OrdinalIgnoreCase))
            .Select(p => new ToolRow { Title = p[0], Sub = $"{p[1]} · installiert am {p[2]}", Tag = p[0] })
            .ToList();
    }

    static async Task<string> UninstallHotfixesAsync(IReadOnlyList<ToolRow> rows, IProgress<string> progress)
    {
        int ok = 0, fail = 0;
        foreach (var row in rows)
        {
            progress.Report($"Entferne {row.Tag} …");
            var kb = ((string)row.Tag!).Substring(2);
            try
            {
                using var p = Process.Start(new ProcessStartInfo("wusa.exe", $"/uninstall /kb:{kb} /quiet /norestart") { UseShellExecute = false, CreateNoWindow = true })!;
                await p.WaitForExitAsync();
                if (p.ExitCode is 0 or 3010) ok++; else fail++;
            }
            catch { fail++; }
        }
        return $"{ok} entfernt, {fail} fehlgeschlagen. Ein Neustart kann nötig sein.";
    }

    static readonly string[] BloatNames =
    {
        "Microsoft.XboxApp", "Microsoft.XboxGamingOverlay", "Microsoft.XboxGameOverlay", "Microsoft.GamingApp", "Microsoft.MicrosoftSolitaireCollection",
        "Microsoft.BingNews", "Microsoft.BingWeather", "Microsoft.BingFinance", "Microsoft.BingSports", "Microsoft.GetHelp", "Microsoft.Getstarted",
        "Microsoft.MicrosoftOfficeHub", "Microsoft.People", "Microsoft.WindowsFeedbackHub", "Microsoft.ZuneMusic", "Microsoft.ZuneVideo",
        "Microsoft.YourPhone", "Microsoft.549981C3F5F10", "Clipchamp.Clipchamp", "MicrosoftTeams", "Microsoft.MixedReality.Portal", "Microsoft.SkypeApp",
    };

    static async Task<List<ToolRow>> LoadBloatAsync()
    {
        var all = await AppxService.ScanAsync();
        var raw = await PowerShellRunner.RunAsync("Get-AppxPackage | ForEach-Object { '{0}|{1}' -f $_.Name,$_.PackageFullName }");
        var map = raw.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('|')).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
        return BloatNames.Where(map.ContainsKey)
            .Select(n => new ToolRow { Title = n.Replace("Microsoft.", ""), Sub = n, Tag = map[n] })
            .ToList();
    }

    static async Task<string> RemoveBloatAsync(IReadOnlyList<ToolRow> rows, IProgress<string> progress)
    {
        int ok = 0, fail = 0;
        foreach (var row in rows)
        {
            progress.Report($"Entferne {row.Title} …");
            var (success, _) = await AppxService.RemoveAsync(new ProgramItem { Id = (string)row.Tag! });
            if (success) ok++; else fail++;
        }
        return $"{ok} entfernt, {fail} fehlgeschlagen.";
    }

    static async Task<List<ToolRow>> LoadWingetAsync()
    {
        var rows = new List<ToolRow>();
        try
        {
            var r = await PowerShellRunner.RunAsync("winget upgrade --include-unknown --accept-source-agreements 2>&1 | Out-String -Width 250", 90_000);
            var lines = r.Output.Replace("\r", "").Split('\n');
            int sep = Array.FindIndex(lines, l => l.Length > 10 && l.Trim().All(c => c == '-'));
            if (sep < 1) return rows;
            var header = lines[sep - 1];
            // Spaltenanfänge aus der (sprachabhängigen) Kopfzeile bestimmen
            var starts = new List<int>();
            for (int i = 0; i < header.Length; i++)
                if (header[i] != ' ' && (i == 0 || header[i - 1] == ' ' && i > 1 && header[i - 2] == ' ')) starts.Add(i);
            if (starts.Count < 4) return rows;
            string Col(string line, int c)
            {
                int a = starts[c], b = c + 1 < starts.Count ? starts[c + 1] : line.Length;
                return a >= line.Length ? "" : line.Substring(a, Math.Min(b, line.Length) - a).Trim();
            }
            for (int i = sep + 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line) || line.Length < starts[^1] || Col(line, 1).Contains(' ')) continue;
                rows.Add(new ToolRow { Title = Col(line, 0), Sub = $"{Col(line, 1)} · {Col(line, 2)} → {Col(line, 3)}", Tag = Col(line, 1) });
            }
        }
        catch { }
        return rows;
    }

    static async Task<string> UpdateWingetAsync(IReadOnlyList<ToolRow> rows, IProgress<string> progress)
    {
        int ok = 0, fail = 0;
        foreach (var row in rows)
        {
            progress.Report($"Aktualisiere {row.Title} …");
            var id = ((string)row.Tag!).Replace("'", "''");
            var r = await PowerShellRunner.RunAsync($"winget upgrade --id '{id}' --silent --accept-source-agreements --accept-package-agreements; exit $LASTEXITCODE", 600_000);
            if (r.ExitCode == 0) ok++; else fail++;
        }
        return $"{ok} aktualisiert, {fail} fehlgeschlagen.";
    }
}

static class PageExtensions
{
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IPage, object> Loaded = new();

    /// <summary>Lädt die Seite nur beim ersten Besuch.</summary>
    public static async Task LoadAsyncIfNeeded(this IPage page)
    {
        if (Loaded.TryGetValue(page, out _)) return;
        Loaded.Add(page, new object());
        await page.LoadAsync();
    }
}


