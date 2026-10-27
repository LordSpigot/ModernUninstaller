using System.Windows;
using ModernUninstaller.Models;
using ModernUninstaller.Services;

namespace ModernUninstaller.Dialogs;

public partial class ConfirmDialog : DialogWindow
{
    public bool CreateRestorePoint => RestoreCheck.IsChecked == true;
    public bool AutoLeftovers => LeftoverCheck.IsChecked == true;

    public ConfirmDialog(IReadOnlyList<ProgramItem> items, string question, string okText, bool showOptions)
    {
        InitializeComponent();
        Question.Text = question;
        OkBtn.Content = okText;
        Options.Visibility = showOptions ? Visibility.Visible : Visibility.Collapsed;
        RestoreCheck.IsChecked = Settings.Current.CreateRestorePoint;
        LeftoverCheck.IsChecked = Settings.Current.AutoRemoveLeftovers;

        if (items.Count == 1)
        {
            AppIcon.Source = items[0].Icon;
            NameText.Text = items[0].Name;
            SubText.Text = items[0].SizeText;
        }
        else
        {
            NameText.Text = string.Join(", ", items.Take(4).Select(i => i.Name)) + (items.Count > 4 ? $" und {items.Count - 4} weitere" : "");
            SubText.Text = $"{items.Count} Einträge · {ProgramItem.FormatSize(items.Sum(i => i.SizeBytes))}";
        }
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => CloseDialog(false);
    void Ok_Click(object sender, RoutedEventArgs e) => CloseDialog(true);
}

