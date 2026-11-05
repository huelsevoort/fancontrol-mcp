using System.Diagnostics;
using System.Text.Json.Nodes;
using FanControl.Domain;
using FanControl.MCP.Engine;
using FanControl.MCP.Mcp;
using static FanControl.MCP.FanControlTools;

namespace FanControl.MCP.Wizard;

/// <summary>Shared state of the wizard tools (profile file, last plans, measurement recorder).</summary>
internal sealed class WizardContext(SystemProfile profile, MeasurementRecorder recorder)
{
    public SystemProfile Profile { get; } = profile;
    public MeasurementRecorder Recorder { get; } = recorder;
    public Dictionary<string, JsonObject> Plans { get; } = new(StringComparer.Ordinal);
}

/// <summary>MCP tools for the guided setup, hardware detection, restore points and load measurements.</summary>
internal static class WizardTools
{
    public const string UiUri = "ui://fancontrol/setup-wizard";
    public const string UiMimeType = "text/html;profile=mcp-app";

    private static readonly Lazy<string> UiHtml = new(() =>
    {
        using var s = typeof(WizardTools).Assembly.GetManifestResourceStream("FanControl.MCP.Ui.wizard.html")
                      ?? throw new InvalidOperationException("Embedded wizard UI missing.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    });

    /// <summary>MCP Apps view (cards UI) for hosts that render it: Claude Desktop/Web, ChatGPT, VS Code, Cursor, Goose ...</summary>
    public static McpResource UiResource { get; } = new(UiUri, "setup-wizard-view", "FanControl setup wizard",
        "Interactive setup wizard (MCP Apps view).", UiMimeType, () => UiHtml.Value,
        new JsonObject { ["ui"] = new JsonObject { ["prefersBorder"] = true, ["csp"] = new JsonObject { ["connectDomains"] = new JsonArray(), ["resourceDomains"] = new JsonArray() } } });

    private static JsonObject UiMeta() => new()
    {
        ["ui"] = new JsonObject { ["resourceUri"] = UiUri },
        ["ui/resourceUri"] = UiUri,          // deprecated flat key, still read by some hosts
        ["openai/outputTemplate"] = UiUri,   // ChatGPT alias
    };

    public static IEnumerable<McpTool> Create(IEngineProvider engines, PluginSettings settings, WizardContext ctx)
    {
        EngineBridge Engine() => engines.TryGetEngine(out var reason) ?? throw new ToolException("FanControl engine not reachable: " + reason);
        void RequireWrite()
        {
            if (!settings.AllowWrite) throw new ToolException($"Write access is disabled (allowWrite=false in {settings.LoadedFrom}).");
        }

        JsonObject Detect(EngineBridge e) => HardwareDetector.Detect(e.Temperatures(false), e.Fans(true), e.Controls(false),
            e.HardwareSensorsJson(BackendSensorType.UsagePercent, BackendSensorType.Frequency, BackendSensorType.Power), ProcessNames());

        yield return new McpTool("setup_wizard", "Guided setup wizard",
            "Start or continue the guided FanControl setup. Returns all wizard steps (goal, usage & room, case, fans & airflow, CPU cooling, GPU, BIOS & software) " +
            "with questions, options, prefilled values from hardware detection, what is already answered, and the next step. Use it to interview the user step by step, " +
            "saving answers with save_system_profile, then propose_baseline_config. Call it again any time to see progress.",
            Schema.Object(), ReadOnly: true,
            _ => SetupWizard.State(ctx.Profile.Snapshot(), Detect(Engine())), Meta: UiMeta());

        yield return new McpTool("setup_wizard_form", "Setup wizard as forms",
            "Runs the open setup wizard steps as native forms in clients that support MCP elicitation (e.g. Claude Code, VS Code, Cursor): " +
            "one form per step and one per fan output, answers are saved to the profile right away. Prefer this over asking in chat when available. " +
            "Fails with a clear message if the client cannot show forms.",
            Schema.Object(("step", new JsonObject
            {
                ["type"] = "string", ["enum"] = new JsonArray("goal", "usage", "case", "fans", "cpu_cooling", "gpu", "software"),
                ["description"] = "Only this step (also if already answered). Default: all open steps in order.",
            }, false)),
            ReadOnly: false,
            a => WizardForms.Run(ctx, () => Detect(Engine()), Arg.OptString(a, "step")), Idempotent: false);

        yield return new McpTool("detect_hardware", "Detect hardware",
            "What FanControl can detect automatically: CPU and GPU models with their thermal limits, primary temperature sensors, liquid sensors, every fan output with paired RPM sensor and whether a fan is connected, spinning fans without control, and other running fan/RGB software that may conflict.",
            Schema.Object(), ReadOnly: true,
            _ => Detect(Engine()));

        yield return new McpTool("get_system_profile", "Get system profile",
            "The saved answers of the setup wizard (goal, room, case, fans with position/direction/size/model, coolers, GPU, BIOS/software).",
            Schema.Object(), ReadOnly: true,
            _ => ctx.Profile.Snapshot());

        yield return new McpTool("save_system_profile", "Save system profile",
            "Merges answers into the saved system profile (JSON merge patch; null deletes a key). Keys follow the wizard question ids, e.g. " +
            "{\"goal\":{\"primary\":\"quiet_no_perf_loss\",\"annoyances\":[\"ramping\"]},\"environment\":{\"ambientC\":22}," +
            "\"fans\":[{\"controlId\":\"/lpc/nct6799d/control/2\",\"position\":\"front\",\"direction\":\"intake\",\"sizeMm\":140,\"count\":2,\"model\":\"Arctic P14\"}]}. " +
            "The fans array is merged by controlId. Set reset=true to start over.",
            Schema.Object(
                ("patch", new JsonObject { ["type"] = "object", ["description"] = "Answers to merge into the profile." }, false),
                ("reset", Schema.Boolean("Delete the whole profile first."), false)),
            ReadOnly: false,
            a =>
            {
                if (Arg.Bool(a, "reset")) ctx.Profile.Reset();
                if (a["patch"] is JsonObject patch) return ctx.Profile.Merge(patch);
                if (a["patch"] is not null) throw new ToolException("'patch' must be a JSON object.");
                return ctx.Profile.Snapshot();
            }, Idempotent: false);

        yield return new McpTool("propose_baseline_config", "Propose baseline configuration",
            "Computes a baseline FanControl configuration from the system profile, detected hardware and the goal (quiet_no_perf_loss, balanced, max_performance, silent, constant): " +
            "smoothed sensors, curves per fan group (CPU cooler, GPU, case, radiator, pump), assignments, tuning and pressure offsets, plus warnings about layout and software. " +
            "Nothing is changed. Present it to the user and apply with apply_baseline_config(plan_id) only after they agree.",
            Schema.Object(("goal", new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray(SetupWizard.Goals.Select(g => (JsonNode)g.Id).ToArray()),
                ["description"] = "Optional: override the goal from the profile.",
            }, false)),
            ReadOnly: true,
            a =>
            {
                var plan = BaselinePlanner.Plan(ctx.Profile.Snapshot(), Detect(Engine()), Arg.OptString(a, "goal"));
                var id = $"plan-{DateTime.Now:HHmmss}";
                plan["planId"] = id;
                lock (ctx.Plans) ctx.Plans[id] = plan;
                return plan;
            });

        yield return new McpTool("apply_baseline_config", "Apply baseline configuration",
            "Applies a plan from propose_baseline_config. Creates a restore point first, reuses matching existing sensors, creates/updates the 'MCP ...' curves and assigns controls. " +
            "In memory only until save_config. Requires the user's confirmation.",
            Schema.Object(("plan_id", Schema.String("planId returned by propose_baseline_config."), true)),
            ReadOnly: false,
            a =>
            {
                RequireWrite();
                JsonObject? plan;
                lock (ctx.Plans) ctx.Plans.TryGetValue(Arg.String(a, "plan_id"), out plan);
                if (plan is null) throw new ToolException("Unknown plan_id. Call propose_baseline_config again.");
                return PlanExecutor.Apply(Engine(), plan, min => min < settings.MinimumManualPercent && min > 0
                    ? $"minimum {min} % is below the configured safety floor of {settings.MinimumManualPercent} %." : null);
            }, Destructive: true, Idempotent: false);

        yield return new McpTool("create_mix_sensor", "Create mix sensor",
            "Creates a FanControl 'mix' custom sensor combining several temperatures (Max, Min, Average, Sum, Subtract), e.g. Max(CPU avg, GPU avg) as case-fan source " +
            "or Subtract(liquid, ambient) for delta-T. Returns its id for temperature_source. Persisted only by save_config.",
            Schema.Object(
                ("name", Schema.String("Name of the new sensor."), true),
                ("sources", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["minItems"] = 2, ["description"] = "Temperature sensor ids or names." }, true),
                ("function", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("Max", "Min", "Average", "Sum", "Subtract"), ["description"] = "Default Max." }, false)),
            ReadOnly: false,
            a =>
            {
                RequireWrite();
                var sources = (a["sources"] as JsonArray ?? throw new ToolException("'sources' must be an array.")).Select(s => s?.ToString() ?? "").ToList();
                return Engine().CreateMixSensor(Arg.String(a, "name"), sources, Arg.OptString(a, "function") ?? "Max");
            }, Idempotent: false);

        yield return new McpTool("create_restore_point", "Create restore point",
            "Snapshots all controls, fan curves and custom sensors in memory so later experiments can be undone with restore_restore_point.",
            Schema.Object(("label", Schema.String("Short description."), false)),
            ReadOnly: false,
            a => Engine().CreateRestorePoint(Arg.OptString(a, "label") ?? "manual"), Idempotent: false);

        yield return new McpTool("list_restore_points", "List restore points", "Restore points of this FanControl session (newest last).",
            Schema.Object(), ReadOnly: true, _ => new JsonObject { ["restorePoints"] = Engine().RestorePoints() });

        yield return new McpTool("restore_restore_point", "Restore",
            "Returns controls, fan curves and custom sensors to a restore point and deletes curves/sensors created after it. The saved config file is not changed.",
            Schema.Object(("id", Schema.String("Restore point id; default: the newest."), false)),
            ReadOnly: false,
            a =>
            {
                RequireWrite();
                return Engine().Restore(Arg.OptString(a, "id"));
            }, Destructive: true);

        yield return new McpTool("list_hardware_sensors", "List load/clock/power sensors",
            "Backend sensors FanControl reads but does not show: load (%), clocks (MHz) and power (W) of CPU and GPU. Useful to judge whether temperatures cost performance.",
            Schema.Object(("types", new JsonObject
            {
                ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("UsagePercent", "Frequency", "Power", "Voltage", "Data") },
                ["description"] = "Default: UsagePercent, Frequency, Power.",
            }, false)),
            ReadOnly: true,
            a =>
            {
                var types = (a["types"] as JsonArray)?.Select(t => Enum.TryParse<BackendSensorType>(t?.ToString(), true, out var v) ? v : (BackendSensorType?)null).OfType<BackendSensorType>().ToArray();
                if (types is null || types.Length == 0) types = [BackendSensorType.UsagePercent, BackendSensorType.Frequency, BackendSensorType.Power];
                var e = Engine();
                if (!e.HasHardwareSensors) throw new ToolException("This FanControl version does not expose backend sensors.");
                return new JsonObject { ["sensors"] = e.HardwareSensorsJson(types) };
            });

