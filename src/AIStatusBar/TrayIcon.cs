using System.Runtime.InteropServices;

namespace AIStatusBar;

/// <summary>Notification-area icon (Shell_NotifyIcon) with a click/menu handler and balloon popups.</summary>
sealed class TrayIcon : IDisposable
{
    const uint WM_APP_TRAY = 0x8000 + 1;
    const int WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, NIN_BALLOONUSERCLICK = 0x405;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    const uint NIIF_WARNING = 2;
    const int IdToggle = 1, IdQuit = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA d);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClass(ref WNDCLASS c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr m, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] static extern int TrackPopupMenu(IntPtr m, uint flags, int x, int y, int res, IntPtr h, IntPtr rect);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr m);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Native.POINT p);
    [DllImport("user32.dll")] static extern IntPtr CreateIconIndirect(ref ICONINFO i);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("gdi32.dll")] static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, byte[] bits);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);

    readonly WndProc proc; // kept alive for the native window
    readonly IntPtr hwnd;
    readonly Dictionary<SessState, IntPtr> icons = new();
    bool added;

    public bool BarVisible = true; // drives the menu label

    public event Action? Activated;   // left click
    public event Action? BalloonClicked;
    public event Action? QuitRequested;

    public TrayIcon()
    {
        proc = OnMessage;
        var cls = "AIStatusBarTray";
        var wc = new WNDCLASS { lpfnWndProc = proc, hInstance = GetModuleHandle(null), lpszClassName = cls };
        RegisterClass(ref wc);
        hwnd = CreateWindowEx(0, cls, cls, 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, wc.hInstance, IntPtr.Zero); // HWND_MESSAGE
        foreach (var (s, c) in new[] {
            (SessState.Working, 0x4C9AFF), (SessState.Waiting, 0xFF5630), (SessState.Idle, 0x36B37E) })
            icons[s] = MakeDotIcon(c);
    }

    static IntPtr MakeDotIcon(int rgb)
    {
        const int n = 16;
        var px = new byte[n * n * 4];
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var dx = x - 7.5; var dy = y - 7.5;
                if (dx * dx + dy * dy > 6.5 * 6.5) continue;
                var i = (y * n + x) * 4;
                px[i] = (byte)(rgb & 255); px[i + 1] = (byte)((rgb >> 8) & 255); px[i + 2] = (byte)((rgb >> 16) & 255); px[i + 3] = 255;
            }
        var color = CreateBitmap(n, n, 1, 32, px);
        var mask = CreateBitmap(n, n, 1, 1, new byte[n * 2]);
        var ii = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
        var icon = CreateIconIndirect(ref ii);
        DeleteObject(color);
        DeleteObject(mask);
        return icon;
    }

    NOTIFYICONDATA Data(SessState state, string tip, uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = hwnd,
        uID = 1,
        uFlags = flags | NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_APP_TRAY,
        hIcon = icons[state],
        szTip = tip.Length > 127 ? tip[..127] : tip,
        szInfo = "",
        szInfoTitle = "",
    };

    public void Show(SessState state, string tip)
    {
        var d = Data(state, tip, 0);
        Shell_NotifyIcon(added ? NIM_MODIFY : NIM_ADD, ref d);
        added = true;
    }

    public void Hide()
    {
        if (!added) return;
        var d = Data(SessState.Idle, "", 0);
        Shell_NotifyIcon(NIM_DELETE, ref d);
        added = false;
    }

    public void Balloon(SessState state, string title, string text)
    {
        if (!added) return;
        var d = Data(state, title, NIF_INFO);
        d.szInfoTitle = title.Length > 63 ? title[..63] : title;
        d.szInfo = text.Length > 255 ? text[..255] : text;
        d.dwInfoFlags = NIIF_WARNING;
        Shell_NotifyIcon(NIM_MODIFY, ref d);
    }

    IntPtr OnMessage(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == WM_APP_TRAY)
        {
            switch ((int)(l.ToInt64() & 0xFFFF))
            {
                case WM_LBUTTONUP: Activated?.Invoke(); break;
                case WM_RBUTTONUP: ShowMenu(); break;
                case NIN_BALLOONUSERCLICK: BalloonClicked?.Invoke(); break;
            }
            return IntPtr.Zero;
        }
        return DefWindowProc(h, msg, w, l);
    }

    void ShowMenu()
    {
        var m = CreatePopupMenu();
        AppendMenu(m, 0, new UIntPtr(IdToggle), BarVisible ? "Minimize to tray" : "Show bar");
        AppendMenu(m, 0x800, UIntPtr.Zero, null); // MF_SEPARATOR
        AppendMenu(m, 0, new UIntPtr(IdQuit), "Quit");
        GetCursorPos(out var p);
        SetForegroundWindow(hwnd); // required so the menu closes when clicking elsewhere
        var cmd = TrackPopupMenu(m, 0x0100 | 0x0020, p.X, p.Y, 0, hwnd, IntPtr.Zero); // TPM_RETURNCMD | TPM_RIGHTBUTTON
        DestroyMenu(m);
        if (cmd == IdToggle) Activated?.Invoke();
        else if (cmd == IdQuit) QuitRequested?.Invoke();
    }

    public void Dispose()
    {
        Hide();
        foreach (var i in icons.Values) DestroyIcon(i);
        icons.Clear();
        if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
    }
}
