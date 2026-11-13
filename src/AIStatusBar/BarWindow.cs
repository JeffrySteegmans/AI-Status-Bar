using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace AIStatusBar;

public sealed class BarOptions
{
    public string Corner = "bc";
    public bool Adapt = true;
    public bool Taskbar;
    public bool Tray; // start minimized to the notification area
}

/// <summary>Borderless always-on-top strip: one pill per AI session.</summary>
public sealed class BarWindow : Window
{
    sealed record Theme(Color Bg, Color Fg, Color Dim, Color Wait, Color Border);

    sealed class Cell
    {
        public Border Box = null!;
        public Ellipse Dot = null!;
        public TextBlock Label = null!;
        public Button Close = null!;
    }

    static readonly Dictionary<SessState, Color> StateColors = new()
    {
        [SessState.Working] = Rgb(0x4C, 0x9A, 0xFF),
        [SessState.Waiting] = Rgb(0xFF, 0x56, 0x30),
        [SessState.Idle] = Rgb(0x36, 0xB3, 0x7E),
    };
    static readonly (double, double, double) BgDark = (0x1E, 0x1F, 0x22);

    readonly SessionStore store;
    readonly BarOptions opt;
    readonly IntPtr hwnd;
    readonly AppWindow appWindow;
    readonly Border root;
    readonly StackPanel panel;
    readonly TextBlock empty;
    readonly Dictionary<string, Cell> cells = new();
    readonly double fontSize;
    readonly Orientation orientation;

    (double R, double G, double B) cur = BgDark, target = BgDark;
    Theme? applied;
    string lastOrder = "";
    readonly TrayIcon tray = new();
    readonly Dictionary<string, SessState> prevStates = new();
    bool trayHidden;
    int notifyPid;
    bool moved, dragging, hidden;
    Native.POINT dragStart;
    PointInt32 dragOrigin;
    int curW, curH;

    static Color Rgb(double r, double g, double b) =>
        Color.FromArgb(255, (byte)Math.Clamp(Math.Round(r), 0, 255), (byte)Math.Clamp(Math.Round(g), 0, 255), (byte)Math.Clamp(Math.Round(b), 0, 255));

    static (double, double, double) Mix((double R, double G, double B) a, (double R, double G, double B) b, double t) =>
        (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    static (double R, double G, double B) ToTuple(Color c) => (c.R, c.G, c.B);

    static double Lum((double R, double G, double B) c) =>
        0.2126 * c.R / 255 + 0.7152 * c.G / 255 + 0.0722 * c.B / 255;

    public BarWindow(SessionStore store, BarOptions opt)
    {
        this.store = store;
        this.opt = opt;
        fontSize = opt.Taskbar ? 12 : 13.5;
        orientation = opt.Taskbar ? Orientation.Horizontal : Orientation.Vertical;

        Title = "AI status";
        hwnd = WindowNative.GetWindowHandle(this);
        appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        if (appWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }
        appWindow.IsShownInSwitchers = false;
        Native.MakeToolWindow(hwnd);

        panel = new StackPanel { Orientation = orientation, Spacing = 2 };
        empty = new TextBlock { Text = "no AI sessions", FontSize = fontSize, FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(8, 2, 8, 2) };
        root = new Border
        {
            Child = panel,
            Padding = opt.Taskbar ? new Thickness(6, 1, 6, 1) : new Thickness(6, 3, 6, 3),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Rgb(0x1E, 0x1F, 0x22)),
        };
        Content = root;

        var menu = new MenuFlyout();
        var reset = new MenuFlyoutItem { Text = "Reset position" };
        reset.Click += (_, _) => { moved = false; Reposition(); };
        var clear = new MenuFlyoutItem { Text = "Clear sessions" };
        clear.Click += (_, _) => store.Clear();
        var minimize = new MenuFlyoutItem { Text = "Minimize to tray" };
        minimize.Click += (_, _) => MinimizeToTray();
        var quit = new MenuFlyoutItem { Text = "Quit" };
        quit.Click += (_, _) => Quit();
        menu.Items.Add(minimize);
        menu.Items.Add(reset);
        menu.Items.Add(clear);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(quit);
        root.ContextFlyout = menu;

        root.Loaded += (_, _) => { ResizeToContent(); Reposition(); }; // re-measure once fonts/DPI are real
        root.PointerPressed += OnPress;
        root.PointerMoved += OnDrag;
        root.PointerReleased += OnRelease;
        root.PointerCanceled += OnRelease;
        root.PointerCaptureLost += OnRelease;

        panel.Children.Add(empty);
        ApplyTheme();
        ResizeToContent();
        Reposition();
        appWindow.Show(false);

        var refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        refresh.Tick += (_, _) => RefreshCells();
        refresh.Start();
        if (opt.Adapt)
        {
            var sampler = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            sampler.Tick += (_, _) => Sample();
            sampler.Start();
        }
        tray.Activated += () => { if (trayHidden) RestoreFromTray(); else MinimizeToTray(); };
        tray.QuitRequested += Quit;
        tray.BalloonClicked += () => { if (notifyPid > 0) Native.FocusProcessWindow(notifyPid); };
        Closed += (_, _) => tray.Dispose();
        RefreshCells();
        if (opt.Tray) MinimizeToTray();
    }

