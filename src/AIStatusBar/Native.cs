using System.Runtime.InteropServices;
using System.Text;

namespace AIStatusBar;

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

    [DllImport("kernel32.dll")] public static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] static extern bool FreeConsole();
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, ref RECT r, uint winIni);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int idx);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    /// <summary>Is the process still running? Access denied counts as alive (exists, not ours).</summary>
    public static bool PidAlive(int pid)
    {
        var h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5;
        try { return GetExitCodeProcess(h, out var code) && code == 259; } // STILL_ACTIVE
        finally { CloseHandle(h); }
    }

    /// <summary>(left, top, right, bottom, trayLeft) of the primary taskbar, or null.</summary>
    public static (int L, int T, int R, int B, int TrayLeft)? TaskbarGeometry()
    {
        var tb = FindWindow("Shell_TrayWnd", null);
        if (tb == IntPtr.Zero || !GetWindowRect(tb, out var r)) return null;
        var trayLeft = r.Right;
        var tray = FindWindowEx(tb, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray != IntPtr.Zero && GetWindowRect(tray, out var tr)) trayLeft = tr.Left;
        return (r.Left, r.Top, r.Right, r.Bottom, trayLeft);
    }

    /// <summary>True when the active window covers its whole monitor (game, video, ...).</summary>
    public static bool FullscreenForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        var cls = new StringBuilder(64);
        GetClassName(hwnd, cls, 64);
        if (cls.ToString() is "Shell_TrayWnd" or "WorkerW" or "Progman") return false; // desktop / taskbar itself
        if (!GetWindowRect(hwnd, out var r)) return false;
        var mon = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return false;
        var m = mi.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    public static (int L, int T, int R, int B) WorkArea()
    {
        var r = new RECT();
        SystemParametersInfo(0x0030, 0, ref r, 0); // SPI_GETWORKAREA
        return (r.Left, r.Top, r.Right, r.Bottom);
    }

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lp);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32FirstW(IntPtr s, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32NextW(IntPtr s, ref PROCESSENTRY32 e);
    delegate bool EnumProc(IntPtr h, IntPtr lp);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    static Dictionary<uint, uint> ParentMap()
    {
        var map = new Dictionary<uint, uint>();
        var snap = CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        if (snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            for (var ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                map[e.th32ProcessID] = e.th32ParentProcessID;
        }
        finally { CloseHandle(snap); }
        return map;
    }

    /// <summary>Bring the terminal window hosting <paramref name="pid"/> (or its nearest ancestor with a window) to the front.</summary>
    public static bool FocusProcessWindow(int pid)
    {
        var parents = ParentMap();
        var chain = new List<uint>();
        var cur = (uint)pid;
        while (cur != 0 && !chain.Contains(cur) && chain.Count < 32)
        {
            chain.Add(cur);
            if (!parents.TryGetValue(cur, out cur)) break;
        }

        var wins = new Dictionary<uint, IntPtr>();
        EnumWindows((h, _) =>
        {
            if (GetWindow(h, 4) != IntPtr.Zero || !IsWindowVisible(h)) return true; // skip owned / hidden
            GetWindowThreadProcessId(h, out var wp);
            if (!wins.ContainsKey(wp)) wins[wp] = h;
            return true;
        }, IntPtr.Zero);

        foreach (var p in chain)
        {
            if (!wins.TryGetValue(p, out var h)) continue;
            if (IsIconic(h)) ShowWindow(h, 9); // SW_RESTORE
            keybd_event(0x12, 0, 0, UIntPtr.Zero); // ALT tap unlocks SetForegroundWindow
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            return SetForegroundWindow(h);
        }
        if (!AttachConsole(pid)) return false;
        try
        {
            var h = GetConsoleWindow();
            if (h == IntPtr.Zero || !IsWindowVisible(h)) return false;
            if (IsIconic(h)) ShowWindow(h, 9); // SW_RESTORE
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            return SetForegroundWindow(h);
        }
        finally { FreeConsole(); }
    }

    public static void ForceTopmost(IntPtr hwnd) =>
        SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // TOPMOST, NOSIZE|NOMOVE|NOACTIVATE

    /// <summary>Make the window a no-activate tool window (no taskbar button, never steals focus).</summary>
    public static void MakeToolWindow(IntPtr hwnd)
    {
        var ex = GetWindowLongPtr(hwnd, -20).ToInt64();
        SetWindowLongPtr(hwnd, -20, new IntPtr(ex | 0x80 | 0x08000000)); // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE

                // drop the resize frame (it steals client area) and the Windows 11 accent border
                var st = GetWindowLongPtr(hwnd, -16).ToInt64();
                SetWindowLongPtr(hwnd, -16, new IntPtr(st & ~0x00C40000L)); // WS_CAPTION | WS_THICKFRAME
                var none = 0xFFFFFFFE; // DWMWA_COLOR_NONE
                DwmSetWindowAttribute(hwnd, 34, ref none, 4); // DWMWA_BORDER_COLOR
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x27); // NOSIZE|NOMOVE|NOZORDER|FRAMECHANGED
    }

            [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref uint val, int size);

    /// <summary>Average screen colour in a ring just outside the bar, so the bar's own pixels are never read.
    /// With beside=true only points left of the bar are used (taskbar mode).</summary>
    public static (double R, double G, double B)? SampleBehind(int x, int y, int w, int h, bool beside)
    {
        var vx = GetSystemMetrics(76); var vy = GetSystemMetrics(77);
        var vw = GetSystemMetrics(78); var vh = GetSystemMetrics(79);
        const int gap = 5;
        var pts = new List<(int, int)>();
        if (beside)
        {
            for (var i = 1; i < 4; i++)
            {
                var fy = y + h * i / 4;
                for (var j = 1; j < 4; j++) pts.Add((x - gap - 4 * j, fy));
            }
        }
        else
        {
            for (var i = 1; i < 6; i++)
            {
                int fx = x + w * i / 6, fy = y + h * i / 6;
                pts.Add((fx, y - gap)); pts.Add((fx, y + h + gap));
                pts.Add((x - gap, fy)); pts.Add((x + w + gap, fy));
            }
        }
        pts = pts.Where(p => p.Item1 >= vx && p.Item1 < vx + vw && p.Item2 >= vy && p.Item2 < vy + vh).ToList();
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        List<uint> cols;
        try { cols = pts.Select(p => GetPixel(dc, p.Item1, p.Item2)).ToList(); }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        cols = cols.Where(c => c != 0xFFFFFFFF).ToList();
        if (cols.Count == 0) return null;
        return (cols.Average(c => (double)(c & 255)),
                cols.Average(c => (double)((c >> 8) & 255)),
                cols.Average(c => (double)((c >> 16) & 255)));
    }
}
