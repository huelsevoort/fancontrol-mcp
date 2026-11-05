using System.Text.Json.Nodes;

namespace FanControl.MCP.Wizard;

/// <summary>
/// The guided setup ("wizard"). Stateless: progress is derived from the saved <see cref="SystemProfile"/>, so it works
/// in any MCP client (plain conversation), can be resumed later, and the same catalogue drives elicitation forms and
/// the MCP Apps card UI. Question texts are English; the assistant asks them in the user's language.
/// </summary>
internal static class SetupWizard
{
    public static readonly (string Id, string Label, string Description)[] Goals =
    [
        ("quiet_no_perf_loss", "Quietest without performance loss",
            "As quiet as possible while CPU/GPU never get hot enough to lose boost clocks. Temperatures may run warmer, performance stays at 100 %."),
        ("balanced", "Balanced",
            "Moderate noise with comfortable temperature headroom. Good default for mixed use."),
        ("max_performance", "Maximum cooling / performance",
            "Lowest temperatures and maximum boost headroom (overclocking, long renders, hot rooms). Noise is secondary."),
        ("silent", "Silent first",
            "Noise matters most; a small performance loss under heavy load is acceptable. Case fans may stop at idle."),
        ("constant", "Steady and predictable",
            "Few speed changes: fans move between a small number of steady levels (streaming, recording, sensitive to ramping)."),
    ];

    private static readonly (string Value, string Label)[] Positions =
    [
        ("front", "Front"), ("top_front", "Top, front position"), ("top_rear", "Top, rear position"), ("top", "Top (single/other)"),
        ("rear", "Rear"), ("bottom", "Bottom"), ("side", "Side panel"), ("radiator", "On a radiator (AIO/loop)"),
        ("cpu_cooler", "CPU air cooler"), ("pump", "Pump (AIO/loop)"), ("gpu", "Graphics card"), ("other", "Other / unknown"),
    ];