    // ------------------------------------------------------------ tray

    void Quit()
    {
        tray.Dispose();
        Application.Current.Exit();
    }

    void MinimizeToTray()
    {
        trayHidden = true;
        tray.BarVisible = false;
        appWindow.Hide();
    }

    void RestoreFromTray()
    {
        if (!trayHidden) return;
        trayHidden = false;
        tray.BarVisible = true;
        appWindow.Show(false);
        Reposition();
        Native.ForceTopmost(hwnd);
    }

    static SessState Urgent(List<Session> rows) =>
        rows.Any(r => r.State == SessState.Waiting) ? SessState.Waiting
        : rows.Any(r => r.State == SessState.Working) ? SessState.Working : SessState.Idle;

    void UpdateTray(List<Session> rows)
    {
        var n = rows.Count(r => r.State == SessState.Waiting);
        var tip = rows.Count == 0 ? "AI status: no sessions"
            : n > 0 ? $"AI status: {n} need{(n == 1 ? "s" : "")} you"
            : $"AI status: {rows.Count} session{(rows.Count == 1 ? "" : "s")}";
        tray.Show(Urgent(rows), tip);
    }

    /// <summary>Keep the icon current; while the bar is minimized, pop a notification when a session starts waiting.</summary>
    void NotifyTray(List<Session> rows)
    {
        UpdateTray(rows);
        if (trayHidden)
        {
            foreach (var r in rows)
            {
                if (r.State != SessState.Waiting || prevStates.GetValueOrDefault(r.Id) == SessState.Waiting) continue;
                notifyPid = r.Pid;
                tray.Balloon(SessState.Waiting, $"{r.Project} needs you", r.Detail.Length > 0 && r.Detail != "thinking" ? r.Detail : "A session is waiting for your input.");
            }
        }
        prevStates.Clear();
        foreach (var r in rows) prevStates[r.Id] = r.State;
    }

    // ------------------------------------------------------------ drag

    void OnPress(object s, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(root);
        if (!pt.Properties.IsLeftButtonPressed) return;
        Native.GetCursorPos(out dragStart);
        dragOrigin = appWindow.Position;
        dragging = true;
        root.CapturePointer(e.Pointer);
    }

    void OnDrag(object s, PointerRoutedEventArgs e)
    {
        if (!dragging) return;
        moved = true;
        Native.GetCursorPos(out var c);
        appWindow.Move(new PointInt32(dragOrigin.X + c.X - dragStart.X, dragOrigin.Y + c.Y - dragStart.Y));
    }

