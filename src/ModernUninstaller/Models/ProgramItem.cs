using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace ModernUninstaller.Models;

public enum ItemKind { Win32, Appx, Extension }

public sealed class ProgramItem : INotifyPropertyChanged
{
    long _size;
    bool _isChecked;

    public ItemKind Kind { get; init; } = ItemKind.Win32;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    public DateTime? InstallDate { get; init; }
    public DateTime? LastUsed { get; set; }
    public string UninstallString { get; init; } = "";
    public string QuietUninstallString { get; init; } = "";
    public string RegistryPath { get; init; } = "";
    public bool Is64Bit { get; init; }
    public bool IsRuntime { get; init; }
    public bool IsMicrosoft { get; init; }
    public bool IsBundleware { get; set; }
    public bool HasLog { get; set; }
    public int LogItemCount { get; set; }
    public string Source { get; init; } = "";
    public ImageSource? Icon { get; set; }

    public long SizeBytes
    {
        get => _size;
        set { if (_size == value) return; _size = value; OnChanged(); OnChanged(nameof(SizeText)); }
    }

    public bool IsChecked
    {
        get => _isChecked;
        set { if (_isChecked == value) return; _isChecked = value; OnChanged(); }
    }

    public string SizeText => SizeBytes <= 0 ? "–" : FormatSize(SizeBytes);
    public string DateText => InstallDate?.ToString("dd.MM.yyyy") ?? "–";
    public string Arch => Is64Bit ? "64 bit" : "32 bit";

    public static string FormatSize(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return (i <= 1 ? v.ToString("0") : v.ToString("0.0")) + " " + u[i];
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public enum LeftoverKind { File, Folder, RegistryKey, RegistryValue, Task, Service, Dll, EnvPath }

public sealed class Leftover : INotifyPropertyChanged
{
    bool _isChecked = true;
    public LeftoverKind Kind { get; init; }
    public string Path { get; init; } = "";
    public string ValueName { get; init; } = "";
    /// <summary>Bei EnvPath: der zu entfernende Eintrag der PATH-Variable.</summary>
    public string Detail { get; init; } = "";
    public bool IsPersonalData { get; init; }
    public bool IsChecked
    {
        get => _isChecked;
        set { if (_isChecked == value) return; _isChecked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); }
    }

    public string Category => Kind switch
    {
        LeftoverKind.File or LeftoverKind.Folder => "Dateien",
        LeftoverKind.RegistryKey or LeftoverKind.RegistryValue or LeftoverKind.EnvPath => "Registry",
        LeftoverKind.Task => "Aufgaben",
        LeftoverKind.Service => "Dienste",
        _ => "DLLs",
    };

    public string Display => Kind == LeftoverKind.EnvPath ? $"PATH-Eintrag: {Detail}   ({Path})" : string.IsNullOrEmpty(ValueName) ? Path : Path + "  →  " + ValueName;

    public event PropertyChangedEventHandler? PropertyChanged;
}


