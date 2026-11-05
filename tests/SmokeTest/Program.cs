using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FanControl.MCP;
using FanControl.Plugins;

var remote = args.FirstOrDefault(a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase));
var write = args.Contains("--write");
var token = args.SkipWhile(a => a != "--token").Skip(1).FirstOrDefault();
var failures = 0;

McpPlugin? plugin = null;
string url;
if (remote is null)
{
    // Host the plugin like FanControl would, on a test port.
    var dir = AppContext.BaseDirectory;
    var settingsPath = Path.Combine(dir, "FanControl.MCP.json");
    const string testSettings = """{ "port": 48699, "virtualSensorCount": 2 }""";
    // OneDrive sometimes locks files in synced folders; only write when needed.
    if (!File.Exists(settingsPath) || File.ReadAllText(settingsPath) != testSettings) File.WriteAllText(settingsPath, testSettings);
    plugin = new McpPlugin();
    plugin.Initialize();
    var container = new Container();
    plugin.Load(container);
    Check("plugin registers virtual sensors", container.TempSensors.Count == 2, $"{container.TempSensors.Count} sensors");
    url = "http://127.0.0.1:48699/mcp";
    write = true;
}
else
{
    url = remote;
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
if (token is not null) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
var nextId = 1;

async Task<(int Status, JsonNode? Body)> Post(string json, string? origin = null)
{
    using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    req.Headers.Accept.ParseAdd("application/json, text/event-stream");
    req.Headers.Add("MCP-Protocol-Version", "2025-06-18");
    if (origin is not null) req.Headers.Add("Origin", origin);
    using var res = await http.SendAsync(req);
    var text = await res.Content.ReadAsStringAsync();
    JsonNode? node = null;
    try { if (text.Length > 0) node = JsonNode.Parse(text); } catch { }
    return ((int)res.StatusCode, node);
}

async Task<JsonNode?> Rpc(string method, JsonObject? @params = null)
{
    var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = nextId++, ["method"] = method };
    if (@params is not null) msg["params"] = @params;
    var (_, body) = await Post(msg.ToJsonString());
    return body;
}

async Task<JsonNode?> Call(string tool, JsonObject? arguments = null)
{
    var res = await Rpc("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments ?? new JsonObject() });
    var result = res?["result"];
    var isError = result?["isError"]?.GetValue<bool>() == true;
    var text = result?["content"]?[0]?["text"]?.GetValue<string>() ?? res?.ToJsonString() ?? "<no response>";
    Console.WriteLine($"--- {tool} {(isError ? "(tool error)" : "")}");
    Console.WriteLine(text.Length > 3000 ? text[..3000] + "\n..." : text);
    return result;
}

void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {detail}");
    if (!ok) failures++;
}

