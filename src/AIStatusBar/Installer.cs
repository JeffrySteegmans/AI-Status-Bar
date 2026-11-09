using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace AIStatusBar;

/// <summary>Per-user install/uninstall (no admin rights): files, Claude + Copilot hooks, autostart.</summary>
static class Installer
{
    const string AppName = "AIStatusBar";

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string DestDir => Path.Combine(
        Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } b ? b : Path.Combine(Home, ".local", "share"), AppName);
    static string ClaudeSettings => Path.Combine(Home, ".claude", "settings.json");
    static string StartupLnk => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "AI Status Bar.lnk");

    static string Self => Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');
    static bool RunningFromDest => string.Equals(Self, DestDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------ helpers

    static string? Backup(string path)
    {
        if (!File.Exists(path)) return null;
        var b = $"{path}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Copy(path, b, true);
        return b;
    }

    /// <summary>Remove our entries from a Claude `hooks` object, keep everything else.</summary>
    static void StripOurs(JsonObject hooks)
    {
        foreach (var ev in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[ev] is not JsonArray entries) continue;
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i] is not JsonObject entry || entry["hooks"] is not JsonArray inner) continue;
                var before = inner.Count;
                for (var j = inner.Count - 1; j >= 0; j--)
                    if (inner[j] is JsonObject h && HookConfig.IsOurs(SessionStore.Get(h, "command")))
                        inner.RemoveAt(j);
                if (inner.Count == 0 && before > 0) entries.RemoveAt(i);
            }
            if (entries.Count == 0) hooks.Remove(ev);
        }
    }

    static void StopRunning()
    {
        var me = Environment.ProcessId;
        foreach (var p in Process.GetProcessesByName(AppName))
        {
            try
            {
                if (p.Id != me) { p.Kill(); p.WaitForExit(5000); }
            }
            catch { /* already gone / not ours */ }
            finally { p.Dispose(); }
        }
    }

    static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src))
            File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    static JsonObject? CollectorState()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1.5) };
        var txt = http.GetStringAsync($"http://127.0.0.1:{Collector.Port}/").GetAwaiter().GetResult();
        return new JsonObject { ["rows"] = JsonNode.Parse(txt) };
    }

    static void SendHookEvent(string hookExe, string name)
    {
        var ev = new JsonObject { ["session_id"] = "installer-test", ["cwd"] = "installer-test", ["hook_event_name"] = name };
        var psi = new ProcessStartInfo(hookExe) { RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(ev.ToJsonString());
        p.StandardInput.Close();
        p.WaitForExit(15000);
    }

    // --------------------------------------------------------------- steps

    static string StepCopy()
    {
        var dest = DestDir;
        Directory.CreateDirectory(dest);
        if (!RunningFromDest) CopyDir(Self, dest);
        return dest;
    }

    static (string path, string? backup) StepClaude(string hookExe)
    {
        var path = ClaudeSettings;
        var data = Json.Load(path) as JsonObject ?? throw new InvalidOperationException("unexpected structure in settings.json");
        if (data["hooks"] is not null and not JsonObject) throw new InvalidOperationException("unexpected structure in settings.json");
        var hooks = data["hooks"] as JsonObject ?? new JsonObject();
        data["hooks"] = hooks;
        StripOurs(hooks);
        foreach (var ev in HookConfig.ClaudeEvents)
        {
            if (hooks[ev] is not JsonArray arr) hooks[ev] = arr = new JsonArray();
            arr.Add(HookConfig.ClaudeEntry(ev, hookExe));
        }
        var b = Backup(path);
        Json.Write(path, data);
        return (path, b);
    }

    static string StepCopilot(string hookExe)
    {
        var path = HookConfig.CopilotHookPath();
        Json.Write(path, HookConfig.CopilotConfig(hookExe));
        return path;
    }

    static string StepAutostart(string exe, string[] barArgs)
    {
        var lnk = StartupLnk;
        Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic s = shell.CreateShortcut(lnk);
        s.TargetPath = exe;
        s.Arguments = string.Join(" ", barArgs);
        s.WorkingDirectory = Path.GetDirectoryName(exe);
        s.Save();
        return lnk;
    }

    static void StepStart(string exe, string[] barArgs)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var a in barArgs) psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
    }

    static (bool ok, string why) StepVerify(string hookExe)
    {
        var up = false;
        for (var i = 0; i < 25 && !up; i++) // wait up to ~6 s for the bar to come up
        {
            try { CollectorState(); up = true; }
            catch { Thread.Sleep(250); }
        }
        if (!up) return (false, "the bar did not start (try running AIStatusBar.exe --no-ui to see the error)");
        SendHookEvent(hookExe, "SessionStart");
        Thread.Sleep(400);
        var seen = CollectorState()?["rows"] is JsonArray rows && rows.Any(r => r?["id"]?.ToString() == "installer-test");
        SendHookEvent(hookExe, "SessionEnd");
        return (seen, seen ? "" : "bar is running but the test event did not arrive");
    }

    // ---------------------------------------------------------------- main

    public static int Install(string[] barArgs)
    {
        var ok = true;
        void Report(bool good, string msg)
        {
            ok &= good;
            Console.WriteLine((good ? "  [ok]   " : "  [FAIL] ") + msg);
        }

        Console.WriteLine("Installing AI Status Bar...\n");
        string dest;
        try
        {
            StopRunning();
            dest = StepCopy();
            Report(true, $"program files in {dest}");
        }
        catch (Exception e)
        {
            Report(false, $"copying files: {e.Message}");
            return 1;
        }
        var hook = HookConfig.HookExeIn(dest);
        var exe = Path.Combine(dest, AppName + ".exe");
        if (!File.Exists(hook)) Report(false, $"{HookConfig.HookExeName} not found in {dest}");

        try
        {
            var (path, b) = StepClaude(hook);
            Report(true, $"Claude Code hooks added to {path}" + (b != null ? $"\n         (backup: {b})" : ""));
        }
        catch (Exception e)
        {
            Report(false, $"Claude Code settings NOT changed ({e.Message}). Your file was left untouched.");
        }

        try { Report(true, $"Copilot CLI hooks written to {StepCopilot(hook)}"); }
        catch (Exception e) { Report(false, $"Copilot CLI hooks: {e.Message}"); }

        try { Report(true, $"starts automatically at login ({StepAutostart(exe, barArgs)})"); }
        catch (Exception e) { Report(false, $"autostart shortcut: {e.Message}"); }

        try
        {
            StepStart(exe, barArgs);
            var (good, why) = StepVerify(hook);
            Report(good, good ? "bar is running and receiving events" : why);
        }
        catch (Exception e) { Report(false, $"starting the bar: {e.Message}"); }

        Console.WriteLine(ok ? "\nDone." : "\nFinished with problems (see [FAIL] above).");
        Console.WriteLine("Restart any open Claude Code / Copilot CLI sessions so they pick up the hooks.");
        return ok ? 0 : 1;
    }

    public static int Uninstall()
    {
        StopRunning();
        Console.WriteLine("Uninstalling AI Status Bar...\n");

        var path = ClaudeSettings;
        try
        {
            if (Json.Load(path) is JsonObject data && data["hooks"] is JsonObject hooks)
            {
                var before = hooks.ToJsonString();
                StripOurs(hooks);
                if (hooks.ToJsonString() != before)
                {
                    if (hooks.Count == 0) data.Remove("hooks");
                    Backup(path);
                    Json.Write(path, data);
                    Console.WriteLine("  [ok]   removed hooks from " + path);
                }
                else Console.WriteLine("  [ok]   no status bar hooks in " + path);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"  [FAIL] could not edit {path}: {e.Message} (left untouched)");
        }

        foreach (var (p, label) in new[] { (HookConfig.CopilotHookPath(), "Copilot CLI hook file"), (StartupLnk, "autostart shortcut") })
        {
            if (!File.Exists(p)) continue;
            File.Delete(p);
            Console.WriteLine($"  [ok]   removed {label}");
        }

        var dest = DestDir;
        if (Directory.Exists(dest))
        {
            if (RunningFromDest) // we are running from inside dest: delete it after this process exits
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{dest}\"")
                { CreateNoWindow = true, UseShellExecute = false })?.Dispose();
            else
                try { Directory.Delete(dest, true); } catch { /* best effort */ }
            Console.WriteLine("  [ok]   program files removed from " + dest);
        }
        Console.WriteLine("\nDone.");
        return 0;
    }
}
