using System.Windows;
using System.Windows.Controls;

namespace ModernUninstaller.Pages;

public partial class ToolboxPage : UserControl, IPage
{
    public string SearchHint => "Toolbox";
    public event Action<string>? ToolRequested;

    public ToolboxPage()
    {
        InitializeComponent();
        // (Schlüssel, Glyph, Beschriftung, verfügbar)
        var tools = new (string key, string glyph, string label, bool ready)[]
        {
            ("monitor",  "", "Installations-Monitor", true),
            ("updater",  "", "Software-Updater", true),
            ("easy",     "", "Einfache Deinstallation", true),
            ("force",    "", "Erzwungene Deinstallation", true),
            ("stubborn", "", "Entferner für hartnäckige Programme", false),
            ("shredder", "", "Datei-Shredder", true),
            ("mover",    "", "Software-Verschieber", false),
            ("bloat",    "", "Bloatware-Entferner", true),
            ("winupd",   "", "Windows-Update-Deinstallation", true),
            ("history",  "", "Deinstallationsverlauf", true),
        };
        foreach (var (key, glyph, label, ready) in tools)
        {
            var b = new Button
            {
                Style = (Style)FindResource("ToolTile"),
                Tag = glyph,
                Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                IsEnabled = ready,
                ToolTip = ready ? null : "Noch nicht umgesetzt (geplant)",
            };
            ToolTipService.SetShowOnDisabled(b, true);
            b.Click += (_, _) => ToolRequested?.Invoke(key);
            Tiles.Children.Add(b);
        }
    }

    public Task LoadAsync() => Task.CompletedTask;
    public void Search(string text) { }
}
