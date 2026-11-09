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
