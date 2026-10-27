using System.IO;
using System.Text.Json;

namespace ModernUninstaller.Services;

public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";          // Dark | Light | System
    public int FontScaleIndex { get; set; } = 1;         // 0 klein, 1 normal, 2 groß
    public bool ShowSystemComponents { get; set; } = true;
    public int RarelyUsedMonths { get; set; } = 2;
    public int OversizedMb { get; set; } = 500;
    public bool CreateRestorePoint { get; set; }
    public bool QuickUninstall { get; set; }
    public bool AutoRemoveLeftovers { get; set; }
    public bool LeftoversToRecycleBin { get; set; } = true;
}

public static class Settings
{
    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModernUninstaller");
    static string FilePath => Path.Combine(DataDir, "settings.json");

    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { Current = new AppSettings(); }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* Einstellungen sind nicht kritisch */ }
    }
}
