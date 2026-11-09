using System.Runtime.InteropServices;

namespace ModernUninstaller.Services;

static class Native
{
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool MoveFileEx(string existing, string? newName, int flags);

    public const int MOVEFILE_DELAY_UNTIL_REBOOT = 4;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    static Dictionary<int, (int parent, string exe)> Snapshot()
    {
        var map = new Dictionary<int, (int, string)>();
        var snap = CreateToolhelp32Snapshot(2, 0);
        if (snap != IntPtr.Zero && snap != new IntPtr(-1))
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            for (bool ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                map[(int)e.th32ProcessID] = ((int)e.th32ParentProcessID, e.szExeFile ?? "");
            CloseHandle(snap);
        }
        return map;
    }

    /// <summary>Alle (auch indirekten) Kindprozesse von <paramref name="root"/>, inklusive root.</summary>
    public static HashSet<int> ProcessTree(int root)
    {
        var set = new HashSet<int> { root };
        set.UnionWith(LiveTree(root));
        return set;
    }

    // Browser, die ein Uninstaller für eine Umfrage öffnet, zählen nicht als "Deinstallation läuft noch".
    static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "iexplore.exe", "msedgewebview2.exe",
    };

    /// <summary>Noch laufende Prozesse im Baum von <paramref name="root"/> (root nur, wenn er noch läuft), ohne Browser.</summary>
    public static HashSet<int> LiveTree(int root)
    {
        var map = Snapshot();
        var set = new HashSet<int>();
        if (map.ContainsKey(root)) set.Add(root);
        var reach = new HashSet<int> { root };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var (pid, (ppid, exe)) in map)
            {
                if (reach.Contains(pid) || !reach.Contains(ppid) || pid == ppid) continue;
                reach.Add(pid); grew = true;
                if (!Browsers.Contains(exe)) set.Add(pid);
            }
        }
        return set;
    }
}
