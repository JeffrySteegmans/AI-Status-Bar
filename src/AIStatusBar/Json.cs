using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIStatusBar;

static class Json
{
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Loads a JSON file; missing/empty file gives {}. Throws on unparsable content.</summary>
    public static JsonNode? Load(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        var txt = File.ReadAllText(path); // strips a UTF-8 BOM
        return string.IsNullOrWhiteSpace(txt) ? new JsonObject() : JsonNode.Parse(txt);
    }

    public static void Write(string path, JsonNode data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, data.ToJsonString(Indented) + "\n", new System.Text.UTF8Encoding(false));
    }
}
