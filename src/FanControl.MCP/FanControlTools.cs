using System.Text.Json.Nodes;
using FanControl.MCP.Engine;
using FanControl.MCP.Mcp;

namespace FanControl.MCP;

/// <summary>Source of the engine bridge; returns null plus a reason when FanControl's engine is unreachable.</summary>
internal interface IEngineProvider
{
    EngineBridge? TryGetEngine(out string reason);
    string Mode { get; }
}

/// <summary>Definitions of all MCP tools offered by the plugin.</summary>
internal static class FanControlTools
{
    public const string Instructions =
        """
        This server controls FanControl (https://getfancontrol.com), the Windows fan control software, on the user's PC.
        Typical flow: call get_snapshot to see temperatures, fan speeds and controls, then act.
        For a first-time or complete setup ("set up my fans", "optimise everything"), run the guided setup: setup_wizard ->
        interview the user step by step -> save_system_profile -> propose_baseline_config -> confirm -> apply_baseline_config ->
        validate under real load with start_measurement / mark_phase / get_measurement_summary -> save_config when the user agrees.
        Controls are the fan/pump outputs FanControl drives (0-100 %). A control either follows its fan curve
        (automatic) or a fixed manual percentage. Changes take effect within about a second but are NOT written to
        the configuration file until save_config is called; loading a config or restarting FanControl reverts them.
        Never stop CPU/GPU/pump fans or set very low speeds while temperatures are high. Prefer set_fan_automatic to
        hand control back to FanControl when you are done. Virtual sensors ("MCP Virtual N") are temperature sources
        you can set; the user can reference them in fan curves (e.g. a 'boost' curve).
        Before proposing or changing fan curves, sources, hysteresis or control tuning, ALWAYS read the built-in
        best-practice guide (get_fan_curve_guide, also available as resource fancontrol://guides/fan-curve-best-practices)
        and run analyze_fan_setup. Propose changes and get the user's confirmation before writing.
        """;

    private static JsonArray? PointsArg(JsonObject a)
    {
        if (a["points"] is null) return null;
        return a["points"] as JsonArray ?? throw new ToolException("'points' must be an array of {temperature, percent} objects.");
    }

    private static FanCurveChange CurveChange(JsonObject a)
    {
        List<CurvePoint>? points = null;
        if (PointsArg(a) is { } arr)
        {
            points = [];
            foreach (var node in arr)
            {
                switch (node)
                {
                    case JsonObject p:
                        points.Add(new CurvePoint(
                            Arg.OptFloat(p, "temperature") ?? throw new ToolException("Each point needs 'temperature'."),
                            Arg.OptFloat(p, "percent") ?? throw new ToolException("Each point needs 'percent'.")));
                        break;
                    case JsonArray { Count: 2 } pair:
                        points.Add(new CurvePoint(pair[0]!.GetValue<double>(), pair[1]!.GetValue<double>()));
                        break;
                    default:
                        throw new ToolException("Each point must be {\"temperature\": 50, \"percent\": 40} or [50, 40].");
                }
            }
        }
        return new FanCurveChange
        {
            Points = points,
            TemperatureSource = Arg.OptString(a, "temperature_source"),
            HysteresisUp = Arg.OptFloat(a, "hysteresis_up"),
            HysteresisDown = Arg.OptFloat(a, "hysteresis_down"),
            ResponseTimeUp = Arg.OptIntOrNull(a, "response_time_up"),
            ResponseTimeDown = Arg.OptIntOrNull(a, "response_time_down"),
            IgnoreHysteresisAtLimits = Arg.OptBool(a, "ignore_hysteresis_at_limits"),
            MinTemperature = Arg.OptFloat(a, "min_temperature"),
            MaxTemperature = Arg.OptFloat(a, "max_temperature"),
            MinSpeed = Arg.OptIntOrNull(a, "min_speed"),
            MaxSpeed = Arg.OptIntOrNull(a, "max_speed"),
            Percent = Arg.OptFloat(a, "percent"),
        };
    }

