using System.Text.Json.Nodes;
using FanControl.MCP.Mcp;

namespace FanControl.MCP.Wizard;

/// <summary>
/// Runs wizard steps as native client forms (MCP elicitation, form mode) generated from the
/// <see cref="SetupWizard"/> question catalogue. Answers are merged into the profile after every form.
/// </summary>
internal static class WizardForms
{
    private static readonly TimeSpan FormTimeout = TimeSpan.FromMinutes(15);

    public static JsonObject Run(WizardContext ctx, Func<JsonObject> detect, string? onlyStep)
    {
        var call = McpCallContext.Current;
        if (call?.Session?.SupportsForms != true)
            throw new ToolException("This MCP client cannot show forms (no elicitation support). Use setup_wizard and ask the questions in chat instead.");

        var detection = detect();
        var done = new JsonArray();
        var skipped = new JsonArray();
        var asked = new HashSet<string>();
        var cancelled = false;

        while (true)
        {
            var state = SetupWizard.State(ctx.Profile.Snapshot(), detection);
            var steps = state["steps"]!.AsArray().OfType<JsonObject>().ToList();
            var step = onlyStep is not null
                ? steps.FirstOrDefault(s => s["id"]?.ToString() == onlyStep && !asked.Contains(onlyStep))
                : steps.FirstOrDefault(s => s["complete"]?.GetValue<bool>() != true && !asked.Contains(s["id"]!.ToString()));
            if (step is null) break;
            var id = step["id"]!.ToString();
            asked.Add(id);

            var outcome = id == "fans" ? AskFans(call, ctx, step) : AskStep(call, ctx, step, steps.IndexOf(step) + 1, steps.Count);
            if (outcome == "cancel") { cancelled = true; break; }
            (outcome == "accept" ? done : skipped).Add(id);
        }

        var final = SetupWizard.State(ctx.Profile.Snapshot(), detection);
        return new JsonObject
        {
            ["completedSteps"] = done,
            ["skippedSteps"] = skipped,
            ["cancelled"] = cancelled,
            ["wizardComplete"] = final["complete"]?.DeepClone(),
            ["nextStep"] = final["nextStep"]?.DeepClone(),
            ["profile"] = ctx.Profile.Snapshot(),
            ["next"] = final["complete"]?.GetValue<bool>() == true
                ? "All required answers are in. Call propose_baseline_config and explain the plan."
                : "Some steps are still open; ask the remaining questions in chat or call setup_wizard_form again.",
        };
    }

    private static string AskStep(McpCallContext call, WizardContext ctx, JsonObject step, int index, int total)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        var keyToPath = new Dictionary<string, string>();
        foreach (var q in (step["questions"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var path = q["id"]!.ToString();
            var key = path.Replace('.', '_');
            var schema = Field(q);
            if (schema is null) continue;
            properties[key] = schema;
            keyToPath[key] = path;
            if (q["required"]?.GetValue<bool>() == true) required.Add(key);
        }
        if (properties.Count == 0) return "accept";

        var requested = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) requested["required"] = required;
        var result = call.Elicit($"FanControl setup {index}/{total}: {step["title"]}. {step["why"]}", requested, FormTimeout)
                     ?? throw new ToolException("The client does not support forms.");
        var action = result["action"]?.ToString() ?? "cancel";
        if (action != "accept" || result["content"] is not JsonObject content) return action;

        var patch = new JsonObject();
        foreach (var (key, value) in content)
        {
            if (value is null || !keyToPath.TryGetValue(key, out var path)) continue;
            if (value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 0) continue;
            SetPath(patch, path, value.DeepClone());
        }
        if (patch.Count > 0) ctx.Profile.Merge(patch);
        return "accept";
    }

