using FanControl.MCP.Mcp;
using FanControl.Serializable;
using FanControl.Serializable.Interfaces;

namespace FanControl.MCP.Engine;

internal readonly record struct CurvePoint(double Temperature, double Percent);

/// <summary>Requested changes to a fan curve; null members stay unchanged.</summary>
internal sealed record FanCurveChange
{
    public IReadOnlyList<CurvePoint>? Points { get; init; }
    public string? TemperatureSource { get; init; }
    public double? HysteresisUp { get; init; }
    public double? HysteresisDown { get; init; }
    public int? ResponseTimeUp { get; init; }
    public int? ResponseTimeDown { get; init; }
    public bool? IgnoreHysteresisAtLimits { get; init; }
    public double? MinTemperature { get; init; }
    public double? MaxTemperature { get; init; }
    public int? MinSpeed { get; init; }
    public int? MaxSpeed { get; init; }
    public float? Percent { get; init; }

    public bool HysteresisSet => HysteresisUp is not null || HysteresisDown is not null || ResponseTimeUp is not null
                                 || ResponseTimeDown is not null || IgnoreHysteresisAtLimits is not null;

    public bool LinearOnlySet => MinTemperature is not null || MaxTemperature is not null || MinSpeed is not null || MaxSpeed is not null;

    /// <summary>Range and shape checks that apply regardless of curve type.</summary>
    public void Validate()
    {
        if (Points is { } pts)
        {
            if (pts.Count < 2) throw new ToolException("A graph curve needs at least 2 points.");
            if (pts.Count > 16) throw new ToolException("At most 16 points are supported.");
            for (var i = 0; i < pts.Count; i++)
            {
                var p = pts[i];
                if (p.Temperature is < 0 or > 150) throw new ToolException($"Point {i + 1}: temperature must be 0-150 °C.");
                if (p.Percent is < 0 or > 100) throw new ToolException($"Point {i + 1}: percent must be 0-100.");
                if (i > 0 && p.Temperature <= pts[i - 1].Temperature) throw new ToolException("Points must be sorted by strictly increasing temperature.");
                if (i > 0 && p.Percent < pts[i - 1].Percent)
                    throw new ToolException("Fan speed must not decrease when temperature rises (non-monotonic curves cause oscillation).");
            }
        }
        if (HysteresisUp is < 0 or > 20 || HysteresisDown is < 0 or > 20) throw new ToolException("Hysteresis must be 0-20 °C.");
        if (ResponseTimeUp is < 0 or > 60 || ResponseTimeDown is < 0 or > 60) throw new ToolException("Response time must be 0-60 s.");
        if (MinSpeed is < 0 or > 100 || MaxSpeed is < 0 or > 100) throw new ToolException("Speeds must be 0-100 %.");
        if (MinTemperature is < 0 or > 150 || MaxTemperature is < 0 or > 150) throw new ToolException("Temperatures must be 0-150 °C.");
        if (Percent is < 0 or > 100) throw new ToolException("percent must be 0-100.");
    }

    public IHysteresisConfig ApplyTo(IHysteresisConfig? current) => new SerializableHysteresisConfig
    {
        HysteresisValueUp = HysteresisUp ?? current?.HysteresisValueUp ?? 2,
        HysteresisValueDown = HysteresisDown ?? current?.HysteresisValueDown ?? 2,
        IgnoreHysteresisAtLimits = IgnoreHysteresisAtLimits ?? current?.IgnoreHysteresisAtLimits ?? true,
        ResponseTimeUp = ResponseTimeUp ?? current?.ResponseTimeUp ?? 3,
        ResponseTimeDown = ResponseTimeDown ?? current?.ResponseTimeDown ?? 5,
    };
}

/// <summary>Requested changes to a control's tuning; null members stay unchanged.</summary>
internal sealed record ControlTuning(int? StepUp, int? StepDown, int? Start, int? Stop, int? MinimumPercent, float? Offset);
