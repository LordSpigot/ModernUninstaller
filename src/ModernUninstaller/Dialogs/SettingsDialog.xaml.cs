using System.Windows;
using ModernUninstaller.Services;

namespace ModernUninstaller.Dialogs;

public partial class SettingsDialog : DialogWindow
{
    static readonly int[] Months = { 1, 2, 3, 6, 12 };
    static readonly int[] Sizes = { 100, 250, 500, 1000, 2000 };

    public SettingsDialog()
    {
        InitializeComponent();
        var s = Settings.Current;

        (s.Theme switch { "Light" => ThemeLight, "System" => ThemeSystem, _ => ThemeDark }).IsChecked = true;
        (s.FontScaleIndex switch { 0 => FontS, 2 => FontL, _ => FontM }).IsChecked = true;

        foreach (var m in Months) MonthsBox.Items.Add(m == 1 ? "1 Monat" : $"{m} Monaten");
        MonthsBox.SelectedIndex = Math.Max(0, Array.IndexOf(Months, s.RarelyUsedMonths));
        foreach (var z in Sizes) SizeBox.Items.Add(z >= 1000 ? $"{z / 1000.0:0.#} GB" : $"{z} MB");
        SizeBox.SelectedIndex = Math.Max(0, Array.IndexOf(Sizes, s.OversizedMb));

        ShowRuntime.IsChecked = s.ShowSystemComponents;
        RestoreCheck.IsChecked = s.CreateRestorePoint;
        QuickCheck.IsChecked = s.QuickUninstall;
        AutoCheck.IsChecked = s.AutoRemoveLeftovers;
        RecycleCheck.IsChecked = s.LeftoversToRecycleBin;
    }

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelUi == null) return;
        PanelUi.Visibility = TabUi.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelGeneral.Visibility = TabGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelUninstall.Visibility = TabUninstall.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        var s = Settings.Current;
        s.Theme = ThemeLight.IsChecked == true ? "Light" : ThemeSystem.IsChecked == true ? "System" : "Dark";
        s.FontScaleIndex = FontS.IsChecked == true ? 0 : FontL.IsChecked == true ? 2 : 1;
        s.RarelyUsedMonths = Months[Math.Max(0, MonthsBox.SelectedIndex)];
        s.OversizedMb = Sizes[Math.Max(0, SizeBox.SelectedIndex)];
        s.ShowSystemComponents = ShowRuntime.IsChecked == true;
        s.CreateRestorePoint = RestoreCheck.IsChecked == true;
        s.QuickUninstall = QuickCheck.IsChecked == true;
        s.AutoRemoveLeftovers = AutoCheck.IsChecked == true;
        s.LeftoversToRecycleBin = RecycleCheck.IsChecked == true;
        Settings.Save();
        CloseDialog(true);
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => CloseDialog(false);
}
