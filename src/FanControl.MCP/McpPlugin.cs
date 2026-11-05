using FanControl.MCP.Engine;
using FanControl.MCP.Mcp;
using FanControl.Plugins;

namespace FanControl.MCP;

/// <summary>
/// FanControl plugin entry point. FanControl discovers it by reflection in "Plugins\FanControl.MCP.dll",
/// injects <see cref="IPluginLogger"/> through the constructor and drives the
/// Initialize → Load → (Update…) → Close life-cycle, possibly several times (refresh).
/// </summary>
public sealed class McpPlugin : IPlugin3, IEngineProvider
{
    private readonly IPluginLogger? _logger;
    private readonly object _sync = new();
    private PluginSettings? _settings;
    private List<VirtualSensor> _virtualSensors = [];
    private HttpServer? _server;
    private EngineBridge? _engine;
    private DateTime _lastLocateAttempt = DateTime.MinValue;
    private string _locateError = "not searched yet";

    public McpPlugin(IPluginLogger logger)
    {
        _logger = logger;
    }

    /// <summary>Used by tests and by FanControl versions that do not inject a logger.</summary>
    public McpPlugin()
    {
    }

    public string Name => "MCP Server";

    /// <inheritdoc />
    /// <remarks>Raised by FanControl's contract only; the plugin itself never requests a refresh.
    /// FanControl's subscription to this event is also how the plugin finds the engine.</remarks>
    public event Action? RefreshRequested;

    public string Mode => EngineLocator.GetWpfApplication() is null ? "service" : "desktop";

    public void Initialize()
    {
        var dir = Path.GetDirectoryName(typeof(McpPlugin).Assembly.Location) ?? AppContext.BaseDirectory;
        _settings = PluginSettings.Load(dir, Log);
        _virtualSensors = Enumerable.Range(1, _settings.VirtualSensorCount).Select(i => new VirtualSensor(i)).ToList();
    }

    public void Load(IPluginSensorsContainer container)
    {
        if (_settings is null) Initialize();
        container.TempSensors.AddRange(_virtualSensors);
        StartServer();
    }

    public void Update()
    {
        // Values are read live from the engine on request; nothing to poll here.
    }

    public void Close()
    {
        lock (_sync)
        {
            _server?.Dispose();
            _server = null;
            _engine = null;
            _lastLocateAttempt = DateTime.MinValue;
        }
    }

    // ------------------------------------------------------------------ engine access

    EngineBridge? IEngineProvider.TryGetEngine(out string reason)
    {
        lock (_sync)
        {
            if (_engine is not null) { reason = ""; return _engine; }
            if (DateTime.UtcNow - _lastLocateAttempt < TimeSpan.FromSeconds(2)) { reason = _locateError; return null; }
            _lastLocateAttempt = DateTime.UtcNow;
            try
            {
                var engine = EngineLocator.Find([RefreshRequested, this, EngineLocator.GetWpfApplication()]);
                if (engine is null)
                {
                    _locateError = "FanControl's engine was not found in this process. If FanControl runs as a Windows service, the plugin lives in the service process; make sure FanControl has finished starting.";
                    reason = _locateError;
                    return null;
                }
                _engine = new EngineBridge(engine);
                reason = "";
                return _engine;
            }
            catch (Exception e)
            {
                // Typically TypeLoadException/MissingMethodException after a FanControl update changed internals.
                _locateError = $"Incompatible FanControl version ({e.GetType().Name}: {e.Message}). Virtual sensors still work.";
                Log(_locateError);
                reason = _locateError;
                return null;
            }
        }
    }

    // ------------------------------------------------------------------ server

    private void StartServer()
    {
        lock (_sync)
        {
            if (_server is not null || _settings is null) return;
            var version = typeof(McpPlugin).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
            var protocol = new McpProtocol(FanControlTools.Create(this, _settings, _virtualSensors), "fancontrol-mcp", version, FanControlTools.Instructions, Log,
                TuningSkill.Resources, TuningSkill.Prompts);
            var server = new HttpServer(_settings.BindAddress, _settings.Port, _settings.AuthToken, protocol, Log);
            try
            {
                server.Start();
                _server = server;
                Log($"MCP server listening on {server.Endpoint}");
            }
            catch (Exception e)
            {
                server.Dispose();
                Log($"Could not start MCP server on {_settings.BindAddress}:{_settings.Port}: {e.Message}");
            }
        }
    }

    private void Log(string message)
    {
        try { _logger?.Log("[FanControl.MCP] " + message); } catch { }
        System.Diagnostics.Debug.WriteLine("[FanControl.MCP] " + message);
    }

    // Keeps the compiler from warning about the never-raised event while documenting why it exists.
    internal void RaiseRefreshRequested() => RefreshRequested?.Invoke();
}
