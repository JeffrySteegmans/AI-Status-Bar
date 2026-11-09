using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace AIStatusBar;

public enum SessState { Working, Waiting, Idle }

public sealed class Session
{
    public string Id = "";
    public SessState State = SessState.Idle;
    public double Since, Created, Seen;
    public string Detail = "", Cwd = "", Project = "", Agent = "claude";
    public string? TPath;
    public long? Tokens;

    public Session Clone() => (Session)MemberwiseClone();
}

/// <summary>Session state model + token counting (Claude Code and Copilot CLI hook payloads).</summary>
public sealed class SessionStore
{
    public const double StaleSecs = 2 * 3600; // forget sessions silent for this long

    static readonly Dictionary<string, string> Canon = new()
    {
        ["sessionstart"] = "SessionStart", ["sessionend"] = "SessionEnd",
        ["userpromptsubmit"] = "UserPromptSubmit", ["userpromptsubmitted"] = "UserPromptSubmit",
        ["pretooluse"] = "PreToolUse", ["posttooluse"] = "PostToolUse", ["posttoolusefailure"] = "PostToolUse",
        ["notification"] = "Notification", ["stop"] = "Stop", ["agentstop"] = "Stop",
    };

    static readonly HashSet<string> UsageEvents = new() { "SessionStart", "UserPromptSubmit", "PostToolUse", "Stop" };

    sealed class UsageInfo
    {
        public long Off;
        public Dictionary<string, long> Msgs = new();
    }

    readonly Dictionary<string, Session> state = new();
    readonly object gate = new();
    readonly ConcurrentDictionary<string, UsageInfo> usage = new();
    readonly object usageGate = new();

    public bool Verbose;

    public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public static string? Get(JsonObject p, params string[] keys)
    {
        foreach (var k in keys)
            if (p[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                return s;
        return null;
    }

    /// <summary>Update state from one hook payload.</summary>
    public void ApplyEvent(JsonObject p)
    {
        var sid = Get(p, "session_id", "sessionId") ?? "unknown";
        var ev = Get(p, "hook_event_name", "event") ?? "";
        ev = Canon.TryGetValue(ev.ToLowerInvariant().Replace("_", ""), out var c) ? c : ev;
        var now = Now();
        lock (gate)
        {
            if (ev == "SessionEnd")
            {
                state.Remove(sid);
                usage.TryRemove(sid, out _);
                return;
            }
            if (!state.TryGetValue(sid, out var s))
                state[sid] = s = new Session { Id = sid, Since = now, Created = now };

            s.Cwd = Get(p, "cwd") ?? s.Cwd;
            var proj = s.Cwd.Replace('\\', '/').TrimEnd('/').Split('/').Last();
            s.Project = proj.Length > 0 ? proj : sid[..Math.Min(8, sid.Length)];
            s.Agent = Get(p, "agent") ?? "claude";
            s.TPath = Get(p, "transcript_path", "transcriptPath") ?? s.TPath;
            s.Seen = now;

            var newState = s.State;
            var detail = s.Detail;
            switch (ev)
            {
                case "SessionStart": newState = SessState.Idle; detail = "started"; break;
                case "UserPromptSubmit": newState = SessState.Working; detail = "thinking"; break;
                case "PreToolUse":
                case "PostToolUse":
                    newState = SessState.Working;
                    detail = Get(p, "tool_name", "toolName") ?? detail;
                    break;
                case "Notification":
                    newState = SessState.Waiting;
                    var msg = Get(p, "message") ?? "needs you";
                    detail = msg.Length > 60 ? msg[..60] : msg;
                    break;
                case "Stop": newState = SessState.Idle; detail = "done"; break;
            }
            if (newState != s.State) s.Since = now;
            s.State = newState;
            s.Detail = detail;
        }
        if (UsageEvents.Contains(ev))
        {
            try { UpdateUsage(sid); }
            catch (Exception e) // cost is a nicety; never break state tracking
            {
                if (Verbose) Console.Error.WriteLine("usage error: " + e.Message);
            }
        }
    }

    // Tokens are read from the transcript Claude Code points us to. Counted per assistant
    // message: input + output + cache-creation tokens. Cache reads are excluded because the
    // same context is re-read every turn and would dwarf the real usage.
    static long Num(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<double>(out var d)) return (long)d;
        }
        return 0;
    }

