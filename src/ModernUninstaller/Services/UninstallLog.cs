using System.IO;

namespace ModernUninstaller.Services;

/// <summary>Protokoll aller Deinstallationsschritte: %LocalAppData%\ModernUninstaller\uninstall.log (für die Fehlersuche).</summary>
public static class UninstallLog
{
    static readonly object Gate = new();
    public static string FilePath => Path.Combine(Settings.DataDir, "uninstall.log");

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Settings.DataDir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch { /* Protokoll ist nicht kritisch */ }
        }
    }
}