    void OnRelease(object s, PointerRoutedEventArgs e)
    {
        var wasClick = dragging && IsClick();
        dragging = false;
        root.ReleasePointerCaptures();
        if (!wasClick || e.GetCurrentPoint(root).Properties.PointerUpdateKind != Microsoft.UI.Input.PointerUpdateKind.LeftButtonReleased) return;

        foreach (var (sid, cell) in cells)
        {
            var p = e.GetCurrentPoint(cell.Box).Position;
            if (p.X < 0 || p.Y < 0 || p.X > cell.Box.ActualWidth || p.Y > cell.Box.ActualHeight) continue;
            var pid = store.Snapshot().FirstOrDefault(r => r.Id == sid)?.Pid ?? 0;
            if (pid > 0) Native.FocusProcessWindow(pid);
            break;
        }
    }

    bool IsClick()
    {
        Native.GetCursorPos(out var c);
        return Math.Abs(c.X - dragStart.X) <= 4 && Math.Abs(c.Y - dragStart.Y) <= 4;
    }

    // ---------------------------------------------------------- layout

    double Scale => Native.GetDpiForWindow(hwnd) / 96.0;

    void ResizeToContent()
    {
        root.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = root.DesiredSize;
        // small slack so text never clips due to rounding/DPI differences
        curW = (int)Math.Ceiling((sz.Width + 16) * Scale);
        curH = (int)Math.Ceiling((sz.Height + 2) * Scale);
        appWindow.Resize(new SizeInt32(curW, curH));
    }

    void Reposition()
    {
        if (moved) return;
        if (opt.Taskbar && Native.TaskbarGeometry() is { } g)
        {
            var x = Math.Max(g.L, g.TrayLeft - curW - 8);
            var y = g.T + Math.Max(0, (g.B - g.T - curH) / 2);
            appWindow.Move(new PointInt32(x, y));
            return;
        }
        var (l, t, r, b) = Native.WorkArea();
        var c = opt.Corner;
        var px = c[1] == 'l' ? l + 8 : c[1] == 'r' ? r - curW - 8 : l + (r - l - curW) / 2;
        var py = c[0] == 't' ? t + 8 : b - curH - 8;
        appWindow.Move(new PointInt32(px, py));
    }

    // ----------------------------------------------------------- theme

    void Sample()
    {
        try
        {
            var pos = appWindow.Position;
            var sz = appWindow.Size;
            if (Native.SampleBehind(pos.X, pos.Y, sz.Width, sz.Height, opt.Taskbar) is { } t) target = t;
        }
        catch { /* sampling is best effort */ }
    }

    Theme ApplyTheme()
    {
        if (opt.Adapt) cur = Mix(cur, target, 0.5);
        var bg = cur;
        var fg = Lum(bg) > 0.55 ? (20.0, 20.0, 20.0) : (235.0, 235.0, 235.0);
        var th = new Theme(
            Rgb(bg.R, bg.G, bg.B),
            Rgb(fg.Item1, fg.Item2, fg.Item3),
            ToColor(Mix(bg, fg, 0.55)),
            ToColor(Mix(bg, ToTuple(StateColors[SessState.Waiting]), 0.45)),
            ToColor(Mix(bg, fg, 0.30)));
        if (th != applied)
        {
            applied = th;
            root.Background = new SolidColorBrush(th.Bg);
            root.BorderBrush = new SolidColorBrush(th.Border);
            empty.Foreground = new SolidColorBrush(th.Dim);
        }
        return th;

        static Color ToColor((double R, double G, double B) c) => Rgb(c.R, c.G, c.B);
    }

    // --------------------------------------------------------- refresh

    static string FmtElapsed(double secs)
    {
        var s = (int)secs;
        if (s < 60) return $"{s}s";
        if (s < 3600) return $"{s / 60}m";
        return $"{s / 3600}h{s % 3600 / 60:00}";
    }

