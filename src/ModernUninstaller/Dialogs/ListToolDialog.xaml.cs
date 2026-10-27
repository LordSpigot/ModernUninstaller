using System.ComponentModel;
using System.Windows;

namespace ModernUninstaller.Dialogs;

public sealed class ToolRow : INotifyPropertyChanged
{
    bool _checked;
    public string Title { get; init; } = "";
    public string Sub { get; init; } = "";
    public object? Tag { get; init; }
    public bool ShowCheck { get; init; } = true;
    public Visibility CheckVisibility => ShowCheck ? Visibility.Visible : Visibility.Collapsed;
    public bool IsChecked { get => _checked; set { _checked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Allgemeiner Listen-Dialog für Toolbox-Werkzeuge: lädt Zeilen, führt eine Aktion für die angehakten aus.</summary>
public partial class ListToolDialog : DialogWindow
{
    readonly Func<IReadOnlyList<ToolRow>, IProgress<string>, Task<string>>? _action;
    readonly Func<Task<List<ToolRow>>> _load;
    readonly string _emptyText;

    public ListToolDialog(string title, string intro, string actionText, string emptyText,
        Func<Task<List<ToolRow>>> load, Func<IReadOnlyList<ToolRow>, IProgress<string>, Task<string>>? action)
    {
        InitializeComponent();
        Title = title;
        Heading.Text = title;
        Intro.Text = intro;
        _load = load; _action = action; _emptyText = emptyText;
        ActionBtn.Content = actionText;
        ActionBtn.Visibility = action == null ? Visibility.Collapsed : Visibility.Visible;
        Loaded += async (_, _) => await ReloadAsync();
    }

    async Task ReloadAsync()
    {
        Placeholder.Visibility = Visibility.Visible;
        Placeholder.Text = "Lade …";
        Rows.ItemsSource = null;
        try
        {
            var rows = await _load();
            Rows.ItemsSource = rows;
            Placeholder.Text = _emptyText;
            Placeholder.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Status.Text = rows.Count == 0 ? "" : $"{rows.Count} Einträge";
        }
        catch (Exception ex) { Placeholder.Text = "Laden fehlgeschlagen: " + ex.Message; }
    }

    async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_action == null || Rows.ItemsSource is not List<ToolRow> rows) return;
        var chosen = rows.Where(r => r.IsChecked).ToList();
        if (chosen.Count == 0) { Status.Text = "Bitte zuerst Einträge ankreuzen."; return; }
        if (MessageBox.Show(this, $"{chosen.Count} Eintrag/Einträge verarbeiten?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ActionBtn.IsEnabled = false;
        try
        {
            Status.Text = await _action(chosen, new Progress<string>(s => Status.Text = s));
            await ReloadAsync();
        }
        finally { ActionBtn.IsEnabled = true; }
    }

    void Close_Click(object sender, RoutedEventArgs e) => CloseDialog(false);
}
