using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIStatusBar;

/// <summary>Local HTTP collector: POST /event with a hook payload, GET / for JSON state.</summary>
public sealed class Collector
{
    public static int Port => int.TryParse(Environment.GetEnvironmentVariable("AI_STATUSBAR_PORT"), out var p) ? p : 47600;

    readonly SessionStore store;
    readonly HttpListener listener = new();

    public Collector(SessionStore store)
    {
        this.store = store;
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    /// <summary>Throws HttpListenerException when the port is taken.</summary>
    public void Start()
    {
        listener.Start();
        new Thread(Loop) { IsBackground = true }.Start();
    }

    void Loop()
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch { return; }
            try { Handle(ctx); }
            catch (Exception e) { Console.Error.WriteLine("bad event: " + e.Message); }
        }
    }

    void Handle(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            if (ctx.Request.HttpMethod == "POST")
            {
                try
                {
                    using var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var body = sr.ReadToEnd();
                    var payload = (string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body)) as JsonObject ?? new JsonObject();
                    store.ApplyEvent(payload);
                    if (store.Verbose)
                    {
                        var sid = SessionStore.Get(payload, "session_id") ?? "";
                        Console.WriteLine($"{DateTime.Now:HH:mm:ss} {SessionStore.Get(payload, "hook_event_name")} " +
                                          $"{sid[..Math.Min(8, sid.Length)]} {SessionStore.Get(payload, "tool_name")}");
                    }
                    res.StatusCode = 204;
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException)
                {
                    Console.Error.WriteLine("bad event: " + e.Message);
                    res.StatusCode = 400;
                }
            }
            else
            {
                var body = Encoding.UTF8.GetBytes(store.ToJson().ToJsonString(Json.Indented));
                res.StatusCode = 200;
                res.ContentType = "application/json";
                res.ContentLength64 = body.Length;
                res.OutputStream.Write(body, 0, body.Length);
            }
        }
        finally
        {
            res.Close();
        }
    }
}
