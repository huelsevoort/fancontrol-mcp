using FanControl.Plugins;

namespace FanControl.MCP;

/// <summary>
/// Temperature sensor whose value is written by MCP clients. It shows up in FanControl like any other
/// temperature source, so it can drive fan curves (e.g. a "boost" curve an assistant can trigger)
/// without touching the engine internals.
/// </summary>
internal sealed class VirtualSensor : IPluginSensor
{
    private readonly object _lock = new();
    private float? _value = 0f;

    public VirtualSensor(int index)
    {
        Index = index;
        Id = $"FanControl.MCP/virtual/{index}";
        Name = $"MCP Virtual {index}";
    }

    public int Index { get; }
    public string Id { get; }
    public string Name { get; }

    public float? Value
    {
        get { lock (_lock) return _value; }
    }

    public void Set(float? value)
    {
        lock (_lock) _value = value;
    }

    public void Update() { }
}
