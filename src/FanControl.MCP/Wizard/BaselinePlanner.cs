using System.Globalization;
using System.Text.Json.Nodes;

namespace FanControl.MCP.Wizard;

/// <summary>
/// Turns the system profile + detected hardware + goal into a concrete, reviewable baseline configuration
/// (sensors to create, curves, control assignments, tuning). Pure function over JSON; nothing is applied here.
///
/// Curve shapes are expressed relative to each component's thermal limits (<see cref="ThermalLimits"/>):
/// N = temperature below which the component loses no performance, F = temperature where fans must be at 100 %.
/// For "quiet_no_perf_loss" the shapes reproduce what was validated with real load measurements
/// (Ryzen 9600X + RTX 2070 in Battlefield 6: full clocks, ~40 % quieter fans).
/// </summary>
internal static class BaselinePlanner
{
    public const string CurvePrefix = "MCP ";

    private sealed record GoalTuning(int StepUp, int StepDown, double HystUp, double HystDown, int RtUp, int RtDown, int CpuAvgSeconds, int GpuAvgSeconds, int CaseFloor, int CpuFloor);

    private static GoalTuning Tuning(string goal) => goal switch
    {
        "balanced" => new(10, 6, 2, 2, 2, 5, 5, 5, 30, 30),
        "max_performance" => new(20, 10, 1, 2, 1, 3, 3, 3, 40, 40),
        "silent" => new(5, 3, 3, 3, 5, 10, 10, 7, 0, 25),
        "constant" => new(5, 3, 4, 4, 8, 15, 10, 10, 35, 35),
        _ => new(8, 4, 2, 3, 3, 8, 7, 5, 20, 28), // quiet_no_perf_loss
    };