try
{
    var init = await Rpc("initialize", new JsonObject
    {
        ["protocolVersion"] = "2025-06-18",
        ["capabilities"] = new JsonObject(),
        ["clientInfo"] = new JsonObject { ["name"] = "smoke-test", ["version"] = "1.0" },
    });
    Check("initialize", init?["result"]?["serverInfo"]?["name"]?.GetValue<string>() == "fancontrol-mcp", init?["result"]?["protocolVersion"]?.ToString() ?? init?.ToJsonString() ?? "");

    var (notifStatus, _) = await Post("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
    Check("initialized notification -> 202", notifStatus == 202, notifStatus.ToString());

    var ping = await Rpc("ping");
    Check("ping", ping?["result"] is JsonObject);

    var tools = await Rpc("tools/list");
    var toolNames = tools?["result"]?["tools"]?.AsArray().Select(t => t?["name"]?.GetValue<string>()).ToList() ?? [];
    Check("tools/list", toolNames.Count >= 10, string.Join(", ", toolNames));

    var unknown = await Rpc("does/not/exist");
    Check("unknown method -> -32601", unknown?["error"]?["code"]?.GetValue<int>() == -32601);

    var (badStatus, bad) = await Post("{not json");
    Check("parse error -> -32700", bad?["error"]?["code"]?.GetValue<int>() == -32700, badStatus.ToString());

    var (forbidden, _) = await Post("""{"jsonrpc":"2.0","id":99,"method":"ping"}""", origin: "https://evil.example");
    Check("foreign Origin -> 403", forbidden == 403, forbidden.ToString());

    var (batchStatus, batch) = await Post("""[{"jsonrpc":"2.0","id":"a","method":"ping"},{"jsonrpc":"2.0","method":"notifications/x"},{"jsonrpc":"2.0","id":"b","method":"ping"}]""");
    Check("batch", batch is JsonArray { Count: 2 }, batchStatus.ToString());

    var status = await Call("get_status");
    Check("get_status", status?["isError"]?.GetValue<bool>() == false);

    var snapshot = await Call("get_snapshot");
    if (remote is null)
        Check("get_snapshot without engine -> tool error", snapshot?["isError"]?.GetValue<bool>() == true);
    else
        Check("get_snapshot", snapshot?["isError"]?.GetValue<bool>() == false);

    if (remote is not null)
    {
        await Call("list_fan_curves");
        await Call("list_configs");
    }

    // ---------------------------------------------------------------- built-in skill
    var resources = await Rpc("resources/list");
    Check("resources/list has guide", resources?["result"]?["resources"]?[0]?["uri"]?.GetValue<string>() == "fancontrol://guides/fan-curve-best-practices");
    var guideRes = await Rpc("resources/read", new JsonObject { ["uri"] = "fancontrol://guides/fan-curve-best-practices" });
    Check("resources/read returns markdown", guideRes?["result"]?["contents"]?[0]?["text"]?.GetValue<string>().StartsWith("# FanControl fan curve best practices") == true);
    var missingRes = await Rpc("resources/read", new JsonObject { ["uri"] = "fancontrol://nope" });
    Check("resources/read unknown -> error", missingRes?["error"] is not null);
    var prompts = await Rpc("prompts/list");
    var promptNames = prompts?["result"]?["prompts"]?.AsArray().Select(p => p?["name"]?.GetValue<string>()).ToList() ?? [];
    Check("prompts/list", promptNames.Contains("tune_fan_curves") && promptNames.Contains("diagnose_fan_noise") && promptNames.Contains("review_fan_setup"), string.Join(", ", promptNames));
    var prompt = await Rpc("prompts/get", new JsonObject { ["name"] = "tune_fan_curves", ["arguments"] = new JsonObject { ["goal"] = "silent", ["focus"] = "CPU" } });
    var msgs = prompt?["result"]?["messages"]?.AsArray();
    Check("prompts/get embeds guide + task", msgs?.Count == 2 && msgs[0]?["content"]?["type"]?.GetValue<string>() == "resource"
                                            && msgs[1]?["content"]?["text"]?.GetValue<string>().Contains("Goal: silent. Focus: CPU.") == true);
    var promptMissingArg = await Rpc("prompts/get", new JsonObject { ["name"] = "diagnose_fan_noise" });
    Check("prompts/get missing required arg -> error", promptMissingArg?["error"] is not null);
    var guideTool = await Rpc("tools/call", new JsonObject { ["name"] = "get_fan_curve_guide", ["arguments"] = new JsonObject() });
    Check("get_fan_curve_guide returns plain markdown", guideTool?["result"]?["content"]?[0]?["text"]?.GetValue<string>().StartsWith("# FanControl") == true);

    if (remote is null) AnalyzerTests();

    if (remote is not null)
    {
        var analysis = await Call("analyze_fan_setup");
        Check("analyze_fan_setup", analysis?["isError"]?.GetValue<bool>() == false && analysis?["structuredContent"]?["summary"] is JsonObject);
    }

    if (remote is not null && write && args.SkipWhile(a => a != "--curve").Skip(1).FirstOrDefault() is { } curveName)
    {
        // Round trip on an existing curve: change response time + rewrite identical points, then restore.
        var curves = await Rpc("tools/call", new JsonObject { ["name"] = "list_fan_curves", ["arguments"] = new JsonObject() });
        var original = curves?["result"]?["structuredContent"]?["fanCurves"]?.AsArray().FirstOrDefault(c => c?["name"]?.GetValue<string>() == curveName)?["config"];
        var origRt = original?["hysteresisConfig"]?["responseTimeUp"]?.GetValue<int>() ?? 5;
        var origPoints = new JsonArray(original?["points"]?.AsArray().Select(p => (JsonNode)new JsonObject { ["temperature"] = p!["x"]!.GetValue<double>(), ["percent"] = p["y"]!.GetValue<double>() }).ToArray() ?? []);
        var changed = await Call("update_fan_curve", new JsonObject { ["name"] = curveName, ["response_time_up"] = origRt + 1, ["points"] = origPoints.DeepClone() });
        Check("update_fan_curve applies", changed?["structuredContent"]?["config"]?["hysteresisConfig"]?["responseTimeUp"]?.GetValue<int>() == origRt + 1);
        var restored = await Call("update_fan_curve", new JsonObject { ["name"] = curveName, ["response_time_up"] = origRt });
        Check("update_fan_curve restores", restored?["structuredContent"]?["config"]?["hysteresisConfig"]?["responseTimeUp"]?.GetValue<int>() == origRt);
        var badCurve = await Call("update_fan_curve", new JsonObject { ["name"] = curveName, ["points"] = new JsonArray(new JsonArray(40, 60), new JsonArray(60, 30)) });
        Check("update_fan_curve rejects decreasing curve", badCurve?["isError"]?.GetValue<bool>() == true);

        // Create -> use -> delete a temporary curve and smoothed sensor.
        var created = await Call("create_fan_curve", new JsonObject
        {
            ["name"] = "MCP Smoke Test", ["type"] = "graph", ["temperature_source"] = "/amdcpu/0/temperature/2",
            ["points"] = new JsonArray(new JsonArray(40, 30), new JsonArray(70, 60), new JsonArray(85, 100)), ["hysteresis_up"] = 2, ["response_time_up"] = 3,
        });
        Check("create_fan_curve", created?["structuredContent"]?["config"]?["points"]?.AsArray().Count == 3);
        var deleted = await Call("delete_fan_curve", new JsonObject { ["name"] = "MCP Smoke Test" });
        Check("delete_fan_curve", deleted?["structuredContent"]?["deleted"]?.GetValue<string>() == "MCP Smoke Test");

        var avg = await Call("create_time_average_sensor", new JsonObject { ["name"] = "MCP Smoke Avg", ["source"] = "/amdcpu/0/temperature/2", ["seconds"] = 7 });
        var avgId = avg?["structuredContent"]?["id"]?.GetValue<string>();
        Check("create_time_average_sensor", avgId is not null, avgId ?? "");
        await Task.Delay(2500);
        var temps = await Rpc("tools/call", new JsonObject { ["name"] = "list_temperatures", ["arguments"] = new JsonObject() });
        var avgNow = temps?["result"]?["structuredContent"]?["temperatures"]?.AsArray().FirstOrDefault(t => t?["id"]?.GetValue<string>() == avgId);
        Check("time average sensor has value + config", avgNow?["value"] is not null && avgNow?["config"]?["selectedTime"]?.GetValue<long>() == 7, avgNow?.ToJsonString() ?? "");
        if (avgId is not null)
        {
            var delAvg = await Call("delete_custom_sensor", new JsonObject { ["sensor"] = avgId });
            Check("delete_custom_sensor", delAvg?["isError"]?.GetValue<bool>() == false);
        }
    }

    if (remote is not null && write && args.SkipWhile(a => a != "--control").Skip(1).FirstOrDefault() is { } tuneControl)
    {
        var controls = await Rpc("tools/call", new JsonObject { ["name"] = "list_controls", ["arguments"] = new JsonObject() });
        var ctl = controls?["result"]?["structuredContent"]?["controls"]?.AsArray().FirstOrDefault(c => c?["id"]?.GetValue<string>() == tuneControl);
        var origDown = (int)(ctl?["stepDown"]?.GetValue<double>() ?? 8);
        var tuned = await Call("update_control_tuning", new JsonObject { ["control"] = tuneControl, ["step_down"] = Math.Max(1, origDown - 3) });
        Check("update_control_tuning applies", (int)(tuned?["structuredContent"]?["stepDown"]?.GetValue<double>() ?? -1) == Math.Max(1, origDown - 3));
        var back = await Call("update_control_tuning", new JsonObject { ["control"] = tuneControl, ["step_down"] = origDown });
        Check("update_control_tuning restores", (int)(back?["structuredContent"]?["stepDown"]?.GetValue<double>() ?? -1) == origDown);
    }

    if (write)
    {
        var v = await Call("set_virtual_sensor", new JsonObject { ["index"] = 1, ["value"] = 42.5 });
        Check("set_virtual_sensor", v?["structuredContent"]?["value"]?.GetValue<double>() == 42.5);
        var outOfRange = await Call("set_virtual_sensor", new JsonObject { ["index"] = 99, ["value"] = 1 });
        Check("set_virtual_sensor invalid index -> tool error", outOfRange?["isError"]?.GetValue<bool>() == true);
    }

    if (remote is not null && write && args.SkipWhile(a => a != "--control").Skip(1).FirstOrDefault() is { } control)
    {
        // Round-trip on one control: fixed speed, then back to automatic.
        var manual = await Call("set_fan_speed", new JsonObject { ["control"] = control, ["percent"] = 60 });
        Check("set_fan_speed", manual?["structuredContent"]?["manualControl"]?.GetValue<bool>() == true);
        await Task.Delay(3000);
        var after = await Call("list_controls");
        var auto = await Call("set_fan_automatic", new JsonObject { ["control"] = control });
        Check("set_fan_automatic", auto?["structuredContent"]?["manualControl"]?.GetValue<bool>() == false);
    }
}
catch (Exception e)
{
    Console.WriteLine("FAIL  exception: " + e);
    failures++;
}
finally
{
    plugin?.Close();
}

Console.WriteLine(failures == 0 ? "\nALL PASSED" : $"\n{failures} FAILED");
return failures == 0 ? 0 : 1;

// Rule checks of the setup analyzer on a synthetic, deliberately bad setup.
void AnalyzerTests()
{
    var temps = JsonNode.Parse("""
        [
          {"id":"/amdcpu/0/temperature/2","name":"Core (Tctl/Tdie)","origin":"AMD Ryzen 5 9600X","type":"Temperature","value":94},
          {"id":"/aio/liquid","name":"Liquid","origin":"AIO","type":"Temperature","value":32},
          {"id":"Time/CPU Avg.","name":"CPU Avg.","customSensorType":"TimeAverage","value":70,"config":{"selectedTempSource":"/amdcpu/0/temperature/2","selectedTime":5}}
        ]
        """)!.AsArray();
    var curves = JsonNode.Parse("""
        [
          {"name":"CPU raw","type":"GraphFanCurve","currentOutput":50,"config":{"points":[{"x":40,"y":0},{"x":95,"y":90}],"selectedTempSource":"/amdcpu/0/temperature/2","hysteresisConfig":{"hysteresisValueUp":0,"hysteresisValueDown":0,"responseTimeUp":0,"responseTimeDown":0,"ignoreHysteresisAtLimits":true}}},
          {"name":"Smooth","type":"GraphFanCurve","currentOutput":40,"config":{"points":[{"x":40,"y":30},{"x":60,"y":20},{"x":85,"y":100}],"selectedTempSource":"Time/CPU Avg.","hysteresisConfig":{"hysteresisValueUp":2,"hysteresisValueDown":2,"responseTimeUp":3,"responseTimeDown":5,"ignoreHysteresisAtLimits":true}}},
          {"name":"Unused","type":"FlatFanCurve","config":{"percent":50}},
          {"name":"Gone source","type":"GraphFanCurve","config":{"points":[{"x":30,"y":30},{"x":80,"y":100}],"selectedTempSource":"/does/not/exist","hysteresisConfig":{"hysteresisValueUp":2,"hysteresisValueDown":2,"responseTimeUp":3,"responseTimeDown":3,"ignoreHysteresisAtLimits":true}}}
        ]
        """)!.AsArray();
    var controls = JsonNode.Parse("""
        [
          {"id":"c/cpu","name":"CPU","enabled":true,"manualControl":false,"fanCurve":"CPU raw","pairedFanSensorId":"f/1","stepUp":8,"stepDown":8,"start":16,"stop":7,"minimumPercent":0},
          {"id":"c/pump","name":"AIO Pump","enabled":true,"manualControl":true,"manualPercent":20,"fanCurve":"Smooth","pairedFanSensorId":"f/2","stepUp":8,"stepDown":8,"start":0,"stop":0},
          {"id":"c/rad","name":"Radiator Top","enabled":true,"manualControl":false,"fanCurve":"Smooth","pairedFanSensorId":"f/3","stepUp":5,"stepDown":20,"start":20,"stop":25},
          {"id":"c/case","name":"Intake Front","enabled":true,"manualControl":false,"fanCurve":"Gone source","stepUp":8,"stepDown":4,"start":10,"stop":5}
        ]
        """)!.AsArray();

    var result = FanControl.MCP.SetupAnalyzer.Analyze(temps, controls, curves);
    var codes = result["findings"]!.AsArray().Select(f => $"{f!["code"]}@{f["target"]}").ToList();
    bool HasCode(string code, string target) => codes.Contains($"{code}@{target}");

    Check("analyzer: CPU temp high", HasCode("TEMP_HIGH", "Core (Tctl/Tdie)"));
    Check("analyzer: zero rpm on CPU fan", HasCode("ZERO_RPM_CRITICAL", "control 'CPU'"));
    Check("analyzer: CPU curve never 100 %", HasCode("CURVE_NO_FULL_SPEED", "curve 'CPU raw'"));
    Check("analyzer: raw CPU source spiky", HasCode("CURVE_SPIKY_SOURCE", "curve 'CPU raw'"));
    Check("analyzer: curve too low now", HasCode("CURVE_TOO_LOW_NOW", "curve 'CPU raw'"));
    Check("analyzer: smoothed source not flagged", !HasCode("CURVE_SPIKY_SOURCE", "curve 'Smooth'"));
    Check("analyzer: non-monotonic", HasCode("CURVE_NON_MONOTONIC", "curve 'Smooth'"));
    Check("analyzer: manual pump low", HasCode("MANUAL_LOW", "control 'AIO Pump'"));
    Check("analyzer: radiator on CPU", HasCode("RADIATOR_ON_CPU", "control 'Radiator Top'"));
    Check("analyzer: floor below stop", HasCode("BELOW_STOP", "control 'Radiator Top'"));
    Check("analyzer: step down faster", HasCode("STEP_DOWN_FASTER", "control 'Radiator Top'"));
    Check("analyzer: missing source", HasCode("SOURCE_MISSING", "curve 'Gone source'"));
    Check("analyzer: not paired", HasCode("NOT_PAIRED", "control 'Intake Front'"));
    Check("analyzer: unused curve", HasCode("CURVE_UNUSED", "curve 'Unused'"));
    Check("analyzer: critical first", result["findings"]![0]!["severity"]!.GetValue<string>() == "critical");
    if (failures > 0) Console.WriteLine(string.Join("\n", codes));
}

sealed class Container : IPluginSensorsContainer
{
    public List<IPluginControlSensor> ControlSensors { get; } = [];
    public List<IPluginSensor> FanSensors { get; } = [];
    public List<IPluginSensor> TempSensors { get; } = [];
}