        yield return new McpTool("start_measurement", "Start measurement",
            "Starts recording temperatures, fan RPM, control %, CPU/GPU load, clocks and power every few seconds while the user runs a real workload (game, render). " +
            "Label phases with mark_phase when you change settings (A/B test), read results with get_measurement_summary. " +
            "With guard=true (default) a restore point is created and automatically restored if CPU or GPU reach their alarm temperature.",
            Schema.Object(
                ("phase", Schema.String("Label of the first phase, e.g. 'A baseline'."), false),
                ("interval_seconds", Schema.Integer("Sampling interval (default 5).", 2, 60), false),
                ("max_minutes", Schema.Integer("Stops automatically after this (default 60).", 1, 180), false),
                ("guard", Schema.Boolean("Auto-restore when a limit is reached (default true)."), false),
                ("cpu_max", Schema.Number("Guard CPU limit in °C (default: detected alarm temperature)."), false),
                ("gpu_max", Schema.Number("Guard GPU limit in °C (default: detected alarm temperature)."), false)),
            ReadOnly: false,
            a => ctx.Recorder.Start(Arg.OptString(a, "phase") ?? "A baseline", Arg.OptIntOrNull(a, "interval_seconds") ?? 5, Arg.OptIntOrNull(a, "max_minutes") ?? 60,
                Arg.OptFloat(a, "cpu_max"), Arg.OptFloat(a, "gpu_max"), Arg.OptBool(a, "guard") ?? true), Idempotent: false);

