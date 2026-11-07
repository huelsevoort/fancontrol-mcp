using System.Text.Json;
using System.Text.Json.Nodes;
using FanControl.MCP.Mcp;

namespace FanControl.MCP.Wizard;

/// <summary>
/// What the setup wizard learned about the user's PC and goal, persisted as "FanControl.MCP.profile.json"
/// next to the plugin. Stored as free-form JSON (see <see cref="SetupWizard"/> for the documented fields) so
/// new questions can be added without migrations; updates are merged as JSON merge patches (RFC 7386).
/// </summary>
internal sealed class SystemProfile
{
    public const string FileName = "FanControl.MCP.profile.json";

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly object _lock = new();
    private JsonObject _data;

    public SystemProfile(string directory)
    {
        _path = System.IO.Path.Combine(directory, FileName);
        _data = LoadFile(_path);
    }

    /// <summary>For tests: an in-memory profile.</summary>
    public SystemProfile(JsonObject data)
    {
        _path = "";
        _data = data;
    }

    public string FilePath => _path;

    public JsonObject Snapshot()
    {
        lock (_lock) return _data.DeepClone().AsObject();
    }

    /// <summary>Merges <paramref name="patch"/> into the profile (null removes a key) and saves it.</summary>
    public JsonObject Merge(JsonObject patch)
    {
        lock (_lock)
        {
            var merged = MergePatch(_data, patch).AsObject();
            merged["version"] = 1;
            merged["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            _data = merged;
            Save();
            return _data.DeepClone().AsObject();
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _data = new JsonObject { ["version"] = 1 };
            Save();
        }
    }

    private void Save()
    {
        if (_path.Length == 0) return;
        try { File.WriteAllText(_path, _data.ToJsonString(Pretty)); }
        catch (Exception e) { throw new ToolException($"Could not save profile to {_path}: {e.Message}"); }
    }

    private static JsonObject LoadFile(string path)
    {
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o) return o;
        }
        catch
        {
            // corrupt file: start fresh but keep the old one for the user
            try { File.Copy(path, path + ".broken", overwrite: true); } catch { }
        }
        return new JsonObject { ["version"] = 1 };
    }

    /// <summary>RFC 7386 JSON merge patch. Arrays are replaced, except "fans", which is merged by "controlId".</summary>
    internal static JsonNode MergePatch(JsonNode? target, JsonNode? patch, string? key = null)
    {
        if (patch is not JsonObject patchObj)
        {
            if (key == "fans" && patch is JsonArray newFans && target is JsonArray oldFans) return MergeFans(oldFans, newFans);
            return patch?.DeepClone()!;
        }
        var result = target as JsonObject is { } t ? t.DeepClone().AsObject() : new JsonObject();
        foreach (var (k, v) in patchObj)
        {
            if (v is null) result.Remove(k);
            else result[k] = MergePatch(result[k], v, k);
        }
        return result;
    }

    private static JsonArray MergeFans(JsonArray oldFans, JsonArray newFans)
    {
        var result = new JsonArray();
        var byId = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in oldFans.OfType<JsonObject>())
        {
            var id = f["controlId"]?.ToString() ?? f["name"]?.ToString() ?? Guid.NewGuid().ToString();
            byId[id] = f.DeepClone().AsObject();
        }
        foreach (var f in newFans.OfType<JsonObject>())
        {
            var id = f["controlId"]?.ToString() ?? f["name"]?.ToString() ?? Guid.NewGuid().ToString();
            byId[id] = byId.TryGetValue(id, out var existing) ? MergePatch(existing, f).AsObject() : f.DeepClone().AsObject();
        }
        foreach (var f in byId.Values) result.Add(f);
        return result;
    }
}
