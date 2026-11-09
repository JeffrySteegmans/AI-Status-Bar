using System.Text.Json.Nodes;

namespace AIStatusBar;

/// <summary>Hook configuration for Claude Code and GitHub Copilot CLI (the hook itself is AIStatusBarHook.exe).</summary>
static class HookConfig
{
    public const string HookExeName = "AIStatusBarHook.exe";

    public static readonly string[] ClaudeEvents =
        { "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Notification", "Stop", "SessionEnd" };

    public static readonly string[] CopilotEvents =
        { "sessionStart", "userPromptSubmitted", "preToolUse", "postToolUse", "notification", "agentStop", "sessionEnd" };

    public static string HookExeIn(string dir) => Path.Combine(dir, HookExeName);

    static string Cmd(string exe, string args = "") => $"\"{exe}\"" + (args.Length > 0 ? " " + args : "");

    /// <summary>Our hook commands (also matches older python installs of this tool).</summary>
    public static bool IsOurs(string? cmd)
    {
        var c = (cmd ?? "").ToLowerInvariant();
        return c.Contains("statusbar") && (c.Contains("hook.exe") || c.Contains("hook.py"));
    }

    public static JsonObject ClaudeEntry(string ev, string exe)
    {
        var entry = new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = Cmd(exe) }),
        };
        if (ev is "PreToolUse" or "PostToolUse") entry["matcher"] = "*";
        return entry;
    }

    public static JsonObject ClaudeConfig(string exe)
    {
        var hooks = new JsonObject();
        foreach (var ev in ClaudeEvents)
            hooks[ev] = new JsonArray(ClaudeEntry(ev, exe));
        return new JsonObject { ["hooks"] = hooks };
    }

    public static JsonObject CopilotConfig(string exe)
    {
        var hooks = new JsonObject();
        foreach (var ev in CopilotEvents)
        {
            var args = $"--agent copilot --event {ev}";
            hooks[ev] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["bash"] = Cmd(exe.Replace('\\', '/'), args),
                ["powershell"] = "& " + Cmd(exe, args),
                ["timeoutSec"] = 5,
            });
        }
        return new JsonObject { ["version"] = 1, ["hooks"] = hooks };
    }

    public static string CopilotHookPath()
    {
        var home = Environment.GetEnvironmentVariable("COPILOT_HOME");
        if (string.IsNullOrEmpty(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");
        return Path.Combine(home, "hooks", "statusbar.json");
    }
}