    private static string AskFans(McpCallContext call, WizardContext ctx, JsonObject step)
    {
        var question = step["questions"]![0]!.AsObject();
        var columns = question["columns"]!.AsObject();
        var rows = (question["rows"] as JsonArray ?? []).OfType<JsonObject>().Where(r => r["connected"]?.GetValue<bool?>() != false).ToList();
        var any = false;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var prefill = row["prefill"] as JsonObject ?? new JsonObject();
            var properties = new JsonObject
            {
                ["position"] = Choice("Position", columns["position"] as JsonArray, prefill["position"]),
                ["direction"] = Choice("Airflow direction", columns["direction"] as JsonArray, prefill["direction"]),
                ["sizeMm"] = new JsonObject { ["type"] = "integer", ["title"] = "Fan size (mm)", ["minimum"] = 40, ["maximum"] = 420, ["default"] = prefill["sizeMm"]?.DeepClone() ?? 120 },
                ["count"] = new JsonObject { ["type"] = "integer", ["title"] = "Fans on this output", ["minimum"] = 1, ["maximum"] = 12, ["default"] = prefill["count"]?.DeepClone() ?? 1 },
                ["model"] = new JsonObject { ["type"] = "string", ["title"] = "Fan model (optional)", ["default"] = prefill["model"]?.DeepClone() ?? "" },
            };
            var rpm = row["rpm"] is JsonValue rv && rv.TryGetValue<double>(out var r) ? $" (currently {r:0} RPM)" : "";
            var result = call.Elicit($"Fan output {i + 1}/{rows.Count}: '{row["name"]}'{rpm}. Where is it and which way does it blow?",
                new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray("position", "direction") }, FormTimeout)
                ?? throw new ToolException("The client does not support forms.");
            var action = result["action"]?.ToString() ?? "cancel";
            if (action == "cancel") return "cancel";
            if (action != "accept" || result["content"] is not JsonObject content) continue;

            var fan = new JsonObject { ["controlId"] = row["controlId"]!.ToString() };
            foreach (var (k, v) in content)
            {
                if (v is null || v is JsonValue sv && sv.TryGetValue<string>(out var s) && s.Length == 0) continue;
                fan[k] = v.DeepClone();
            }
            ctx.Profile.Merge(new JsonObject { ["fans"] = new JsonArray(fan) });
            any = true;
        }
        return any ? "accept" : "decline";
    }

    /// <summary>Maps a wizard question to the flat elicitation schema subset (string/number/boolean/enum/array of enum).</summary>
    private static JsonObject? Field(JsonObject q)
    {
        var title = q["prompt"]?.ToString() ?? q["id"]!.ToString();
        var current = q["answer"] ?? q["prefill"];
        switch (q["type"]?.ToString())
        {
            case "choice":
                return Choice(title, q["options"] as JsonArray, current);
            case "multi":
            {
                var o = new JsonObject
                {
                    ["type"] = "array", ["title"] = title,
                    ["items"] = new JsonObject { ["anyOf"] = Options(q["options"] as JsonArray) },
                };
                if (current is JsonArray) o["default"] = current.DeepClone();
                return o;
            }
            case "number":
            {
                var o = new JsonObject { ["type"] = "number", ["title"] = title };
                if (q["min"] is { } min) o["minimum"] = min.DeepClone();
                if (q["max"] is { } max) o["maximum"] = max.DeepClone();
                if (current is JsonValue) o["default"] = current.DeepClone();
                return o;
            }
            case "text":
            {
                var o = new JsonObject { ["type"] = "string", ["title"] = title };
                if (current is JsonValue) o["default"] = current.ToString();
                return o;
            }
            default:
                return null;
        }
    }

    private static JsonObject Choice(string title, JsonArray? options, JsonNode? current)
    {
        var o = new JsonObject { ["type"] = "string", ["title"] = title, ["oneOf"] = Options(options) };
        if (current is JsonValue v && v.TryGetValue<string>(out var s) && options?.Any(x => x?["value"]?.ToString() == s) == true) o["default"] = s;
        return o;
    }

    private static JsonArray Options(JsonArray? options) => new((options ?? []).OfType<JsonObject>()
        .Select(x => (JsonNode)new JsonObject
        {
            ["const"] = x["value"]?.ToString(),
            ["title"] = x["hint"] is null ? x["label"]?.ToString() : $"{x["label"]} – {x["hint"]}",
        }).ToArray());

    private static void SetPath(JsonObject root, string path, JsonNode value)
    {
        var parts = path.Split('.');
        var node = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (node[parts[i]] is not JsonObject child) { child = new JsonObject(); node[parts[i]] = child; }
            node = child;
        }
        node[parts[^1]] = value;
    }
}