    private static (string, JsonObject, bool)[] CurveProperties() =>
    [
        ("points", new JsonObject
        {
            ["type"] = "array",
            ["description"] = "Graph curves: 2-16 points sorted by temperature, speed non-decreasing. Each {temperature: °C, percent: 0-100}.",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["temperature"] = new JsonObject { ["type"] = "number" },
                    ["percent"] = new JsonObject { ["type"] = "number", ["minimum"] = 0, ["maximum"] = 100 },
                },
                ["required"] = new JsonArray("temperature", "percent"),
            },
        }, false),
        ("temperature_source", Schema.String("Graph/linear: temperature sensor id (preferred) or name from list_temperatures."), false),
        ("hysteresis_up", Schema.Number("Graph/linear: hysteresis in °C when rising (typ. 2-3, liquid 0.5-1)."), false),
        ("hysteresis_down", Schema.Number("Graph/linear: hysteresis in °C when falling."), false),
        ("response_time_up", Schema.Integer("Graph/linear: seconds the temperature must stay above before speeding up (typ. 3-5).", 0, 60), false),
        ("response_time_down", Schema.Integer("Graph/linear: seconds before slowing down (typ. 5-10).", 0, 60), false),
        ("ignore_hysteresis_at_limits", Schema.Boolean("Graph/linear: reach 0/100 % without hysteresis delay (recommended true)."), false),
        ("min_temperature", Schema.Number("Linear: temperature where the ramp starts."), false),
        ("max_temperature", Schema.Number("Linear: temperature where max_speed is reached."), false),
        ("min_speed", Schema.Integer("Linear: speed % below min_temperature.", 0, 100), false),
        ("max_speed", Schema.Integer("Linear: speed % at max_temperature.", 0, 100), false),
        ("percent", Schema.Number("Flat: constant speed %."), false),
    ];

    public static IReadOnlyList<McpTool> Create(IEngineProvider engines, PluginSettings settings, IReadOnlyList<VirtualSensor> virtualSensors)
    {
        EngineBridge Engine()
            => engines.TryGetEngine(out var reason) ?? throw new ToolException("FanControl engine not reachable: " + reason);

        void RequireWrite()
        {
            if (!settings.AllowWrite)
                throw new ToolException($"Write access is disabled (allowWrite=false in {settings.LoadedFrom}).");
        }

        JsonArray VirtualJson()
        {
            var arr = new JsonArray();
            foreach (var v in virtualSensors)
                arr.Add(new JsonObject { ["index"] = v.Index, ["id"] = v.Id, ["name"] = v.Name, ["value"] = v.Value });
            return arr;
        }

        var includeHidden = Schema.Object(("include_hidden", Schema.Boolean("Also list sensors hidden in the FanControl UI. Default false."), false));

        return
        [
            new McpTool("get_status", "Plugin status",
                "Status of the FanControl MCP plugin: version, run mode (desktop app or Windows service), whether FanControl's engine is reachable, current configuration, write permission.",
                Schema.Object(), ReadOnly: true,
                _ =>
                {
                    var o = new JsonObject
                    {
                        ["pluginVersion"] = typeof(FanControlTools).Assembly.GetName().Version?.ToString(3),
                        ["fanControlVersion"] = typeof(FanControl.Domain.Engine.IFanControlEngine).Assembly.GetName().Version?.ToString(),
                        ["mode"] = engines.Mode,
                        ["allowWrite"] = settings.AllowWrite,
                        ["minimumManualPercent"] = settings.MinimumManualPercent,
                        ["virtualSensors"] = VirtualJson(),
                    };
                    var engine = engines.TryGetEngine(out var reason);
                    if (engine is null) { o["engineAttached"] = false; o["engineError"] = reason; }
                    else foreach (var (k, v) in engine.Status()) o[k] = v?.DeepClone();
                    return o;
                }),

            new McpTool("get_snapshot", "Snapshot of all sensors",
                "One-call overview: all visible temperatures (incl. custom sensors), fan speeds (RPM), controls with their current %, mode and fan curve, plus the plugin's virtual sensors. Start here.",
                Schema.Object(), ReadOnly: true,
                _ =>
                {
                    var e = Engine();
                    return new JsonObject
                    {
                        ["temperatures"] = e.Temperatures(false),
                        ["fans"] = e.Fans(false),
                        ["controls"] = e.Controls(false),
                        ["virtualSensors"] = VirtualJson(),
                    };
                }),

            new McpTool("list_temperatures", "List temperature sensors",
                "All temperature sensors known to FanControl (hardware sensors and custom sensors such as mix/offset/average) with current value in °C.",
                includeHidden, ReadOnly: true,
                a => new JsonObject { ["temperatures"] = Engine().Temperatures(Arg.Bool(a, "include_hidden")) }),

            new McpTool("list_fans", "List fan speed sensors",
                "All fan/pump speed sensors (RPM) known to FanControl.",
                includeHidden, ReadOnly: true,
                a => new JsonObject { ["fans"] = Engine().Fans(Arg.Bool(a, "include_hidden")) }),

            new McpTool("list_controls", "List fan controls",
                "All controls (fan/pump outputs) with id, name, current output %, enabled state, manual vs. automatic mode, manual value, selected fan curve and tuning (step up/down, start/stop, minimum).",
                includeHidden, ReadOnly: true,
                a => new JsonObject { ["controls"] = Engine().Controls(Arg.Bool(a, "include_hidden")) }),

            new McpTool("list_fan_curves", "List fan curves",
                "All fan curves with type (graph, linear, flat, mix, sync, trigger, auto), their current output and full settings (temperature source, points, hysteresis ...).",
                Schema.Object(), ReadOnly: true,
                _ => new JsonObject { ["fanCurves"] = Engine().FanCurves() }),

            new McpTool("set_fan_speed", "Set fixed fan speed",
                "Switches a control to manual mode with a fixed speed in percent. Not persisted until save_config. Use set_fan_automatic to give control back to the fan curve.",
                Schema.Object(
                    ("control", Schema.String("Control id (preferred) or its displayed name, from list_controls."), true),
                    ("percent", Schema.Integer("Fan speed 0-100 %.", 0, 100), true)),
                ReadOnly: false,
                a =>
                {
                    RequireWrite();
                    var percent = Arg.Int(a, "percent");
                    if (percent < settings.MinimumManualPercent)
                        throw new ToolException($"{percent} % is below the configured safety floor of {settings.MinimumManualPercent} %.");
                    return Engine().SetManual(Arg.String(a, "control"), percent);
                }),

            new McpTool("set_fan_automatic", "Return fan to its curve",
                "Switches a control back to automatic mode so it follows a fan curve. Optionally selects a different fan curve by name. Not persisted until save_config.",
                Schema.Object(
                    ("control", Schema.String("Control id (preferred) or its displayed name."), true),
                    ("fan_curve", Schema.String("Optional: name of the fan curve to assign (see list_fan_curves)."), false)),
                ReadOnly: false,
                a =>
                {
                    RequireWrite();
                    return Engine().SetAutomatic(Arg.String(a, "control"), Arg.OptString(a, "fan_curve"));
                }),

            new McpTool("set_control_enabled", "Enable/disable a control",
                "Enables or disables a control. A disabled control is released back to the BIOS / hardware default. Not persisted until save_config.",
                Schema.Object(
                    ("control", Schema.String("Control id (preferred) or its displayed name."), true),
                    ("enabled", Schema.Boolean("true to let FanControl drive it, false to release it."), true)),
                ReadOnly: false,
                a =>
                {
                    RequireWrite();
                    return Engine().SetEnabled(Arg.String(a, "control"), Arg.Bool(a, "enabled"));
                }, Destructive: true),

            new McpTool("list_configs", "List configurations",
                "Lists the FanControl configuration files (profiles) and marks the active one.",
                Schema.Object(), ReadOnly: true,
                _ => new JsonObject { ["configs"] = Engine().Configs() }),

            new McpTool("load_config", "Switch configuration",
                "Loads another FanControl configuration (profile) by file name. Unsaved changes of the current configuration are discarded.",
                Schema.Object(("name", Schema.String("Configuration file name, e.g. 'Silent.json' (from list_configs)."), true)),
                ReadOnly: false,
                a =>
                {
                    RequireWrite();
                    return Engine().LoadConfig(Arg.String(a, "name"));
                }, Destructive: true),

            new McpTool("save_config", "Save configuration",
                "Writes the current in-memory state (including manual speeds set via MCP) to the active configuration file.",
                Schema.Object(), ReadOnly: false,
                _ =>
                {
                    RequireWrite();
                    return Engine().SaveConfig();
                }, Destructive: true),

            new McpTool("get_fan_curve_guide", "Fan curve best practices",
                "Built-in expert guide for tuning FanControl: safety limits per component (Ryzen/Intel/GPU/liquid), choosing temperature sources, curve shapes and templates, hysteresis/response time, step up/down, start/stop, airflow and a symptom->fix table. Read this before proposing or changing curves.",
                Schema.Object(), ReadOnly: true,
                _ => JsonValue.Create(TuningSkill.Guide)),

            new McpTool("analyze_fan_setup", "Analyze fan setup",
                "Checks the current FanControl setup against the best practices and returns findings (critical/warning/info) with concrete recommendations: unsafe 0 % on CPU/pump fans, curves never reaching 100 %, spiky unsmoothed CPU sources, radiator fans on CPU temperature, floors below stall speed, missing calibration, manual overrides left on, unused curves.",
                Schema.Object(), ReadOnly: true,
                _ =>
                {
                    var e = Engine();
                    return SetupAnalyzer.Analyze(e.Temperatures(true), e.Controls(true), e.FanCurves());
                }),

            new McpTool("update_fan_curve", "Edit fan curve",
                "Changes an existing graph, linear or flat fan curve: points, temperature source, hysteresis and response times (graph/linear), min/max temperature and speed (linear) or percent (flat). Only given fields change. Validated: points sorted, speeds 0-100 and non-decreasing. Takes effect immediately, persisted only by save_config. Confirm with the user first.",
                Schema.Object([("name", Schema.String("Name of the fan curve (from list_fan_curves)."), true), .. CurveProperties()]),
                ReadOnly: false,
                a =>
                {
                    RequireWrite();
                    return Engine().UpdateFanCurve(Arg.String(a, "name"), CurveChange(a));
                }),

            new McpTool("create_fan_curve", "Create fan curve",
                "Creates a new graph, linear or flat fan curve with the given settings. Assign it to controls with set_fan_automatic (fan_curve). Persisted only by save_config.",
                Schema.Object([
                    ("name", Schema.String("Unique name for the new curve."), true),
                    ("type", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("graph", "linear", "flat"), ["description"] = "Curve type; graph is the most flexible." }, true),
                    .. CurveProperties()]),
                ReadOnly: false,
                a =>
                {
                    RequireWrite();
                    return Engine().CreateFanCurve(Arg.String(a, "name"), Arg.String(a, "type"), CurveChange(a));
                }, Idempotent: false),

            new McpTool("delete_fan_curve", "Delete fan curve",
                "Deletes a fan curve that no control and no mix curve uses. Persisted only by save_config.",
                Schema.Object(("name", Schema.String("Name of the fan curve."), true)),
                ReadOnly: false,
                a =>
                {
                    RequireWrite();
                    return Engine().DeleteFanCurve(Arg.String(a, "name"));
                }, Destructive: true),

            new McpTool("delete_custom_sensor", "Delete custom sensor",
                "Deletes a custom sensor (time average, mix, offset, file) that no fan curve or other custom sensor uses. Persisted only by save_config.",
                Schema.Object(("sensor", Schema.String("Custom sensor id or name."), true)),
                ReadOnly: false,
                a =>
                {
                    RequireWrite();
                    return Engine().DeleteCustomSensor(Arg.String(a, "sensor"));
                }, Destructive: true),

            new McpTool("update_control_tuning", "Tune a fan control",
                "Changes how a control follows its curve: step up/down (%/s), start and stop values (%), minimum %, offset. Only given fields change. Start/stop should come from FanControl's calibration. Persisted only by save_config.",
                Schema.Object(
                    ("control", Schema.String("Control id (preferred) or its displayed name."), true),
                    ("step_up", Schema.Integer("Max speed increase per second in % (typ. 5-10).", 1, 100), false),
                    ("step_down", Schema.Integer("Max speed decrease per second in % (typ. 2-5).", 1, 100), false),
                    ("start", Schema.Integer("% needed to start the stopped fan.", 0, 100), false),
                    ("stop", Schema.Integer("% below which the fan stalls/stops.", 0, 100), false),
                    ("minimum_percent", Schema.Integer("Never go below this % while enabled.", 0, 100), false),
                    ("offset", Schema.Number("Offset in % added to the curve output."), false)),
                ReadOnly: false,
                a =>
                {
                    RequireWrite();
                    var tuning = new ControlTuning(Arg.OptIntOrNull(a, "step_up"), Arg.OptIntOrNull(a, "step_down"), Arg.OptIntOrNull(a, "start"),
                        Arg.OptIntOrNull(a, "stop"), Arg.OptIntOrNull(a, "minimum_percent"), Arg.OptFloat(a, "offset"));
                    if (tuning.MinimumPercent is { } min && min < settings.MinimumManualPercent)
                        throw new ToolException($"minimum_percent {min} is below the configured safety floor of {settings.MinimumManualPercent} %.");
                    return Engine().UpdateControlTuning(Arg.String(a, "control"), tuning);
                }),

            new McpTool("create_time_average_sensor", "Create smoothed sensor",
                "Creates a FanControl 'time average' custom sensor that smooths a spiky temperature (typ. CPU 5-10 s, GPU 3-5 s). Returns its id to use as temperature_source. Persisted only by save_config.",
                Schema.Object(
                    ("name", Schema.String("Name of the new sensor, e.g. 'CPU avg 7s'."), true),
                    ("source", Schema.String("Temperature sensor id or name to smooth."), true),
                    ("seconds", Schema.Integer("Averaging window in seconds.", 1, 600), true)),
                ReadOnly: false,
                a =>
                {
                    RequireWrite();
                    return Engine().CreateTimeAverageSensor(Arg.String(a, "name"), Arg.String(a, "source"), Arg.Int(a, "seconds"));
                }, Idempotent: false),

            new McpTool("set_virtual_sensor", "Set virtual temperature",
                "Sets the value of one of the plugin's virtual temperature sensors ('MCP Virtual N'). Fan curves that use it as source react on the next update. Works even when the engine is not reachable.",
                Schema.Object(
                    ("index", Schema.Integer($"Sensor number 1-{Math.Max(1, virtualSensors.Count)}.", 1, Math.Max(1, virtualSensors.Count)), true),
                    ("value", Schema.Number("Value in °C (any number). Omit or null to clear the sensor."), false)),
                ReadOnly: false,
                a =>
                {
                    RequireWrite();
                    if (virtualSensors.Count == 0) throw new ToolException("Virtual sensors are disabled (virtualSensorCount=0).");
                    var index = Arg.Int(a, "index");
                    var sensor = virtualSensors.FirstOrDefault(v => v.Index == index)
                                 ?? throw new ToolException($"Virtual sensor {index} does not exist (1-{virtualSensors.Count}).");
                    sensor.Set(Arg.OptFloat(a, "value"));
                    return new JsonObject { ["id"] = sensor.Id, ["name"] = sensor.Name, ["value"] = sensor.Value };
                }),
        ];
    }

    // ------------------------------------------------------------------ small helpers

    internal static class Schema
    {
        public static JsonObject Object(params (string Name, JsonObject Schema, bool Required)[] props)
        {
            var properties = new JsonObject();
            var required = new JsonArray();
            foreach (var (name, schema, req) in props)
            {
                properties[name] = schema;
                if (req) required.Add(name);
            }
            var o = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
            if (required.Count > 0) o["required"] = required;
            return o;
        }

        public static JsonObject String(string description) => new() { ["type"] = "string", ["description"] = description };
        public static JsonObject Boolean(string description) => new() { ["type"] = "boolean", ["description"] = description };
        public static JsonObject Number(string description) => new() { ["type"] = new JsonArray("number", "null"), ["description"] = description };
        public static JsonObject Integer(string description, int min, int max) => new()
        {
            ["type"] = "integer", ["description"] = description, ["minimum"] = min, ["maximum"] = max,
        };
    }

    internal static class Arg
    {
        public static string String(JsonObject a, string name)
            => OptString(a, name) ?? throw new ToolException($"Missing argument '{name}'.");

        public static string? OptString(JsonObject a, string name)
            => a[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

        public static bool Bool(JsonObject a, string name)
        {
            if (a[name] is not JsonValue v) return false;
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out b)) return b;
            throw new ToolException($"Argument '{name}' must be true or false.");
        }

        public static int Int(JsonObject a, string name)
        {
            var f = OptFloat(a, name) ?? throw new ToolException($"Missing argument '{name}'.");
            return (int)Math.Round(f);
        }

        public static int? OptIntOrNull(JsonObject a, string name)
            => OptFloat(a, name) is { } f ? (int)Math.Round(f) : null;

        public static bool? OptBool(JsonObject a, string name)
            => a[name] is null ? null : Bool(a, name);

        public static float? OptFloat(JsonObject a, string name)
        {
            if (a[name] is not JsonValue v) return null;
            if (v.TryGetValue<double>(out var d)) return (float)d;
            if (v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) return (float)d;
            throw new ToolException($"Argument '{name}' must be a number.");
        }
    }
}
