using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ModernUninstaller.Models;
using ModernUninstaller.Services;

namespace ModernUninstaller.Pages;

public interface IPage
{
    string SearchHint { get; }
    Task LoadAsync();
    void Search(string text);
}

public sealed class ListTab
{
    public required string Label { get; init; }
    public required Func<ProgramItem, bool> Match { get; init; }
    public string? EmptyText { get; init; }
}

public sealed class ListPageConfig
{
    public required string Title { get; init; }
    public required string SearchHint { get; init; }
    public required string Noun { get; init; }          // "Programme"
    public required string Glyph { get; init; }
    public required string ActionText { get; init; }
    public required ListTab[] Tabs { get; init; }
    public required Func<Task<List<ProgramItem>>> Load { get; init; }
    public bool ShowDriveFilter { get; init; }
    public bool ShowRuntimeToggle { get; init; }
}

public partial class ListPage : UserControl, IPage
{
    readonly ListPageConfig _cfg;
    readonly List<ProgramItem> _all = new();
    readonly ObservableCollection<ProgramItem> _view = new();
    ListTab _tab;
    string _search = "";
    string _sort = "Bezeichnung";
    string _drive = "";                 // leer = alle Laufwerke
    bool _loaded;
    CancellationTokenSource? _sizesCancellation;

    public event Action<IReadOnlyList<ProgramItem>>? ActionRequested;
    public IReadOnlyList<ProgramItem> AllItems => _all;

    public string SearchHint => _cfg.SearchHint;

    public ListPage(ListPageConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        _tab = cfg.Tabs[0];
        TitleText.Text = cfg.Title;
        SummaryGlyph.Text = cfg.Glyph;
        CountLabel.Text = cfg.Noun + " gefunden";
        ActionBtn.Content = cfg.ActionText;
        FilterBtn.Visibility = cfg.ShowDriveFilter ? Visibility.Visible : Visibility.Collapsed;
        List.ItemsSource = _view;

        if (cfg.Tabs.Length > 1)
            foreach (var t in cfg.Tabs)
            {
                var rb = new RadioButton { Content = t.Label, Style = (Style)FindResource("TabPill"), GroupName = "tab" + GetHashCode(), IsChecked = t == _tab, Tag = t };
                rb.Checked += (_, _) => { _tab = (ListTab)rb.Tag; Refresh(); };
                TabsPanel.Children.Add(rb);
            }
    }

    public async Task LoadAsync()
    {
        // Nur abbrechen; der alte Task verwendet sein Token noch bis zur Beendigung.
        _sizesCancellation?.Cancel();
        _sizesCancellation = null;
        LoadingText.Visibility = Visibility.Visible;
        EmptyPanel.Visibility = Visibility.Collapsed;
        _view.Clear();
        try
        {
            var items = await _cfg.Load();
            _all.Clear();
            _all.AddRange(items);
            _loaded = true;
        }
        catch (Exception ex)
        {
            _all.Clear();
            EmptyText.Text = "Laden fehlgeschlagen: " + ex.Message;
        }
        LoadingText.Visibility = Visibility.Collapsed;
        Refresh();
        if (_all.Any(i => i.Kind == ItemKind.Win32))
        {
            _sizesCancellation = new CancellationTokenSource();
            _ = ProgramScanner.FillSizesAsync(_all, _sizesCancellation.Token);
        }
    }

    public void Search(string text)
    {
        _search = text.Trim();
        if (_loaded) Refresh();
    }

    public void ReloadSettingsFilters() { if (_loaded) Refresh(); }

    public IReadOnlyList<ProgramItem> Checked => _all.Where(i => i.IsChecked).ToList();