        yield return new McpTool("mark_phase", "Mark measurement phase",
            "Starts a new labelled phase in the running measurement (call right after changing fan settings).",
            Schema.Object(("phase", Schema.String("Label, e.g. 'B GPU curve +3 °C'."), true)),
            ReadOnly: false, a => ctx.Recorder.MarkPhase(Arg.String(a, "phase")), Idempotent: false);

        yield return new McpTool("stop_measurement", "Stop measurement", "Stops recording (results stay available).",
            Schema.Object(), ReadOnly: false, _ => ctx.Recorder.Stop());

        yield return new McpTool("get_measurement_summary", "Measurement results",
            "Per-phase averages/min/max of temperatures, fan RPM, control %, CPU/GPU clocks, load and power, GPU limit reasons (power vs thermal) and guard events. Works while running.",
            Schema.Object(("skip_seconds", Schema.Integer("Ignore the first seconds of each phase while temperatures settle (default 45).", 0, 600), false)),
            ReadOnly: true, a => ctx.Recorder.Summary(Arg.OptIntOrNull(a, "skip_seconds") ?? 45));
    }

    private static IEnumerable<string> ProcessNames()
    {
        try { return Process.GetProcesses().Select(p => { try { return p.ProcessName; } catch { return ""; } }).Where(n => n.Length > 0).ToList(); }
        catch { return []; }
    }
}
