using System.Net;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AIStatusBar;

static class Program
{
    const string Usage = """
        AI Status Bar

          AIStatusBar.exe                 run the bar + collector
          AIStatusBar.exe --demo          run with fake sessions
          AIStatusBar.exe --no-ui         collector only, prints events (debugging)
          AIStatusBar.exe --corner tr     tl tc tr bl bc br (default bc = bottom centre)
          AIStatusBar.exe --no-adapt      fixed dark background (skip colour sampling)
          AIStatusBar.exe --taskbar       dock into the Windows taskbar, left of the tray
                    AIStatusBar.exe --tray          start minimized to the tray (icon is always in the tray; popup when a session needs you while minimized)
          AIStatusBar.exe --install [bar options]   install hooks, autostart, start now
          AIStatusBar.exe --uninstall
          AIStatusBar.exe --print-config            Claude Code hooks JSON
          AIStatusBar.exe --install-copilot         write ~/.copilot/hooks/statusbar.json
        """;

    [STAThread]
    static int Main(string[] args)
    {
        Native.AttachConsole(-1); // print to the parent console when started from one

        if (args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
        {
            Console.WriteLine(Usage);
            return 0;
        }
        if (args.Contains("--print-config"))
        {
            var hook = HookConfig.HookExeIn(AppContext.BaseDirectory);
            Console.WriteLine(HookConfig.ClaudeConfig(hook).ToJsonString(Json.Indented));
            return 0;
        }
        if (args.Contains("--install-copilot"))
        {
            var path = HookConfig.CopilotHookPath();
            Json.Write(path, HookConfig.CopilotConfig(HookConfig.HookExeIn(AppContext.BaseDirectory)));
            Console.WriteLine("wrote " + path);
            return 0;
        }
        if (args.Length > 0 && args[0] == "--install") return Installer.Install(args[1..]);
        if (args.Contains("--uninstall")) return Installer.Uninstall();

        var opt = new BarOptions { Adapt = !args.Contains("--no-adapt"), Taskbar = args.Contains("--taskbar"), Tray = args.Contains("--tray") };
        var ci = Array.IndexOf(args, "--corner");
        if (ci >= 0 && ci < args.Length - 1)
        {
            if (!new[] { "tl", "tc", "tr", "bl", "bc", "br" }.Contains(args[ci + 1]))
            {
                Console.Error.WriteLine("--corner must be one of: tl tc tr bl bc br");
                return 2;
            }
            opt.Corner = args[ci + 1];
        }
        var noUi = args.Contains("--no-ui");

        var store = new SessionStore { Verbose = noUi };
        try
        {
            new Collector(store).Start();
        }
        catch (HttpListenerException e)
        {
            Console.Error.WriteLine($"Cannot listen on 127.0.0.1:{Collector.Port} ({e.Message}). Is the bar already running?");
            return 1;
        }
        Console.WriteLine($"Collector listening on http://127.0.0.1:{Collector.Port}  (GET / for JSON state)");
        store.StartUsagePoller();

        if (args.Contains("--demo")) new Thread(store.DemoFeed) { IsBackground = true }.Start();
        if (noUi)
        {
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        if (opt.Taskbar && Native.TaskbarGeometry() is null)
        {
            Console.Error.WriteLine("No taskbar found; falling back to corner placement.");
            opt.Taskbar = false;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            var app = new App(store, opt);
            GC.KeepAlive(app);
        });
        return 0;
    }
}
