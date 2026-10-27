using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;
using ModernUninstaller.Services;

namespace ModernUninstaller.Dialogs;

public partial class ShredderDialog : DialogWindow
{
    readonly ObservableCollection<string> _paths = new();

    public ShredderDialog()
    {
        InitializeComponent();
        PathList.ItemsSource = _paths;
        _paths.CollectionChanged += (_, _) => DropHint.Visibility = _paths.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Add(IEnumerable<string> paths)
    {
        foreach (var p in paths) if (!_paths.Contains(p, StringComparer.OrdinalIgnoreCase)) _paths.Add(p);
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) Add(files);
    }

    void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Title = "Dateien zum Vernichten wählen" };
        if (dlg.ShowDialog(this) == true) Add(dlg.FileNames);
    }

    void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Multiselect = true, Title = "Ordner zum Vernichten wählen" };
        if (dlg.ShowDialog(this) == true) Add(dlg.FolderNames);
    }

    void Clear_Click(object sender, RoutedEventArgs e) => _paths.Clear();

    async void Shred_Click(object sender, RoutedEventArgs e)
    {
        if (_paths.Count == 0) { Status.Text = "Die Liste ist leer."; return; }
        if (MessageBox.Show(this, $"{_paths.Count} Element(e) unwiderruflich vernichten?\nDas lässt sich nicht rückgängig machen.", "Datei-Shredder",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var mode = ModeBox.SelectedIndex switch { 0 => ShredMode.Quick, 2 => ShredMode.Professional, _ => ShredMode.Advanced };
        ShredBtn.IsEnabled = false;
        try
        {
            var (ok, failed) = await Shredder.ShredAsync(_paths.ToList(), mode, new Progress<string>(s => Status.Text = s), CancellationToken.None);
            _paths.Clear();
            Status.Text = $"{ok} Dateien vernichtet" + (failed > 0 ? $", {failed} fehlgeschlagen (in Benutzung oder keine Rechte)." : ".");
        }
        finally { ShredBtn.IsEnabled = true; }
    }

    void Close_Click(object sender, RoutedEventArgs e) => CloseDialog(false);
}
