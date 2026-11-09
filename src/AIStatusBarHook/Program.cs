using System.Runtime.InteropServices;
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

// PID of the CLI that fired this hook: nearest ancestor that is not a shell/wrapper.
// Lets the bar tie sessions to their terminal, so /new replaces the old pill and dead CLIs disappear.
static int? CliPid()
{
    var snap = Native.CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
    if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return null;
    var ppid = new Dictionary<uint, uint>();
    var name = new Dictionary<uint, string>();
    try
    {
        var e = new Native.PE32 { dwSize = (uint)Marshal.SizeOf<Native.PE32>() };
        for (var ok = Native.Process32FirstW(snap, ref e); ok; ok = Native.Process32NextW(snap, ref e))
        {
            ppid[e.th32ProcessID] = e.th32ParentProcessID;
            name[e.th32ProcessID] = e.szExeFile.ToLowerInvariant();
        }
    }
    finally { Native.CloseHandle(snap); }

    var skip = new HashSet<string> { "python.exe", "pythonw.exe", "py.exe", "cmd.exe", "powershell.exe",
        "pwsh.exe", "conhost.exe", "bash.exe", "sh.exe", "aistatusbarhook.exe" };
    var pid = (uint)Environment.ProcessId;
    var seen = new HashSet<uint>();
    while (ppid.TryGetValue(pid, out var parent) && seen.Add(pid))
    {
        pid = parent;
        if (name.TryGetValue(pid, out var n) && !skip.Contains(n)) return (int)pid;
    }
    return null;
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

    if (payload["cli_pid"] == null)
    {
        try { if (CliPid() is { } cp) payload["cli_pid"] = cp; }
        catch { /* optional */ }
    }

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
    using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
    http.PostAsync($"http://127.0.0.1:{port}/event", content).GetAwaiter().GetResult();
}
catch
{
    // bar not running, bad JSON, etc. -- stay silent
}
return 0;

static class Native
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PE32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public UIntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll")] public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool Process32FirstW(IntPtr snap, ref PE32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool Process32NextW(IntPtr snap, ref PE32 e);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
}