    /// <summary>Builds the wizard state: all steps with questions, prefilled values, completion and the next step.</summary>
    public static JsonObject State(JsonObject profile, JsonObject detection)
    {
        var steps = new JsonArray
        {
            Step("goal", "Goal", "Decides where curves end, how warm components may run and how aggressive smoothing is.", profile,
                ["goal.primary"],
                Q("goal.primary", "What should the setup optimise for?", "choice", Goals.Select(g => Opt(g.Id, g.Label, g.Description)).ToArray(), required: true),
                Q("goal.noiseSensitivity", "How sensitive are you to fan noise?", "choice",
                    [Opt("low", "Low", "Noise rarely bothers me"), Opt("medium", "Medium", ""), Opt("high", "High", "I notice every fan")]),
                Q("goal.annoyances", "Which fan behaviour bothers you most?", "multi",
                    [Opt("ramping", "Speeding up and down", ""), Opt("idle_noise", "Noise at idle / desktop", ""), Opt("load_noise", "Loud under load (gaming, rendering)", ""),
                     Opt("hum", "Humming / beating between fans", ""), Opt("whine", "Motor whine or clicking at low speed", ""), Opt("none", "Nothing specific", "")]),
                Q("goal.notes", "Anything else about your goal? (e.g. 'max quiet while gaming with headphones off')", "text", [])),

            Step("usage", "Usage & room", "Typical load pattern and room temperature set idle floors and summer headroom.", profile,
                ["usage.workloads", "environment.ambientC"],
                Q("usage.workloads", "What do you mainly use the PC for?", "multi",
                    [Opt("gaming", "Gaming", ""), Opt("office", "Office / browsing", ""), Opt("creative", "Rendering / encoding / compiling", ""),
                     Opt("ai", "AI / long GPU compute", ""), Opt("streaming", "Streaming / recording", ""), Opt("idle_server", "Always-on / server", "")]),
                Q("usage.titles", "Typical games or apps (optional, helps estimate CPU vs GPU load)", "text", []),
                Q("environment.ambientC", "Typical room temperature in °C", "number", [], min: 10, max: 40),
                Q("environment.summerAmbientC", "Room temperature on hot summer days in °C (optional)", "number", [], min: 10, max: 45),
                Q("environment.pcLocation", "Where does the PC stand?", "choice",
                    [Opt("desk", "On the desk", ""), Opt("floor", "On the floor", "More dust"), Opt("enclosed", "In a cabinet / enclosed desk", "Hot air can build up")])),

            Step("case", "Case", "Front panel type and size decide how much airflow fans can actually move.", profile,
                ["case.model", "case.frontPanel"],
                Q("case.model", "Which case is it (brand and model)?", "text", []),
                Q("case.size", "Case size", "choice",
                    [Opt("sff", "Small form factor / ITX", ""), Opt("mid", "Mid tower", ""), Opt("full", "Full tower", ""), Opt("other", "Other", "")]),
                Q("case.frontPanel", "Front panel", "choice",
                    [Opt("mesh", "Mesh / perforated", ""), Opt("solid_vents", "Solid with side vents", "Restrictive"), Opt("glass", "Glass / panoramic", "Restrictive, often side/bottom intake")]),
                Q("case.dustFilters", "Dust filters on intakes?", "choice", [Opt("yes", "Yes", ""), Opt("no", "No", ""), Opt("unknown", "Don't know", "")])),

            FansStep(profile, detection),

            Step("cpu_cooling", "CPU cooling", "Air coolers react in seconds, AIOs follow the slow liquid temperature: different curve sources.", profile,
                ["cpu.cooler.type"],
                Q("cpu.model", "CPU model (detected, confirm or correct)", "text", [], prefill: detection["cpu"]?["name"]?.DeepClone()),
                Q("cpu.cooler.type", "CPU cooler type", "choice",
                    [Opt("air", "Air cooler (tower/top-flow)", ""), Opt("aio", "AIO liquid cooler", ""), Opt("custom_loop", "Custom water loop", ""), Opt("stock", "Stock/boxed cooler", "")], required: true),
                Q("cpu.cooler.model", "Cooler model (e.g. 'Thermalright Peerless Assassin 120 SE', 'Arctic Liquid Freezer III 360')", "text", []),
                Q("cpu.cooler.radiatorSizeMm", "AIO/loop: radiator size in mm (240, 280, 360, 420)", "number", [], min: 120, max: 560),
                Q("cpu.cooler.radiatorPosition", "AIO/loop: where is the radiator?", "choice",
                    [Opt("front", "Front", ""), Opt("top", "Top", ""), Opt("side", "Side", ""), Opt("bottom", "Bottom", ""), Opt("rear", "Rear (120/140)", "")]),
                Q("cpu.cooler.radiatorDirection", "AIO/loop: radiator fans blow", "choice", [Opt("intake", "Into the case (intake)", ""), Opt("exhaust", "Out of the case (exhaust)", "")]),
                Q("cpu.cooler.pumpHeader", "AIO/loop: where is the pump connected?", "choice",
                    [Opt("aio_pump", "AIO_PUMP / W_PUMP header", ""), Opt("cpu_fan", "CPU_FAN header", ""), Opt("sata", "SATA power (fixed speed)", ""), Opt("usb_hub", "USB / vendor hub", ""), Opt("unknown", "Don't know", "")])),

            Step("gpu", "Graphics card", "Exact model and cooler type decide fan minimums and how warm it may run.", profile,
                ["gpu.model"],
                Q("gpu.model", "Graphics card model including the board partner (detected, confirm), e.g. 'MSI RTX 4070 Gaming X Trio'", "text", [],
                    prefill: detection["gpus"]?.AsArray().FirstOrDefault(g => g?["integrated"]?.GetValue<bool>() != true)?["name"]?.DeepClone()),
                Q("gpu.coolerType", "GPU cooler type", "choice",
                    [Opt("open_air", "Open-air (2-3 axial fans)", ""), Opt("blower", "Blower (single radial fan)", ""), Opt("liquid", "Water-cooled / hybrid", ""), Opt("passive", "Passive", "")]),
                Q("gpu.mount", "How is the GPU mounted?", "choice", [Opt("horizontal", "Normal (horizontal)", ""), Opt("vertical", "Vertical mount", "Check clearance to the side panel")]),
                Q("gpu.zeroRpmWanted", "Should the GPU fans stop at idle (zero-RPM) if the card supports it?", "choice", [Opt("yes", "Yes", ""), Opt("no", "No", "")])),

            Step("software", "BIOS & other software", "Another tool or a bad BIOS fallback can override or undermine FanControl.", profile,
                ["software.biosFallback"],
                Q("software.biosFallback", "What do the BIOS fan settings do if FanControl is not running?", "choice",
                    [Opt("sane_curve", "A sane BIOS curve", "Recommended fallback"), Opt("full_speed", "Full speed", "Safe but loud"), Opt("silent_bios", "Very quiet BIOS curve", "Risky if FanControl crashes"), Opt("unknown", "Don't know", "")], required: true),
                Q("software.headerModes", "Are the motherboard fan headers set to the right mode (PWM for 4-pin fans, DC for 3-pin)?", "choice",
                    [Opt("yes", "Yes", ""), Opt("auto", "Left on Auto", ""), Opt("unknown", "Don't know", "")]),
                Q("software.otherTools", "Other fan/RGB/GPU tools installed (detected ones are listed)", "text", [],
                    prefill: detection["fanSoftwareRunning"] is JsonArray sw && sw.Count > 0 ? string.Join(", ", sw.Select(s => s?["product"]?.ToString())) : null),
                Q("software.startWithWindows", "Does FanControl start with Windows (or run as service)?", "choice", [Opt("yes", "Yes", ""), Opt("no", "No", ""), Opt("unknown", "Don't know", "")])),
        };

        var next = steps.OfType<JsonObject>().FirstOrDefault(s => s["complete"]?.GetValue<bool>() != true);
        return new JsonObject
        {
            ["instructions"] =
                "Guide the user through the steps in order, one step at a time. Ask the step's questions in the user's language, " +
                "offer the options, show detected/prefilled values for confirmation and skip what the user does not know. " +
                "After each step call save_system_profile with the answers as a JSON patch using the question ids as paths " +
                "(e.g. {\"goal\":{\"primary\":\"quiet_no_perf_loss\"}}; fans as an array of objects keyed by controlId). " +
                "When all required answers are present, call propose_baseline_config, explain the plan, and apply it only after the user agrees. " +
                "Then validate under real load with start_measurement (guarded) before suggesting save_config.",
            ["complete"] = next is null,
            ["nextStep"] = next?["id"]?.DeepClone(),
            ["steps"] = steps,
            ["detected"] = Summary(detection),
        };
    }