    static long CountTokens(JsonObject u) =>
        Num(u["input_tokens"]) + Num(u["output_tokens"]) + Num(u["cache_creation_input_tokens"]);

    void UpdateUsage(string sid)
    {
        lock (usageGate)
        {
            string? path;
            lock (gate) path = state.TryGetValue(sid, out var s) ? s.TPath : null;
            if (string.IsNullOrEmpty(path)) return;

            var u = usage.GetOrAdd(sid, _ => new UsageInfo());
            byte[] data;
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return;
                if (fi.Length < u.Off) { u.Off = 0; u.Msgs.Clear(); } // file replaced/truncated
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                fs.Seek(u.Off, SeekOrigin.Begin);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                data = ms.ToArray();
            }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }

            var end = Array.LastIndexOf(data, (byte)'\n');
            if (end < 0) return; // no complete line yet

            foreach (var raw in Encoding.UTF8.GetString(data, 0, end + 1).Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                JsonObject? o;
                try { o = JsonNode.Parse(line) as JsonObject; }
                catch (System.Text.Json.JsonException) { continue; }
                if (o == null || Get(o, "type") != "assistant" || o["message"] is not JsonObject m) continue;
                if (m["usage"] is not JsonObject usageObj) continue;
                // streamed messages repeat under one id: keep the latest usage
                u.Msgs[Get(m, "id") ?? Get(o, "uuid") ?? ""] = CountTokens(usageObj);
            }
            u.Off += end + 1;
            var total = u.Msgs.Values.Sum();
            lock (gate)
                if (state.TryGetValue(sid, out var s2)) s2.Tokens = total;
        }
    }

    public List<Session> Snapshot()
    {
        var now = Now();
        lock (gate)
        {
            foreach (var k in state.Where(kv => now - kv.Value.Seen > StaleSecs).Select(kv => kv.Key).ToList())
            {
                state.Remove(k);
                usage.TryRemove(k, out _);
            }
            // oldest first; stable, rows never jump around
            return state.Values.Select(v => v.Clone()).OrderBy(r => r.Created).ToList();
        }
    }

    public void Clear()
    {
        lock (gate) state.Clear();
    }

    public void AddTokens(string sid, long n)
    {
        lock (gate)
            if (state.TryGetValue(sid, out var s)) s.Tokens = (s.Tokens ?? 0) + n;
    }

    public JsonArray ToJson()
    {
        var arr = new JsonArray();
        foreach (var r in Snapshot())
            arr.Add(new JsonObject
            {
                ["state"] = r.State.ToString().ToLowerInvariant(),
                ["since"] = r.Since,
                ["detail"] = r.Detail,
                ["created"] = r.Created,
                ["cwd"] = r.Cwd,
                ["project"] = r.Project,
                ["agent"] = r.Agent,
                ["tpath"] = r.TPath,
                ["seen"] = r.Seen,
                ["tokens"] = r.Tokens,
                ["id"] = r.Id,
            });
        return arr;
    }

    public void DemoFeed()
    {
        (string sid, string cwd, string ev, string? tool)[] script =
        {
            ("a", "/work/api-service", "SessionStart", null),
            ("b", "/work/web-app", "SessionStart", null),
            ("a", "/work/api-service", "UserPromptSubmit", null),
            ("a", "/work/api-service", "PreToolUse", "Edit"),
            ("b", "/work/web-app", "UserPromptSubmit", null),
            ("b", "/work/web-app", "Notification", null),
            ("a", "/work/api-service", "PreToolUse", "Bash"),
            ("b", "/work/web-app", "PostToolUse", "Bash"),
            ("a", "/work/api-service", "Stop", null),
            ("b", "/work/web-app", "Stop", null),
        };
        while (true)
            foreach (var (sid, cwd, ev, tool) in script)
            {
                var p = new JsonObject { ["session_id"] = sid, ["cwd"] = cwd, ["hook_event_name"] = ev };
                if (tool != null) p["tool_name"] = tool;
                if (ev == "Notification") p["message"] = "Claude needs your permission to use Bash";
                ApplyEvent(p);
                AddTokens(sid, 4200);
                Thread.Sleep(3000);
            }
    }
}
