using System.Text;
using System.Text.Json.Nodes;

// AI hook -> status bar forwarder (Claude Code and GitHub Copilot CLI).
// Reads the hook event JSON on stdin, forwards it to the local collector, and
// always exits 0 so a missing status bar can never break a session.
//   AIStatusBarHook.exe                                   (Claude Code)
//   AIStatusBarHook.exe --agent copilot --event preToolUse (Copilot CLI)

static string? Arg(string[] a, string name)
{
    var i = Array.IndexOf(a, name);
    return i >= 0 && i < a.Length - 1 ? a[i + 1] : null;
}

try
{
    var argv = Environment.GetCommandLineArgs();
    var port = int.TryParse(Environment.GetEnvironmentVariable("AI_STATUSBAR_PORT"), out var p) ? p : 47600;

    string raw;
    using (var sr = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
        raw = sr.ReadToEnd();
    var payload = (string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw)) as JsonObject ?? new JsonObject();

    var agent = Arg(argv, "--agent");
    if (agent != null) payload["agent"] = agent;
    var ev = Arg(argv, "--event");
    if (ev != null && string.IsNullOrEmpty(payload["hook_event_name"]?.ToString()))
        payload["hook_event_name"] = ev; // camelCase payloads carry no event name

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
    using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
    http.PostAsync($"http://127.0.0.1:{port}/event", content).GetAwaiter().GetResult();
}
catch
{
    // bar not running, bad JSON, etc. -- stay silent
}
return 0;