    private static JsonObject FansStep(JsonObject profile, JsonObject detection)
    {
        var known = (profile["fans"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(f => f["controlId"] is not null).ToDictionary(f => f["controlId"]!.ToString(), StringComparer.OrdinalIgnoreCase);
        var rows = new JsonArray();
        var allDescribed = true;
        foreach (var h in (detection["controls"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var id = h["controlId"]?.ToString();
            if (id is null) continue;
            var entry = known.GetValueOrDefault(id);
            var connected = h["connected"]?.GetValue<bool?>();
            if (entry?["position"] is null && connected != false) allDescribed = false;
            rows.Add(new JsonObject
            {
                ["controlId"] = id,
                ["name"] = h["name"]?.DeepClone(),
                ["rpm"] = h["rpm"]?.DeepClone(),
                ["connected"] = connected,
                ["prefill"] = entry?.DeepClone() ?? Guess(h),
            });
        }

        return new JsonObject
        {
            ["id"] = "fans",
            ["title"] = "Fans & airflow",
            ["why"] = "Position and direction of every fan decide which temperature should drive it and whether the case has positive pressure.",
            ["complete"] = allDescribed && rows.Count > 0,
            ["questions"] = new JsonArray(new JsonObject
            {
                ["id"] = "fans",
                ["prompt"] = "For each fan output: where is it, which way does it blow, size, model and how many fans hang on it? " +
                             "Headers with 0 RPM are probably empty. Use FanControl's 'identify' if unsure which header is which.",
                ["type"] = "fan_table",
                ["columns"] = new JsonObject
                {
                    ["position"] = new JsonArray(Positions.Select(p => (JsonNode)new JsonObject { ["value"] = p.Value, ["label"] = p.Label }).ToArray()),
                    ["direction"] = new JsonArray(Opt("intake", "Intake", ""), Opt("exhaust", "Exhaust", ""), Opt("internal", "Internal (CPU/GPU/pump)", "")),
                    ["sizeMm"] = "number (92, 120, 140, 200 ...)",
                    ["model"] = "text, e.g. 'Arctic P12 PWM PST'",
                    ["count"] = "number of fans on this output (splitters/hubs)",
                },
                ["rows"] = rows,
            }),
        };
    }

    /// <summary>Best guess from FanControl names so the user only has to confirm.</summary>
    private static JsonObject Guess(JsonObject header)
    {
        var n = $"{header["name"]} {header["originalName"]} {header["controlId"]}".ToLowerInvariant();
        string? position = null, direction = null;
        if (n.Contains("pump")) { position = "pump"; direction = "internal"; }
        else if (n.Contains("nvapi") || n.Contains("adlx") || n.Contains("gpu") || n.Contains("geforce") || n.Contains("radeon")) { position = "gpu"; direction = "internal"; }
        else if (n.Contains("cpu")) { position = "cpu_cooler"; direction = "internal"; }
        else if (n.Contains("rad") || n.Contains("aio")) position = "radiator";
        else if (n.Contains("front")) position = "front";
        else if (n.Contains("rear") || n.Contains("back")) position = "rear";
        else if (n.Contains("bottom")) position = "bottom";
        else if (n.Contains("top")) position = "top";
        if (direction is null)
        {
            if (n.Contains("intake") || n.Contains(" in")) direction = "intake";
            else if (n.Contains("exhaust") || n.Contains(" out")) direction = "exhaust";
        }
        if (position == "top" && direction is not null && n.Contains("top")) position = direction == "intake" ? "top_front" : "top_rear";
        var o = new JsonObject { ["guessed"] = true };
        if (position is not null) o["position"] = position;
        if (direction is not null) o["direction"] = direction;
        return o;
    }

    private static JsonObject Summary(JsonObject d) => new()
    {
        ["cpu"] = d["cpu"]?["name"]?.DeepClone(),
        ["gpus"] = new JsonArray((d["gpus"] as JsonArray ?? []).Select(g => (JsonNode?)g?["name"]?.DeepClone()).ToArray()),
        ["mainboard"] = d["mainboard"]?.DeepClone(),
        ["liquidSensors"] = (d["liquidSensors"] as JsonArray)?.Count ?? 0,
        ["fanSoftwareRunning"] = d["fanSoftwareRunning"]?.DeepClone(),
    };

    // ------------------------------------------------------------------ builders

    private static JsonObject Step(string id, string title, string why, JsonObject profile, string[] requiredPaths, params JsonObject[] questions)
    {
        foreach (var q in questions)
        {
            var current = Get(profile, q["id"]!.ToString());
            if (current is not null) q["answer"] = current.DeepClone();
        }
        return new JsonObject
        {
            ["id"] = id,
            ["title"] = title,
            ["why"] = why,
            ["complete"] = requiredPaths.All(p => Get(profile, p) is not null),
            ["questions"] = new JsonArray(questions.Select(q => (JsonNode)q).ToArray()),
        };
    }

    private static JsonObject Q(string id, string prompt, string type, JsonObject[] options, bool required = false, double? min = null, double? max = null, JsonNode? prefill = null)
    {
        var q = new JsonObject { ["id"] = id, ["prompt"] = prompt, ["type"] = type };
        if (options.Length > 0) q["options"] = new JsonArray(options.Select(o => (JsonNode)o).ToArray());
        if (required) q["required"] = true;
        if (min is not null) q["min"] = min;
        if (max is not null) q["max"] = max;
        if (prefill is not null) q["prefill"] = prefill;
        return q;
    }

    private static JsonObject Opt(string value, string label, string hint)
    {
        var o = new JsonObject { ["value"] = value, ["label"] = label };
        if (hint.Length > 0) o["hint"] = hint;
        return o;
    }

    /// <summary>Reads a dotted path ("cpu.cooler.type") from the profile.</summary>
    public static JsonNode? Get(JsonObject root, string path)
    {
        JsonNode? node = root;
        foreach (var part in path.Split('.'))
        {
            node = node is JsonObject o ? o[part] : null;
            if (node is null) return null;
        }
        return node;
    }
}
