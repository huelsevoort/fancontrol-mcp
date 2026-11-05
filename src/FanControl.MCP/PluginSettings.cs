using System.Text.Json;
using System.Text.Json.Serialization;

namespace FanControl.MCP;

/// <summary>
/// Settings read from "FanControl.MCP.json" next to the plugin DLL. A default file is written on first start.
/// </summary>
internal sealed class PluginSettings
{
    public const string FileName = "FanControl.MCP.json";

    /// <summary>TCP port of the MCP endpoint (http://127.0.0.1:{Port}/mcp).</summary>
    public int Port { get; set; } = 48620;

    /// <summary>Address to bind to. Keep it on loopback unless you know what you are doing.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>Optional bearer token. When set, clients must send "Authorization: Bearer &lt;token&gt;".</summary>
    public string AuthToken { get; set; } = "";

    /// <summary>When false, every tool that changes fans or configuration is rejected.</summary>
    public bool AllowWrite { get; set; } = true;

    /// <summary>Lowest manual fan speed in percent an MCP client may set (safety floor).</summary>
    public int MinimumManualPercent { get; set; } = 0;

    /// <summary>Number of virtual temperature sensors the plugin adds to FanControl.</summary>
    public int VirtualSensorCount { get; set; } = 4;

    [JsonIgnore]
    public string? LoadedFrom { get; private set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static PluginSettings Load(string directory, Action<string> log)
    {
        var path = Path.Combine(directory, FileName);
        PluginSettings settings;
        try
        {
            if (File.Exists(path))
            {
                settings = JsonSerializer.Deserialize<PluginSettings>(File.ReadAllText(path), JsonOptions) ?? new PluginSettings();
            }
            else
            {
                settings = new PluginSettings();
                try { File.WriteAllText(path, JsonSerializer.Serialize(settings, JsonOptions)); }
                catch (Exception e) { log($"Could not write default settings to {path}: {e.Message}"); }
            }
        }
        catch (Exception e)
        {
            log($"Invalid settings file {path}, using defaults: {e.Message}");
            settings = new PluginSettings();
        }

        settings.Port = Math.Clamp(settings.Port, 1, 65535);
        settings.MinimumManualPercent = Math.Clamp(settings.MinimumManualPercent, 0, 100);
        settings.VirtualSensorCount = Math.Clamp(settings.VirtualSensorCount, 0, 16);
        if (string.IsNullOrWhiteSpace(settings.BindAddress)) settings.BindAddress = "127.0.0.1";
        settings.LoadedFrom = path;
        return settings;
    }
}
