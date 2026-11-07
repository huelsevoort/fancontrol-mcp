using System.Text.Json.Nodes;

namespace FanControl.MCP;

/// <summary>
/// Checks a FanControl setup against the rules in Guides/fan-curve-best-practices.md.
/// Works purely on the JSON produced by EngineBridge (list_temperatures / list_controls / list_fan_curves),
/// so it is independent of FanControl internals and unit-testable.
/// </summary>
internal static class SetupAnalyzer
{
    private enum Kind { Unknown, Cpu, Gpu, Liquid, Virtual, Other }
    private enum Role { Unknown, Cpu, Pump, Radiator, Gpu, Case }

    private sealed record Source(string Id, string Name, Kind Kind, bool Smoothed, double? Value);
    private sealed record Finding(string Severity, string Target, string Code, string Issue, string Recommendation);

    // Temperatures at which a curve on this kind of source should already be at 100 %, and alarm levels.
    private static double FullSpeedBy(Kind k) => k switch { Kind.Cpu => 90, Kind.Gpu => 85, Kind.Liquid => 45, _ => double.NaN };
    private static double Alarm(Kind k) => k switch { Kind.Cpu => 92, Kind.Gpu => 85, Kind.Liquid => 50, _ => double.NaN };

    public static JsonObject Analyze(JsonArray temperatures, JsonArray controls, JsonArray curves)
    {
        var findings = new List<Finding>();
        var sources = BuildSources(temperatures);
        var curveByName = curves.OfType<JsonObject>()
            .Where(c => c["name"] is not null)
            .GroupBy(c => Str(c, "name")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // ---------------------------------------------------------- global: current temperatures
        foreach (var s in sources.Values.Where(s => s.Kind == Kind.Liquid && s.Value >= 50 && !s.Smoothed))
            findings.Add(new(s.Value >= 55 ? "critical" : "warning", s.Name, "LIQUID_HOT", $"Liquid temperature {s.Value:0.#} °C.", "AIO/loop components are typically rated to 60 °C: check pump operation and radiator fans (should be near 100 % here)."));
        foreach (var s in sources.Values.Where(s => s.Value is not null && s.Kind is Kind.Cpu or Kind.Gpu && !s.Smoothed))
        {
            if (s.Value >= Alarm(s.Kind))
                findings.Add(new("warning", s.Name, "TEMP_HIGH", $"{s.Name} is at {s.Value:0.#} °C right now.",
                    "Check that the related fans are near 100 % and that the cooler is mounted/working. Ryzen 7000/9000 at 90-95 °C under full load is by design."));
        }

        // ---------------------------------------------------------- curves
        var usedCurves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in controls.OfType<JsonObject>())
            if (Str(c, "fanCurve") is { } fc && Bool(c, "enabled") && !Bool(c, "manualControl")) usedCurves.Add(fc);
        foreach (var mix in curves.OfType<JsonObject>().Where(c => Str(c, "type") == "MixFanCurve"))
            foreach (var n in (mix["config"]?["selectedFanCurves"] as JsonArray ?? []).Select(x => x?.ToString()).OfType<string>())
                usedCurves.Add(n);

        var curveInfo = new Dictionary<string, (double? MinOut, double? MaxOut, Source? Source)>(StringComparer.OrdinalIgnoreCase);
        foreach (var curve in curves.OfType<JsonObject>())
        {
            var name = Str(curve, "name") ?? "?";
            var type = Str(curve, "type") ?? "";
            var cfg = curve["config"] as JsonObject ?? new JsonObject();
            var target = $"curve '{name}'";

            if (!usedCurves.Contains(name))
                findings.Add(new("info", target, "CURVE_UNUSED", "Not used by any enabled automatic control or mix curve.", "Delete it or assign it, to keep the setup understandable."));

            Source? src = null;
            if (Str(cfg, "selectedTempSource") is { } srcId)
            {
                if (!sources.TryGetValue(srcId, out src))
                    findings.Add(new("critical", target, "SOURCE_MISSING", $"Temperature source '{srcId}' does not exist (hardware removed or renamed?).", "Select an existing source with update_fan_curve."));
                else if (src.Value is null)
                    findings.Add(new("critical", target, "SOURCE_NO_VALUE", $"Temperature source '{src.Name}' currently has no value.", "Choose a working sensor; a curve on a dead sensor cannot react to heat."));
                else if (src.Kind == Kind.Virtual)
                    findings.Add(new("info", target, "SOURCE_VIRTUAL", $"Driven by the MCP virtual sensor '{src.Name}'.", "Make sure it is combined (Mix Max) with a real temperature curve so the fan still reacts to heat."));
            }
            else if (type is "GraphFanCurve" or "LinearFanCurve")
            {
                findings.Add(new("critical", target, "SOURCE_NONE", "No temperature source selected.", "Select a source with update_fan_curve (temperature_source)."));
            }

            double? minOut = null, maxOut = null, fullAt = null;
            if (type == "GraphFanCurve")
            {
                var pts = (cfg["points"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(p => (T: Num(p, "x") ?? 0, P: Num(p, "y") ?? 0)).OrderBy(p => p.T).ToList();
                if (pts.Count < 2)
                {
                    findings.Add(new("warning", target, "CURVE_FEW_POINTS", $"Only {pts.Count} point(s).", "Use 3-5 points: idle floor, working ramp, 100 % before the limit."));
                }
                if (pts.Count > 0)
                {
                    minOut = pts.Min(p => p.P);
                    maxOut = pts.Max(p => p.P);
                    fullAt = pts.FirstOrDefault(p => p.P >= 99.5) is { P: >= 99.5 } f ? f.T : null;
                    for (var i = 1; i < pts.Count; i++)
                    {
                        if (pts[i].P < pts[i - 1].P)
                            findings.Add(new("warning", target, "CURVE_NON_MONOTONIC", $"Speed drops from {pts[i - 1].P:0} % to {pts[i].P:0} % while temperature rises ({pts[i - 1].T:0}->{pts[i].T:0} °C).", "Make the curve non-decreasing."));
                        var dt = pts[i].T - pts[i - 1].T;
                        if (dt > 0 && (pts[i].P - pts[i - 1].P) / dt > 10)
                            findings.Add(new("info", target, "CURVE_STEEP", $"Very steep section {pts[i - 1].T:0}->{pts[i].T:0} °C ({(pts[i].P - pts[i - 1].P) / dt:0.#} %/°C).", "Steep sections near typical temperatures make fans audibly jump; spread the ramp over more degrees."));
                    }
                }
            }
            else if (type == "LinearFanCurve")
            {
                minOut = Num(cfg, "minimumFanSpeed");
                maxOut = Num(cfg, "maximumFanSpeed");
                fullAt = maxOut >= 99.5 ? Num(cfg, "maximumTemperature") : null;
            }
            else if (type == "FlatFanCurve")
            {
                minOut = maxOut = Num(cfg, "percent");
            }

            if (src is not null && src.Kind is Kind.Cpu or Kind.Gpu or Kind.Liquid && maxOut is not null)
            {
                var limit = FullSpeedBy(src.Kind);
                if (maxOut < 99.5)
                    findings.Add(new("warning", target, "CURVE_NO_FULL_SPEED", $"Never goes above {maxOut:0} % although it cools a {src.Kind} source.", $"Let it reach 100 % by about {limit:0} °C so there is headroom under heavy load."));
                else if (fullAt > limit)
                    findings.Add(new("warning", target, "CURVE_FULL_SPEED_LATE", $"Reaches 100 % only at {fullAt:0} °C.", $"Reach 100 % by about {limit:0} °C for a {src.Kind} source."));

                if (src.Value is { } v && v >= limit - 5 && Num(curve, "currentOutput") is { } outNow && outNow < 80)
                    findings.Add(new("critical", target, "CURVE_TOO_LOW_NOW", $"{src.Name} is {v:0.#} °C but the curve outputs only {outNow:0} %.", "Raise the upper part of the curve."));
            }

            if (type is "GraphFanCurve" or "LinearFanCurve" && cfg["hysteresisConfig"] is JsonObject h)
            {
                var hystUp = Num(h, "hysteresisValueUp") ?? 0;
                var hystDown = Num(h, "hysteresisValueDown") ?? 0;
                var rtUp = Num(h, "responseTimeUp") ?? 0;
                if (src is { Kind: Kind.Cpu, Smoothed: false } && rtUp < 3 && hystUp < 2)
                    findings.Add(new("warning", target, "CURVE_SPIKY_SOURCE", $"Uses raw CPU temperature '{src.Name}' with little smoothing (hysteresis {hystUp:0.#} °C, response {rtUp:0} s).",
                        "CPU temperatures spike constantly: use a time-average sensor (create_time_average_sensor, 5-10 s) or hysteresis 2-3 °C and response time 3-5 s."));
                else if (src is { Kind: Kind.Liquid } && (hystUp > 2 || rtUp > 5))
                    findings.Add(new("info", target, "CURVE_LIQUID_SLOW", "Liquid temperature is already slow; large hysteresis/response time delays the reaction further.", "Use hysteresis 0.5-1 °C and response time 1-2 s."));
                else if (hystUp == 0 && hystDown == 0 && rtUp <= 1 && src is not { Smoothed: true })
                    findings.Add(new("info", target, "CURVE_NO_HYSTERESIS", "No hysteresis and no response time.", "Hysteresis 2 °C and response time 3 s avoid ping-pong around a point."));
                if (h["ignoreHysteresisAtLimits"]?.GetValue<bool>() == false)
                    findings.Add(new("info", target, "CURVE_HYST_AT_LIMITS", "Hysteresis also applies at the limits.", "Enable 'ignore hysteresis at limits' so 100 % is reached without delay."));
            }

            curveInfo[name] = (minOut, maxOut, src);
        }

        // Mix curves: min/max over their members (Max mix -> max of mins is a lower bound; good enough for floor checks).
        foreach (var mix in curves.OfType<JsonObject>().Where(c => Str(c, "type") == "MixFanCurve"))
        {
            var members = (mix["config"]?["selectedFanCurves"] as JsonArray ?? []).Select(x => x?.ToString()).OfType<string>()
                .Where(curveInfo.ContainsKey).Select(n => curveInfo[n]).ToList();
            if (members.Count > 0)
                curveInfo[Str(mix, "name")!] = (members.Max(m => m.MinOut), members.Max(m => m.MaxOut), members.FirstOrDefault(m => m.Source is not null).Source);
        }

        // ---------------------------------------------------------- controls
        var curveUsers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in controls.OfType<JsonObject>())
        {
            var name = Str(c, "name") ?? Str(c, "id") ?? "?";
            var target = $"control '{name}'";
            var role = RoleOf(c);
            var enabled = Bool(c, "enabled");
            if (!enabled)
            {
                if (role is Role.Cpu or Role.Pump or Role.Radiator)
                    findings.Add(new("info", target, "CONTROL_DISABLED", "Disabled: speed is left to BIOS/hardware.", "Fine if the BIOS curve is good; otherwise enable it and assign a curve."));
                continue;
            }

            var stop = Num(c, "stop") ?? 0;
            var start = Num(c, "start") ?? 0;
            var minimum = Num(c, "minimumPercent") ?? 0;
            var stepUp = Num(c, "stepUp");
            var stepDown = Num(c, "stepDown");
            var critical = role is Role.Cpu or Role.Pump or Role.Radiator;

            if (Bool(c, "manualControl"))
            {
                var manual = Num(c, "manualPercent") ?? 0;
                if (critical && manual < (role == Role.Pump ? 60 : 30))
                    findings.Add(new("critical", target, "MANUAL_LOW", $"Fixed at {manual:0} % (manual) on a {role} output.", "Use an automatic curve (set_fan_automatic) or a higher fixed speed."));
                else
                    findings.Add(new("info", target, "MANUAL", $"Fixed at {manual:0} % (manual); does not react to temperature.", "Switch back with set_fan_automatic when the test/override is over."));
                continue;
            }

            var curveName = Str(c, "fanCurve");
            if (curveName is null)
            {
                findings.Add(new("warning", target, "NO_CURVE", "Enabled, automatic, but no fan curve selected.", "Assign a curve with set_fan_automatic."));
                continue;
            }
            if (!curveInfo.TryGetValue(curveName, out var info))
            {
                findings.Add(new("warning", target, "CURVE_NOT_FOUND", $"Selected fan curve '{curveName}' does not exist.", "Assign an existing curve."));
                continue;
            }
            curveUsers[curveName] = curveUsers.GetValueOrDefault(curveName) + 1;

            var floor = Math.Max(info.MinOut ?? 0, minimum);
            if (floor <= 0.5 && critical)
                findings.Add(new("critical", target, "ZERO_RPM_CRITICAL", $"Curve '{curveName}' can stop this {role} fan (0 %).", "Never stop CPU, pump or radiator fans: raise the curve floor or set minimum_percent."));
            else if (floor > 0.5 && stop > 0 && floor < stop + 2)
                findings.Add(new("warning", target, "BELOW_STOP", $"Lowest speed {floor:0} % is at/below the stall value ({stop:0} %).", $"Raise the floor to ≥ {stop + 5:0} % or set minimum_percent, otherwise the fan may stop/click."));
            else if (floor <= 0.5 && start > 0 && info.MaxOut is not null)
                findings.Add(new("info", target, "ZERO_RPM", $"Zero-RPM curve; fan restarts at {start:0} %.", "Fine for case/GPU fans if wanted; make sure the first non-zero point is ≥ the start value."));

            if (role == Role.Pump && floor < 60)
                findings.Add(new(floor <= 0.5 ? "critical" : "warning", target, "PUMP_LOW", $"Pump can drop to {floor:0} %.", "Pumps belong at a fixed speed: 100 % (NZXT/Arctic) or 70-80 % (e.g. Corsair), never below 60 % and never on a zero-RPM curve."));
            else if (role == Role.Pump && info.MinOut is { } pMin && info.MaxOut is { } pMax && pMax - pMin > 5)
                findings.Add(new("info", target, "PUMP_CURVE", $"Pump follows a curve ({pMin:0}-{pMax:0} %).", "A fixed pump speed is the vendor recommendation; let the radiator fans follow the liquid temperature instead."));

            if (role == Role.Gpu && c["id"]?.ToString()?.Contains("nvapi", StringComparison.OrdinalIgnoreCase) == true && info.MinOut is { } gMin && gMin > 0.5 && gMin < 29.5)
                findings.Add(new("warning", target, "NVIDIA_INVALID_RANGE", $"Curve goes down to {gMin:0} %; NVIDIA fans only accept 30-100 % (0 % = card's own fan-stop).", "Use ≥ 30 %, or 0 % below the card's stop temperature followed by a jump to ≥ 30 %."));

            if (role == Role.Radiator && info.Source is { Kind: Kind.Cpu })
                findings.Add(new("warning", target, "RADIATOR_ON_CPU", $"Radiator fans follow CPU temperature ('{info.Source.Name}').", "Use the liquid/coolant temperature as source; it changes slowly and avoids constant ramping."));

            if (role == Role.Cpu && info.Source is { Kind: Kind.Gpu })
                findings.Add(new("warning", target, "CPU_FAN_ON_GPU", "CPU cooler fan follows a GPU temperature.", "Use a (smoothed) CPU temperature."));

            if (stepUp is { } up && stepDown is { } down)
            {
                if (down > up)
                    findings.Add(new("info", target, "STEP_DOWN_FASTER", $"Slows down faster ({down:0} %/s) than it speeds up ({up:0} %/s).", "Speed up fast (5-10 %/s), slow down gently (2-5 %/s)."));
                else if (up >= 50)
                    findings.Add(new("info", target, "STEP_ABRUPT", $"Step up {up:0} %/s is very abrupt.", "5-10 %/s sounds smoother."));
            }

            if (c["pairedFanSensorId"] is null && role != Role.Gpu)
                findings.Add(new("info", target, "NOT_PAIRED", "No RPM sensor paired.", "Pair the RPM sensor in FanControl so calibration and stall detection work."));
            else if (start == 0 && stop == 0 && role != Role.Gpu)
                findings.Add(new("info", target, "NOT_CALIBRATED", "Start/stop values are 0 (probably never calibrated).", "Run FanControl's calibration to learn start/stop values."));
        }

        var order = new Dictionary<string, int> { ["critical"] = 0, ["warning"] = 1, ["info"] = 2 };
        var sorted = findings.OrderBy(f => order[f.Severity]).ToList();
        var arr = new JsonArray();
        foreach (var f in sorted)
            arr.Add(new JsonObject { ["severity"] = f.Severity, ["target"] = f.Target, ["code"] = f.Code, ["issue"] = f.Issue, ["recommendation"] = f.Recommendation });

        return new JsonObject
        {
            ["summary"] = new JsonObject
            {
                ["critical"] = sorted.Count(f => f.Severity == "critical"),
                ["warning"] = sorted.Count(f => f.Severity == "warning"),
                ["info"] = sorted.Count(f => f.Severity == "info"),
            },
            ["findings"] = arr,
            ["note"] = "Heuristic checks; roles (CPU/pump/radiator/case) are guessed from names. Read get_fan_curve_guide before changing anything and confirm changes with the user.",
        };
    }

    // ------------------------------------------------------------------ classification

    private static Dictionary<string, Source> BuildSources(JsonArray temperatures)
    {
        var raw = temperatures.OfType<JsonObject>().Where(t => Str(t, "id") is not null).ToList();
        var byId = raw.ToDictionary(t => Str(t, "id")!, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);

        Source Resolve(JsonObject t, int depth)
        {
            var id = Str(t, "id")!;
            if (result.TryGetValue(id, out var known)) return known;
            var name = Str(t, "name") ?? id;
            var value = Num(t, "value");
            var custom = Str(t, "customSensorType");
            var cfg = t["config"] as JsonObject;
            Source s;
            if (custom is not null && depth < 4)
            {
                var children = new List<Source>();
                if (Str(cfg, "selectedTempSource") is { } single && byId.TryGetValue(single, out var child)) children.Add(Resolve(child, depth + 1));
                foreach (var cid in (cfg?["selectedSensors"] as JsonArray ?? []).Select(x => x?.ToString()).OfType<string>())
                    if (byId.TryGetValue(cid, out var c2)) children.Add(Resolve(c2, depth + 1));

                var kind = children.Count == 0 ? KindOf(t) : children.Select(c => c.Kind).Distinct().Count() == 1 ? children[0].Kind : Kind.Other;
                if (children.Any(c => c.Kind == Kind.Cpu) && kind == Kind.Other) kind = Kind.Cpu; // CPU+GPU mix: treat as CPU (strictest smoothing need)
                var smoothed = custom == "TimeAverage" || (children.Count > 0 && children.All(c => c.Smoothed));
                s = new Source(id, name, kind, smoothed, value);
            }
            else
            {
                s = new Source(id, name, KindOf(t), false, value);
            }
            result[id] = s;
            return s;
        }

        foreach (var t in raw) Resolve(t, 0);
        return result;
    }

    private static Kind KindOf(JsonObject t)
    {
        var text = $"{Str(t, "id")} {Str(t, "name")} {Str(t, "originalName")} {Str(t, "origin")}".ToLowerInvariant();
        if (text.Contains("fancontrol.mcp/virtual")) return Kind.Virtual;
        if (Has(text, "liquid", "coolant", "water", "wasser", "kühlmittel")) return Kind.Liquid;
        if (Has(text, "/amdcpu", "/intelcpu", "ryzen", "core (tctl", "tctl", "tdie", "ccd", "cpu package", "intel core", "core ultra")) return Kind.Cpu;
        if (Has(text, "gpu", "nvapi", "adlx", "nvidia", "geforce", "radeon", "/gpu-")) return Kind.Gpu;
        if (text.Contains("cpu")) return Kind.Cpu;
        return Kind.Other;
    }

    private static Role RoleOf(JsonObject c)
    {
        var text = $"{Str(c, "name")} {Str(c, "originalName")} {Str(c, "id")} {Str(c, "origin")}".ToLowerInvariant();
        if (Has(text, "pump", "pumpe")) return Role.Pump;
        if (Has(text, "nvapi", "adlx", "geforce", "radeon", "gpu")) return Role.Gpu;
        if (Has(text, "radiator", "rad ", "aio")) return Role.Radiator;
        if (Has(text, "cpu")) return Role.Cpu;
        if (Has(text, "intake", "exhaust", "front", "top", "rear", "back", "case", "sys", "chassis", "gehäuse", "hinten", "vorne", "oben")) return Role.Case;
        return Role.Unknown;
    }

    private static bool Has(string text, params string[] needles) => needles.Any(text.Contains);

    private static string? Str(JsonNode? o, string key) => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Num(JsonNode? o, string key) => o?[key] is JsonValue v
        ? v.TryGetValue<double>(out var d) ? d : v.TryGetValue<float>(out var f) ? f : v.TryGetValue<int>(out var i) ? i : v.TryGetValue<long>(out var l) ? l : null
        : null;

    private static bool Bool(JsonNode? o, string key) => o?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
