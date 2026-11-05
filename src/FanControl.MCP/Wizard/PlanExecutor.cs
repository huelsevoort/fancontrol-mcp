using System.Text.Json.Nodes;
using FanControl.MCP.Engine;
using FanControl.MCP.Mcp;

namespace FanControl.MCP.Wizard;

/// <summary>Applies a plan from <see cref="BaselinePlanner"/>: creates a restore point, then executes actions in order.</summary>
internal static class PlanExecutor
{
    public static JsonObject Apply(EngineBridge bridge, JsonObject plan, Func<int, string?> safetyFloorViolation)
    {
        var restorePoint = bridge.CreateRestorePoint($"before baseline '{plan["goal"]}'");
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        var log = new JsonArray();
        var errors = new JsonArray();

        string Resolve(string source) => source.StartsWith('$')
            ? refs.TryGetValue(source[1..], out var id) ? id : throw new ToolException($"Plan references {source} before it was created.")
            : source;

        foreach (var action in (plan["actions"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var type = action["type"]?.ToString();
            try
            {
                switch (type)
                {
                    case "ensure_time_average":
                    {
                        var source = Resolve(action["source"]!.ToString());
                        var seconds = action["seconds"]!.GetValue<int>();
                        var existing = bridge.FindTimeAverageSensor(source, seconds);
                        if (existing is not null)
                        {
                            refs[action["key"]!.ToString()] = existing;
                            log.Add($"reuse time average '{existing}' ({seconds}s of {source})");
                        }
                        else
                        {
                            var created = bridge.CreateTimeAverageSensor(UniqueSensorName(bridge, action["name"]!.ToString()), source, seconds);
                            refs[action["key"]!.ToString()] = created["id"]!.ToString();
                            log.Add($"created time average '{created["id"]}' ({seconds}s of {source})");
                        }
                        break;
                    }
                    case "ensure_mix":
                    {
                        var sources = (action["sources"] as JsonArray ?? []).Select(s => Resolve(s!.ToString())).ToList();
                        var fn = action["function"]?.ToString() ?? "Max";
                        var existing = bridge.FindMixSensor(sources, fn);
                        if (existing is not null)
                        {
                            refs[action["key"]!.ToString()] = existing;
                            log.Add($"reuse mix sensor '{existing}'");
                        }
                        else
                        {
                            var created = bridge.CreateMixSensor(UniqueSensorName(bridge, action["name"]!.ToString()), sources, fn);
                            refs[action["key"]!.ToString()] = created["id"]!.ToString();
                            log.Add($"created mix sensor '{created["id"]}' ({fn} of {string.Join(", ", sources)})");
                        }
                        break;
                    }
                    case "upsert_curve":
                    {
                        var name = action["name"]!.ToString();
                        var curveType = action["curveType"]?.ToString() ?? "graph";
                        var change = new FanCurveChange
                        {
                            Points = (action["points"] as JsonArray)?.OfType<JsonArray>().Select(p => new CurvePoint(p[0]!.GetValue<double>(), p[1]!.GetValue<double>())).ToList(),
                            TemperatureSource = action["source"] is { } s ? Resolve(s.ToString()) : null,
                            HysteresisUp = Dbl(action["hysteresis_up"]),
                            HysteresisDown = Dbl(action["hysteresis_down"]),
                            ResponseTimeUp = (int?)Dbl(action["response_time_up"]),
                            ResponseTimeDown = (int?)Dbl(action["response_time_down"]),
                            IgnoreHysteresisAtLimits = action["ignore_hysteresis_at_limits"]?.GetValue<bool>(),
                            Percent = (float?)Dbl(action["percent"]),
                        };
                        var expected = curveType == "flat" ? "FlatFanCurve" : "GraphFanCurve";
                        var existingType = bridge.FanCurveKind(name);
                        if (existingType is null)
                        {
                            bridge.CreateFanCurve(name, curveType, change);
                            log.Add($"created {curveType} curve '{name}'");
                        }
                        else if (existingType == expected)
                        {
                            bridge.UpdateFanCurve(name, change);
                            log.Add($"updated {curveType} curve '{name}'");
                        }
                        else
                        {
                            throw new ToolException($"A curve named '{name}' exists with a different type ({existingType}); rename or delete it first.");
                        }
                        break;
                    }
                    case "assign":
                    {
                        var control = action["controlId"]!.ToString();
                        var min = (int)(Dbl(action["minimum_percent"]) ?? 0);
                        if (safetyFloorViolation(min) is { } violation) throw new ToolException(violation);
                        bridge.SetAutomatic(control, action["curve"]!.ToString());
                        bridge.UpdateControlTuning(control, new ControlTuning(
                            (int?)Dbl(action["step_up"]), (int?)Dbl(action["step_down"]), (int?)Dbl(action["start"]), (int?)Dbl(action["stop"]),
                            min, (float?)Dbl(action["offset"])));
                        log.Add($"'{action["controlName"]}' -> '{action["curve"]}' (min {min} %, offset {action["offset"]})");
                        break;
                    }
                    default:
                        throw new ToolException($"Unknown action type '{type}'.");
                }
            }
            catch (Exception e)
            {
                errors.Add($"{type} {action["name"] ?? action["controlName"]}: {e.Message}");
            }
        }

        return new JsonObject
        {
            ["applied"] = errors.Count == 0,
            ["restorePoint"] = restorePoint["id"]?.DeepClone(),
            ["log"] = log,
            ["errors"] = errors,
            ["note"] = errors.Count == 0
                ? "Applied in memory only. Validate under real load (start_measurement), then save_config if the user is happy; restore_restore_point undoes everything."
                : "Some actions failed; the rest was applied. restore_restore_point undoes everything.",
        };
    }

    private static string UniqueSensorName(EngineBridge bridge, string name)
    {
        var existing = bridge.Temperatures(true).OfType<JsonObject>().Select(t => t["name"]?.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(name)) return name;
        for (var i = 2; i < 100; i++)
            if (!existing.Contains($"{name} {i}")) return $"{name} {i}";
        return $"{name} {Guid.NewGuid():N}"[..40];
    }

    private static double? Dbl(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}
