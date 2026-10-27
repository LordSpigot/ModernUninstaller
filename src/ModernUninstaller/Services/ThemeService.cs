using System.Windows;
using Microsoft.Win32;

namespace ModernUninstaller.Services;

public static class ThemeService
{
    static ResourceDictionary? _current;

    public static bool IsDark { get; private set; } = true;

    public static void Apply(string theme)
    {
        IsDark = theme switch
        {
            "Light" => false,
            "System" => !SystemUsesLightTheme(),
            _ => true,
        };

        var dict = new ResourceDictionary { Source = new Uri($"Themes/{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Relative) };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_current != null) merged.Remove(_current);
        merged.Add(dict);
        _current = dict;
    }

    static bool SystemUsesLightTheme()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v ? v != 0 : false;
        }
        catch { return false; }
    }

    public static double FontScale(int index) => index switch { 0 => 0.92, 2 => 1.12, _ => 1.0 };
}