    void Refresh()
    {
        IEnumerable<ProgramItem> q = _all.Where(_tab.Match);
        if (_cfg.ShowRuntimeToggle && !Settings.Current.ShowSystemComponents) q = q.Where(i => !i.IsRuntime);
        if (_drive.Length > 0) q = q.Where(i => i.InstallLocation.StartsWith(_drive, StringComparison.OrdinalIgnoreCase));
        if (_search.Length > 0) q = q.Where(i => i.Name.Contains(_search, StringComparison.CurrentCultureIgnoreCase) || i.Publisher.Contains(_search, StringComparison.CurrentCultureIgnoreCase));
        q = _sort switch
        {
            "Größe" => q.OrderByDescending(i => i.SizeBytes),
            "Installationsdatum" => q.OrderByDescending(i => i.InstallDate ?? DateTime.MinValue),
            _ => q.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        var list = q.ToList();
        _view.Clear();
        foreach (var i in list) _view.Add(i);

        CountText.Text = list.Count.ToString();
        RecentList.ItemsSource = _all.OrderByDescending(i => i.InstallDate ?? DateTime.MinValue).Take(3)
            .Select(i => new { i.Name, DateText = RelDate(i.InstallDate) }).ToList();

        bool empty = list.Count == 0 && _loaded;
        EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty) EmptyText.Text = _tab.EmptyText ?? "Keine Einträge gefunden.";
        UpdateDetail();
    }

    static string RelDate(DateTime? d)
    {
        if (d == null) return "–";
        var days = (DateTime.Today - d.Value.Date).Days;
        return days <= 0 ? "Heute" : days == 1 ? "Gestern" : d.Value.ToString("dd.MM.yyyy");
    }

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDetail();

    void UpdateDetail()
    {
        if (List.SelectedItem is not ProgramItem p)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            SummaryPanel.Visibility = Visibility.Visible;
            return;
        }
        SummaryPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        DetailIcon.Source = p.Icon;
        DetailName.Text = p.Name;
        DetailSize.Text = p.SizeText;
        DetailVersion.Text = p.Version.Length > 0 ? p.Version : "–";
        DetailPublisher.Text = p.Publisher.Length > 0 ? p.Publisher : "–";
        DetailPath.Text = p.InstallLocation.Length > 0 ? p.InstallLocation : "–";
        DetailDate.Text = p.DateText;
        var logVis = p.HasLog ? Visibility.Visible : Visibility.Collapsed;
        DetailLogLabel.Visibility = logVis;
        DetailLog.Visibility = logVis;
        DetailLog.Text = $"{p.LogItemCount} Elemente protokolliert";
    }

    void Filter_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        if (_cfg.ShowRuntimeToggle)
        {
            var rt = new MenuItem { Header = (Settings.Current.ShowSystemComponents ? "✓  " : "      ") + "Zeige System-Laufzeitkomponenten", Tag = "" };
            rt.Click += (_, _) => { Settings.Current.ShowSystemComponents = !Settings.Current.ShowSystemComponents; Settings.Save(); Refresh(); };
            menu.Items.Add(rt);
            menu.Items.Add(new Separator());
        }
        void Add(string header, string drive)
        {
            var mi = new MenuItem { Header = (_drive == drive ? "✓  " : "      ") + header, Tag = "" };
            mi.Click += (_, _) => { _drive = drive; FilterLabel.Text = header; Refresh(); };
            menu.Items.Add(mi);
        }
        Add("Alle Laufwerke", "");
        foreach (var d in _all.Select(i => Path.GetPathRoot(i.InstallLocation)).Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => r))
            Add(d!, d!);
        Open(menu, FilterBtn);
    }

    void Sort_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var s in new[] { "Bezeichnung", "Größe", "Installationsdatum" })
        {
            var mi = new MenuItem { Header = (_sort == s ? "✓  " : "      ") + s, Tag = "" };
            mi.Click += (_, _) => { _sort = s; SortLabel.Text = s; Refresh(); };
            menu.Items.Add(mi);
        }
        Open(menu, SortBtn);
    }

    static void Open(ContextMenu menu, FrameworkElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void RowAction_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is ProgramItem p) ActionRequested?.Invoke(new[] { p });
    }

    void Action_Click(object sender, RoutedEventArgs e)
    {
        var chosen = Checked;
        if (chosen.Count == 0 && List.SelectedItem is ProgramItem sel) chosen = new[] { sel };
        if (chosen.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "Bitte zuerst mindestens einen Eintrag ankreuzen.", "Keine Auswahl", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ActionRequested?.Invoke(chosen);
    }
}

