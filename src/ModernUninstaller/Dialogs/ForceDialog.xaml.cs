using System.IO;
using System.Windows;
using Microsoft.Win32;
using ModernUninstaller.Models;

namespace ModernUninstaller.Dialogs;

public partial class ForceDialog : DialogWindow
{
    readonly IReadOnlyList<ProgramItem> _programs;

    public ProgramItem? Result { get; private set; }
    public bool Forced { get; private set; }

    ProgramItem? _match;

    public ForceDialog(IReadOnlyList<ProgramItem> programs)
    {
        InitializeComponent();
        _programs = programs;
        NameBox.PreviewKeyDown += (_, _) => NameBox.Tag = null;
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) PathBox.Text = Resolve(files[0]);
    }

    /// <summary>Verknüpfungen (.lnk) auf ihr Ziel auflösen.</summary>
    static string Resolve(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return path;
        try
        {
            var shell = Type.GetTypeFromProgID("WScript.Shell");
            if (shell == null) return path;
            dynamic sh = Activator.CreateInstance(shell)!;
            string target = sh.CreateShortcut(path).TargetPath;
            return string.IsNullOrEmpty(target) ? path : target;
        }
        catch { return path; }
    }

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Programmordner wählen" };
        if (dlg.ShowDialog(this) == true) PathBox.Text = dlg.FolderName;
    }

    void File_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Programmdatei wählen", Filter = "Programme und Verknüpfungen|*.exe;*.lnk|Alle Dateien|*.*" };
        if (dlg.ShowDialog(this) == true) PathBox.Text = Resolve(dlg.FileName);
    }

    void PathBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var path = PathBox.Text.Trim().Trim('"');
        _match = null;
        if (path.Length < 4) { MatchText.Text = "Noch nichts ausgewählt."; Buttons(false); return; }

        _match = _programs
            .Where(p => p.Kind == ItemKind.Win32 && p.InstallLocation.Length > 3 &&
                        (path.StartsWith(p.InstallLocation.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(p.InstallLocation.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(p => p.InstallLocation.Length)
            .FirstOrDefault();

        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        if (NameBox.Text.Length == 0 || NameBox.Tag as string == "auto")
        {
            NameBox.Text = _match?.Name ?? new DirectoryInfo(folder).Name;
            NameBox.Tag = "auto";
        }
        MatchText.Text = _match != null
            ? $"Passender Eintrag: {_match.Name} {_match.Version}\nInstalliert in {_match.InstallLocation}"
            : "Kein installierter Eintrag zu diesem Pfad gefunden. Es können nur Reste erzwungen entfernt werden.";
        Buttons(true);
    }

    void Buttons(bool pathOk)
    {
        NormalBtn.IsEnabled = pathOk && _match != null;
        ForceBtn.IsEnabled = pathOk;
    }

    void Normal_Click(object sender, RoutedEventArgs e)
    {
        Result = _match; Forced = false;
        CloseDialog(true);
    }

    void Force_Click(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim().Trim('"');
        var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
        var name = NameBox.Text.Trim();
        Result = _match ?? new ProgramItem { Id = "forced", Name = name.Length > 0 ? name : new DirectoryInfo(folder).Name, InstallLocation = folder };
        if (_match == null && name.Length > 0) Result = new ProgramItem { Id = "forced", Name = name, InstallLocation = folder };
        Forced = true;
        CloseDialog(true);
    }

    void Close_Click(object sender, RoutedEventArgs e) => CloseDialog(false);
}