    static string FmtTokens(long n) =>
        n < 1000 ? n.ToString() : n < 1_000_000 ? $"{n / 1000.0:0}k" : $"{n / 1e6:0.0}M";

    Cell MakeCell()
    {
        var dot = new Ellipse { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var label = new TextBlock { FontSize = fontSize, FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center };
        var close = new Button
        {
            Content = "×",
            FontSize = fontSize,
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(close, "Remove session");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(dot);
        row.Children.Add(label);
        row.Children.Add(close);
        var box = new Border
        {
            Child = row,
            Padding = new Thickness(8, 2, 8, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        return new Cell { Box = box, Dot = dot, Label = label, Close = close };
    }

    void RefreshCells()
    {
        try
        {
            var th = ApplyTheme();
            var rows = store.Snapshot();
            NotifyTray(rows);
            if (!opt.Taskbar && opt.Corner[0] == 'b') rows.Reverse(); // bottom-anchored: bar grows upward
            var ids = rows.Select(r => r.Id).ToHashSet();
            var changed = false;

            foreach (var sid in cells.Keys.Where(k => !ids.Contains(k)).ToList())
            {
                cells.Remove(sid);
                changed = true;
            }
            foreach (var r in rows)
            {
                if (!cells.TryGetValue(r.Id, out var cell))
                {
                    cells[r.Id] = cell = MakeCell();
                    var sid = r.Id;
                    cell.Close.Click += (_, _) =>
                    {
                        store.Remove(sid);
                        RefreshCells();
                    };
                    changed = true;
                }
                var waiting = r.State == SessState.Waiting;
                cell.Box.Background = new SolidColorBrush(waiting ? th.Wait : th.Bg);
                cell.Label.Foreground = new SolidColorBrush(th.Fg);
                cell.Close.Foreground = new SolidColorBrush(th.Fg);
                cell.Dot.Fill = new SolidColorBrush(StateColors[r.State]);

                var label = r.State switch { SessState.Working => "working", SessState.Waiting => "needs you", _ => "idle" };
                var extra = r.State == SessState.Working && r.Detail != "thinking" ? r.Detail : "";
                var who = r.Agent == "claude" ? "" : $" [{r.Agent}]";
                var text = $"{r.Project}{who}  ·  {label}" + (extra.Length > 0 ? $" ({extra})" : "");
                text += $"  {FmtElapsed(SessionStore.Now() - r.Since)}";
                if (r.Credits is { } aic) text += $"  ·  {aic:0.00} AIC";
                else if (r.Tokens is { } tok) text += $"  ·  {FmtTokens(tok)} tok";
                if (cell.Label.Text != text)
                {
                    cell.Label.Text = text;
                    cell.Label.FontWeight = waiting ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
                    changed = true;
                }
            }

            var order = string.Join(",", rows.Select(r => r.Id));
            if (order != lastOrder)
            {
                lastOrder = order;
                changed = true;
            }
            if (changed)
            {
                panel.Children.Clear();
                if (rows.Count == 0) panel.Children.Add(empty);
                foreach (var r in rows) panel.Children.Add(cells[r.Id].Box);
                ResizeToContent();
                Reposition();
            }
            if (opt.Taskbar) Dock();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("refresh error: " + e.Message);
        }
    }

    /// <summary>Follow the taskbar, stay on top of it, hide for fullscreen apps.</summary>
    void Dock()
    {
        if (trayHidden) return;
        bool hide;
        try { hide = Native.FullscreenForeground(); }
        catch { hide = false; }
        if (hide != hidden)
        {
            hidden = hide;
            if (hide) appWindow.Hide(); else appWindow.Show(false);
        }
        if (!hide)
        {
            Reposition(); // tracks taskbar auto-hide / resolution changes
            Native.ForceTopmost(hwnd);
        }
    }
}