    public static JsonObject Plan(JsonObject profile, JsonObject detection, string? goalOverride = null)
    {
        var goal = goalOverride ?? SetupWizard.Get(profile, "goal.primary")?.ToString() ?? "quiet_no_perf_loss";
        if (!SetupWizard.Goals.Any(g => g.Id == goal)) goal = "quiet_no_perf_loss";
        var t = Tuning(goal);
        var ambient = Num(SetupWizard.Get(profile, "environment.ambientC")) ?? 22;
        var summerAmbient = Num(SetupWizard.Get(profile, "environment.summerAmbientC"));

        var actions = new JsonArray();
        var summary = new List<string>();
        var warnings = new List<string>();

        // ---------------------------------------------------------------- limits & sources
        var cpu = detection["cpu"] as JsonObject ?? new JsonObject();
        var gpu = (detection["gpus"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(g => g["integrated"]?.GetValue<bool>() != true);
        var cpuLim = Lim(cpu["limits"], 85, 92, 95);
        var gpuLim = Lim(gpu?["limits"], 80, 84, 86);
        var cpuTempId = cpu["primaryTemperatureId"]?.ToString();
        var gpuTempId = gpu?["temperatureId"]?.ToString();
        var liquidId = (detection["liquidSensors"] as JsonArray)?.OfType<JsonObject>().Select(l => l["id"]?.ToString()).FirstOrDefault(i => i is not null);
        var coolerType = SetupWizard.Get(profile, "cpu.cooler.type")?.ToString() ?? "air";
        var liquidCooled = coolerType is "aio" or "custom_loop";

        if (cpuTempId is null) warnings.Add("No CPU temperature sensor found; CPU-related curves were skipped.");
        if (summerAmbient is { } s && s - ambient >= 6)
            warnings.Add($"Summer room temperature ({s:0} °C) is {s - ambient:0} °C above normal: expect component temperatures to rise by about the same; curves already end at 100 % before the limits.");

        if (cpuTempId is not null)
            actions.Add(new JsonObject { ["type"] = "ensure_time_average", ["key"] = "cpuAvg", ["name"] = $"MCP CPU avg {t.CpuAvgSeconds}s", ["source"] = cpuTempId, ["seconds"] = t.CpuAvgSeconds });
        if (gpuTempId is not null)
            actions.Add(new JsonObject { ["type"] = "ensure_time_average", ["key"] = "gpuAvg", ["name"] = $"MCP GPU avg {t.GpuAvgSeconds}s", ["source"] = gpuTempId, ["seconds"] = t.GpuAvgSeconds });
        if (cpuTempId is not null && gpuTempId is not null)
        {
            actions.Add(new JsonObject { ["type"] = "ensure_mix", ["key"] = "caseMix", ["name"] = "MCP CPU/GPU max", ["sources"] = new JsonArray("$cpuAvg", "$gpuAvg"), ["function"] = "Max" });
            summary.Add("Case fans follow the hotter of CPU and GPU (both smoothed), so they react to gaming GPU heat as well as CPU load.");
        }
        var caseSource = cpuTempId is not null && gpuTempId is not null ? "$caseMix" : cpuTempId is not null ? "$cpuAvg" : gpuTempId is not null ? "$gpuAvg" : null;
        var caseN = Math.Min(cpuLim.N, gpu is null ? cpuLim.N : gpuLim.N);

        // ---------------------------------------------------------------- fans: merge profile with detection
        var fans = Fans(profile, detection);
        var floorByControl = fans.ToDictionary(f => f.ControlId, f => f.Stop > 0 ? (int)Math.Ceiling(f.Stop) + 5 : 0, StringComparer.OrdinalIgnoreCase);
        int Floor(IEnumerable<FanInfo> group, int goalFloor) => Math.Max(goalFloor, group.Select(f => floorByControl[f.ControlId]).DefaultIfEmpty(0).Max());

        var cpuFans = fans.Where(f => f.Position == "cpu_cooler").ToList();
        var pumps = fans.Where(f => f.Position == "pump").ToList();
        var radiatorFans = fans.Where(f => f.Position == "radiator").ToList();
        var gpuFans = fans.Where(f => f.Position == "gpu").ToList();
        var caseFans = fans.Where(f => f.Position is not ("cpu_cooler" or "pump" or "radiator" or "gpu")).ToList();
        var skipped = fans.Where(f => !f.Connected).ToList();
        caseFans.RemoveAll(f => !f.Connected);
        cpuFans.RemoveAll(f => !f.Connected);
        radiatorFans.RemoveAll(f => !f.Connected);
        if (skipped.Count > 0) summary.Add($"Left untouched (0 RPM, probably empty): {string.Join(", ", skipped.Select(f => f.Name))}.");

        // ---------------------------------------------------------------- CPU cooler fans
        if (cpuTempId is not null && cpuFans.Count > 0)
        {
            var floor = Floor(cpuFans, t.CpuFloor);
            AddCurve(actions, "MCP CPU", "$cpuAvg", ComponentShape(goal, cpuLim, floor), t.HystUp, t.HystDown, t.RtUp, t.RtDown);
            foreach (var f in cpuFans) Assign(actions, f, "MCP CPU", t, Math.Max(floor - 5, 0), 0);
            summary.Add($"CPU cooler: {floor} % floor, 100 % by {cpuLim.F:0} °C ({cpuLim.Family}: no clock loss below ~{cpuLim.N:0} °C).");
        }

        // ---------------------------------------------------------------- liquid cooling
        if (liquidCooled || pumps.Count > 0 || radiatorFans.Count > 0)
        {
            if (pumps.Count > 0)
            {
                // NZXT and Arctic ask for 100 % (low speed shortens pump life); Corsair allows a fixed 70-80 %. Never below 60 %.
                var coolerModel = SetupWizard.Get(profile, "cpu.cooler.model")?.ToString() ?? "";
                var fullOnly = coolerModel.Contains("nzxt", StringComparison.OrdinalIgnoreCase) || coolerModel.Contains("kraken", StringComparison.OrdinalIgnoreCase)
                               || coolerModel.Contains("arctic", StringComparison.OrdinalIgnoreCase) || coolerModel.Contains("freezer", StringComparison.OrdinalIgnoreCase);
                var pumpPct = fullOnly || goal is "max_performance" or "balanced" ? 100 : 80;
                actions.Add(new JsonObject { ["type"] = "upsert_curve", ["name"] = "MCP Pump", ["curveType"] = "flat", ["percent"] = pumpPct });
                foreach (var p in pumps) Assign(actions, p, "MCP Pump", t with { StepDown = 10 }, 60, 0);
                summary.Add($"Pump: constant {pumpPct} % (pumps belong at a fixed high speed, never on a temperature curve below 60 %).");
                if (pumpPct < 100) warnings.Add("Pump at 80 % for less noise (allowed by e.g. Corsair, costs ~1-2 °C). If your AIO's manual asks for 100 %, use 100 %.");
            }
            if (radiatorFans.Count > 0)
            {
                var floor = Floor(radiatorFans, Math.Max(t.CaseFloor, 20));
                if (liquidId is not null)
                {
                    AddCurve(actions, "MCP Radiator", liquidId, LiquidShape(goal, ambient, floor), 1, 1, 2, 3);
                    summary.Add($"Radiator fans follow the liquid temperature ({ambient + 4:0} -> {LiquidFull(goal, ambient):0} °C), not the spiky CPU temperature.");
                }
                else if (cpuTempId is not null)
                {
                    actions.Add(new JsonObject { ["type"] = "ensure_time_average", ["key"] = "cpuSlow", ["name"] = "MCP CPU avg 45s", ["source"] = cpuTempId, ["seconds"] = 45 });
                    AddCurve(actions, "MCP Radiator", "$cpuSlow", ComponentShape(goal, cpuLim, floor), 4, 4, 5, 10);
                    warnings.Add("No liquid temperature is visible to FanControl. Radiator fans fall back to a 45 s CPU average with 4 °C hysteresis. Better: a plugin that exposes the coolant sensor (FanControl.CorsairLink, FanControl.NzxtKraken, AquacomputerDevices, Liquidctl, HWInfo) or a 10K thermistor on a T_SENSOR header.");
                }
                foreach (var f in radiatorFans) Assign(actions, f, "MCP Radiator", t, Math.Max(floor - 5, 0), 0);
            }
            var radPos = SetupWizard.Get(profile, "cpu.cooler.radiatorPosition")?.ToString();
            var radDir = SetupWizard.Get(profile, "cpu.cooler.radiatorDirection")?.ToString();
            if (liquidCooled && radPos == "top" && radDir == "intake")
                warnings.Add("Top-mounted radiator as intake works against rising heat; top exhaust is the usual default.");
            if (liquidCooled && radPos == "bottom")
                warnings.Add("Bottom-mounted radiator: the pump is then the highest point of the loop and collects air (noise, wear). Move the radiator to the top or front.");
            if (liquidCooled && radPos == "front" && radDir == "intake")
                summary.Add("Front intake radiator: best for the CPU, but warms the case air (GPU typically +6-7 °C).");
        }

        // ---------------------------------------------------------------- GPU
        if (gpuTempId is not null && gpuFans.Count > 0)
        {
            var vendorMin = gpu?["vendor"]?.ToString() == "nvidia" ? 30 : 20;
            var floor = Math.Max(vendorMin, (int)Math.Ceiling(gpuFans.Max(f => f.Stop))); // the GPU's stop value is the card minimum, no margin needed
            var hystUp = goal == "quiet_no_perf_loss" ? 1 : t.HystUp; // measured: 2 °C hysteresis let the GPU fan lag near its limit
            var shape = ComponentShape(goal, gpuLim, floor);
            var zeroRpm = SetupWizard.Get(profile, "gpu.zeroRpmWanted")?.ToString() == "yes" && gpu?["vendor"]?.ToString() == "nvidia";
            if (zeroRpm)
            {
                // NVIDIA: 0 % hands control back to the card's own fan-stop; then jump straight to >= 30 % (1-29 % is invalid).
                var stopAt = Math.Min(shape[0].T, 50);
                shape = [(stopAt - 1, 0), (stopAt, floor), .. shape.Where(p => p.T > stopAt)];
                summary.Add($"GPU fan-stop below {stopAt - 1:0} °C via NVIDIA's 0 % override, then {floor} % and up.");
            }
            if (gpu?["vendor"]?.ToString() == "amd")
            {
                warnings.Add("AMD GPU: applying any FanControl fan setting resets Adrenalin overclock/undervolt settings, and ADLX control is unreliable in service mode and around sleep. GPU fan control was left out; the plan only reads its temperature. Enable it manually if you accept that.");
            }
            else
            {
                AddCurve(actions, "MCP GPU", "$gpuAvg", shape, hystUp, Math.Min(t.HystDown, 2), t.RtUp, Math.Min(t.RtDown, 6));
                foreach (var f in gpuFans) Assign(actions, f, "MCP GPU", t, zeroRpm ? 0 : floor, 0, zeroRpm ? floor : null);
                summary.Add($"GPU: {floor} % floor (card minimum), 100 % by {gpuLim.F:0} °C ({gpuLim.Family}: no clock loss below ~{gpuLim.N:0} °C).");
            }
        }

        // ---------------------------------------------------------------- case fans + pressure
        if (caseSource is not null && caseFans.Count > 0)
        {
            var allowZero = goal == "silent";
            var floor = Floor(caseFans, allowZero ? 0 : t.CaseFloor);
            // Zero-RPM needs >= 5 °C between stop and start and a long response time, or fans cycle on and off.
            AddCurve(actions, "MCP Case", caseSource, CaseShape(goal, caseN, floor), allowZero ? 5 : t.HystUp, allowZero ? 5 : t.HystDown, allowZero ? 15 : t.RtUp, allowZero ? 20 : t.RtDown);

            var intakes = caseFans.Where(f => f.Direction == "intake").Sum(f => f.Count);
            var exhausts = caseFans.Where(f => f.Direction == "exhaust").Sum(f => f.Count);
            var intakeOffset = 0; var exhaustOffset = 0;
            if (intakes > 0 && exhausts > 0)
            {
                if (intakes >= exhausts) exhaustOffset = -5;
                else { intakeOffset = 5; warnings.Add($"More exhaust ({exhausts}) than intake ({intakes}) fans: negative pressure pulls dust through unfiltered gaps. Intakes run 5 % faster to compensate; consider flipping a fan to intake."); }
                summary.Add($"Slight positive pressure: {intakes} intake vs {exhausts} exhaust fan(s), {(exhaustOffset != 0 ? "exhausts -5 %" : "intakes +5 %")}.");
            }
            else if (exhausts == 0 && intakes > 0) warnings.Add("No exhaust fan: hot air can only leave passively. One rear exhaust usually helps a lot.");
            else if (intakes == 0 && exhausts > 0) warnings.Add("No intake fan: the case runs at negative pressure and collects dust.");

            if (coolerType is "air" or "stock" && caseFans.Any(f => f.Position == "top_front" && f.Direction == "exhaust") && caseFans.Any(f => f.Position == "front" && f.Direction == "intake"))
                warnings.Add("With an air cooler, a top-front exhaust pulls the fresh front air straight out before it reaches the cooler (short circuit). Make it an intake or switch it off, as long as a rear/top-rear exhaust remains.");
            if (caseFans.Any(f => f.Position == "bottom" && f.Direction == "exhaust"))
                warnings.Add("Bottom fan as exhaust works against rising heat and feeds the GPU less; bottom fans should be intakes (they help GPU temperatures a lot).");
            var similar = caseFans.GroupBy(f => f.Direction).Where(g => g.Count() >= 2).ToList();
            if (similar.Count > 0 && goal is "quiet_no_perf_loss" or "silent")
                summary.Add("If identical fans hum or beat together, offset one of them by ~50 RPM (offset 2-3 %).");

            foreach (var f in caseFans)
            {
                var offset = f.Direction == "intake" ? intakeOffset : f.Direction == "exhaust" ? exhaustOffset : 0;
                Assign(actions, f, "MCP Case", t, allowZero ? 0 : Math.Max(floor - 5, 0), offset);
            }
            summary.Add($"Case fans: {(allowZero ? "may stop at idle" : $"{floor} % floor")}, reach 100 % around {caseN + 10:0} °C of the hotter component.");
        }

        if (gpu is null) warnings.Add("No dedicated GPU detected; GPU curves skipped.");
        if (detection["fanSoftwareRunning"] is JsonArray sw && sw.Count > 0)
            warnings.Add($"Other fan software is running ({string.Join(", ", sw.Select(x => x?["product"]))}). Make sure only FanControl controls these fans.");
        if (SetupWizard.Get(profile, "software.biosFallback")?.ToString() == "silent_bios")
            warnings.Add("The BIOS fallback curve is very quiet: if FanControl ever stops, components can run hot. A moderate BIOS curve is a safer fallback.");

        return new JsonObject
        {
            ["goal"] = goal,
            ["goalLabel"] = SetupWizard.Goals.First(g => g.Id == goal).Label,
            ["summary"] = new JsonArray(summary.Select(x => (JsonNode)x).ToArray()),
            ["warnings"] = new JsonArray(warnings.Select(x => (JsonNode)x).ToArray()),
            ["actions"] = actions,
            ["nextSteps"] = "Explain the plan, apply it with apply_baseline_config after the user agrees (a restore point is created automatically), " +
                            "then validate under the user's real load with start_measurement before save_config.",
        };
    }

    // ------------------------------------------------------------------ shapes

    private sealed record L(string Family, double N, double F, double Alarm);

    private static L Lim(JsonNode? node, double n, double f, double a) => new(
        node?["family"]?.ToString() ?? "default", Num(node?["noLossBelow"]) ?? n, Num(node?["fullSpeedBy"]) ?? f, Num(node?["alarm"]) ?? a);

    /// <summary>Curve for a fan that cools one component directly (CPU cooler, GPU).</summary>
    private static List<(double T, double P)> ComponentShape(string goal, L l, int floor) => Normalize(goal switch
    {
        "balanced" => [(l.N - 40, floor + 5), (l.N - 25, floor + 15), (l.N - 12, floor + 30), (l.N - 5, 75), (l.F - 4, 100)],
        "max_performance" => [(l.N - 50, Math.Max(floor, 40)), (l.N - 35, 55), (l.N - 20, 75), (l.N - 10, 90), (l.N - 5, 100)],
        "silent" => [(l.N - 25, floor), (l.N - 8, floor + 8), (l.N, floor + 22), (l.F, 70), (l.Alarm - 1, 100)],
        "constant" => [(l.N - 30, floor + 10), (l.N - 12, floor + 10), (l.N - 10, floor + 25), (l.N - 2, floor + 25), (l.N, 85), (l.F, 100)],
        _ => [(l.N - 35, floor), (l.N - 20, floor + 8), (l.N - 8, floor + 20), (l.N - 3, floor + 33), (l.F - 3, 85), (l.F, 100)],
    });

    /// <summary>Curve for case fans on the hotter of CPU/GPU; N is the lower of both components' no-loss temperatures.</summary>
    private static List<(double T, double P)> CaseShape(string goal, double n, int floor) => Normalize(goal switch
    {
        "balanced" => [(n - 40, floor), (n - 25, floor + 10), (n - 12, floor + 25), (n - 3, 70), (n + 5, 100)],
        "max_performance" => [(n - 45, Math.Max(floor, 40)), (n - 30, 55), (n - 18, 75), (n - 8, 100)],
        "silent" => [(n - 25, floor), (n - 10, floor + 10), (n, floor + 25), (n + 8, 60), (n + 14, 100)],
        "constant" => [(n - 30, floor), (n - 10, floor), (n - 8, floor + 15), (n + 2, floor + 15), (n + 4, 75), (n + 10, 100)],
        _ => [(n - 35, floor), (n - 20, floor + 5), (n - 8, floor + 15), (n, floor + 30), (n + 6, 75), (n + 10, 100)],
    });

    private static double LiquidFull(string goal, double ambient) => goal switch
    {
        "max_performance" => Math.Min(ambient + 10, 40),
        "balanced" or "constant" => Math.Min(ambient + 13, 43),
        "silent" => Math.Min(ambient + 18, 48),
        _ => Math.Min(ambient + 16, 45),
    };

    private static List<(double T, double P)> LiquidShape(string goal, double ambient, int floor)
    {
        var start = ambient + 4;
        var full = Math.Max(LiquidFull(goal, ambient), start + 6);
        return Normalize([(start - 2, floor), (start + 3, floor + 10), (full - 4, 60), (full, 100)]);
    }

    /// <summary>Rounds, clamps, removes duplicate temperatures and enforces a non-decreasing speed.</summary>
    private static List<(double T, double P)> Normalize(List<(double T, double P)> points)
    {
        var result = new List<(double T, double P)>();
        foreach (var (t0, p0) in points.OrderBy(p => p.T))
        {
            var t = Math.Round(Math.Clamp(t0, 0, 120));
            var p = Math.Round(Math.Clamp(p0, 0, 100));
            if (result.Count > 0)
            {
                if (t <= result[^1].T) t = result[^1].T + 1;
                p = Math.Max(p, result[^1].P);
            }
            result.Add((t, p));
        }
        return result;
    }

    // ------------------------------------------------------------------ actions

    private static void AddCurve(JsonArray actions, string name, string source, List<(double T, double P)> points, double hystUp, double hystDown, int rtUp, int rtDown)
    {
        actions.Add(new JsonObject
        {
            ["type"] = "upsert_curve",
            ["name"] = name,
            ["curveType"] = "graph",
            ["source"] = source,
            ["points"] = new JsonArray(points.Select(p => (JsonNode)new JsonArray(p.T, p.P)).ToArray()),
            ["hysteresis_up"] = hystUp,
            ["hysteresis_down"] = hystDown,
            ["response_time_up"] = rtUp,
            ["response_time_down"] = rtDown,
            ["ignore_hysteresis_at_limits"] = true,
        });
    }

    private static void Assign(JsonArray actions, FanInfo fan, string curve, GoalTuning t, int minimumPercent, int offset, int? startStop = null)
    {
        var a = new JsonObject
        {
            ["type"] = "assign",
            ["controlId"] = fan.ControlId,
            ["controlName"] = fan.Name,
            ["curve"] = curve,
            ["step_up"] = t.StepUp,
            ["step_down"] = t.StepDown,
            ["minimum_percent"] = minimumPercent,
            ["offset"] = offset,
        };
        if (startStop is { } s) { a["start"] = s; a["stop"] = s; }
        actions.Add(a);
    }

    private sealed record FanInfo(string ControlId, string Name, string Position, string Direction, int Count, double Stop, bool Connected);

    private static List<FanInfo> Fans(JsonObject profile, JsonObject detection)
    {
        var described = (profile["fans"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(f => f["controlId"] is not null).ToDictionary(f => f["controlId"]!.ToString(), StringComparer.OrdinalIgnoreCase);
        var controls = (detection["controls"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var list = new List<FanInfo>();
        foreach (var c in controls)
        {
            var id = c["controlId"]?.ToString();
            if (id is null) continue;
            var d = described.GetValueOrDefault(id);
            var position = d?["position"]?.ToString() ?? GuessPosition(c);
            var direction = d?["direction"]?.ToString() ?? (position is "cpu_cooler" or "pump" or "gpu" ? "internal" : GuessDirection(c));
            var count = (int)(Num(d?["count"]) ?? 1);
            var stop = Num(c["stop"]) ?? 0;
            var connected = c["connected"]?.GetValue<bool?>() != false;
            list.Add(new FanInfo(id, c["name"]?.ToString() ?? id, position, direction, Math.Max(count, 1), stop, connected));
        }
        return list;
    }

    private static string GuessPosition(JsonObject c)
    {
        var n = $"{c["name"]} {c["originalName"]} {c["controlId"]}".ToLowerInvariant();
        if (n.Contains("pump")) return "pump";
        if (n.Contains("nvapi") || n.Contains("adlx") || n.Contains("gpu")) return "gpu";
        if (n.Contains("cpu")) return "cpu_cooler";
        if (n.Contains("rad") || n.Contains("aio")) return "radiator";
        return "other";
    }

    private static string GuessDirection(JsonObject c)
    {
        var n = $"{c["name"]}".ToLowerInvariant();
        return n.Contains("intake") ? "intake" : n.Contains("exhaust") ? "exhaust" : "unknown";
    }

    private static double? Num(JsonNode? n) => n is JsonValue v
        ? v.TryGetValue<double>(out var d) ? d : v.TryGetValue<int>(out var i) ? i : v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : null
        : null;
}
