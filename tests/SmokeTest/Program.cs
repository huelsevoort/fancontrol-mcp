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

    if (remote is null) { AnalyzerTests(); WizardTests(); }

    if (remote is not null && args.Contains("--elicit")) await ElicitationTest();

    if (remote is not null)
    {
        var detect = await Call("detect_hardware");
        Check("detect_hardware", detect?["structuredContent"]?["cpu"]?["name"] is not null, detect?["structuredContent"]?["cpu"]?["name"]?.ToString() ?? "");
        var wizard = await Call("setup_wizard");
        Check("setup_wizard has 7 steps", wizard?["structuredContent"]?["steps"]?.AsArray().Count == 7);
        var plan = await Call("propose_baseline_config", new JsonObject { ["goal"] = "quiet_no_perf_loss" });
        Check("propose_baseline_config", plan?["structuredContent"]?["actions"]?.AsArray().Count > 0 && plan?["structuredContent"]?["planId"] is not null);
        var hw = await Call("list_hardware_sensors");
        Check("list_hardware_sensors", hw?["isError"]?.GetValue<bool>() == false);
    }

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

// Full elicitation round trip like Claude Code does it: session with form capability, tools/call answered as SSE,
// server asks via elicitation/create, client answers with a separate POST, final result arrives on the stream.
async Task ElicitationTest()
{
    using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    var backup = await Call("get_system_profile");

    var init = new HttpRequestMessage(HttpMethod.Post, url)
    {
        Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{"elicitation":{"form":{}}},"clientInfo":{"name":"elicit-test","version":"1"}}}""", Encoding.UTF8, "application/json"),
    };
    var initRes = await client.SendAsync(init);
    var session = initRes.Headers.TryGetValues("MCP-Session-Id", out var ids) ? ids.First() : null;
    Check("session id issued on initialize", session is not null, session ?? "");
    if (session is null) return;

    HttpRequestMessage Msg(string json) => new(HttpMethod.Post, url)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
        Headers = { { "MCP-Session-Id", session }, { "Accept", "application/json, text/event-stream" } },
    };

    var unknown = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, url)
    {
        Content = new StringContent("""{"jsonrpc":"2.0","id":2,"method":"ping"}""", Encoding.UTF8, "application/json"),
        Headers = { { "MCP-Session-Id", "does-not-exist" } },
    });
    Check("unknown session -> 404", (int)unknown.StatusCode == 404, ((int)unknown.StatusCode).ToString());

    var call = await client.SendAsync(Msg("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"setup_wizard_form","arguments":{"step":"goal"}}}"""), HttpCompletionOption.ResponseHeadersRead);
    Check("tools/call answered as SSE", call.Content.Headers.ContentType?.MediaType == "text/event-stream", call.Content.Headers.ContentType?.ToString() ?? "");
    using var sse = new StreamReader(await call.Content.ReadAsStreamAsync());

    async Task<JsonNode?> NextEvent()
    {
        string? line;
        while ((line = await sse.ReadLineAsync()) is not null)
            if (line.StartsWith("data: ", StringComparison.Ordinal)) return JsonNode.Parse(line[6..]);
        return null;
    }

    var ask = await NextEvent();
    var schema = ask?["params"]?["requestedSchema"]?["properties"]?.AsObject();
    Check("elicitation/create with goal form", ask?["method"]?.ToString() == "elicitation/create" && schema?.ContainsKey("goal_primary") == true,
        string.Join(", ", schema?.Select(p => p.Key) ?? []));
    var oneOf = schema?["goal_primary"]?["oneOf"]?.AsArray().Select(o => o?["const"]?.ToString()).ToList() ?? [];
    Check("goal options as oneOf", oneOf.Contains("quiet_no_perf_loss") && oneOf.Contains("max_performance"));

    var answer = new JsonObject
    {
        ["jsonrpc"] = "2.0", ["id"] = ask?["id"]?.DeepClone(),
        ["result"] = new JsonObject { ["action"] = "accept", ["content"] = new JsonObject { ["goal_primary"] = "balanced", ["goal_annoyances"] = new JsonArray("ramping") } },
    };
    var posted = await client.SendAsync(Msg(answer.ToJsonString()));
    Check("elicitation answer -> 202", (int)posted.StatusCode == 202, ((int)posted.StatusCode).ToString());

    var final = await NextEvent();
    var saved = final?["result"]?["structuredContent"]?["profile"]?["goal"];
    Check("form answers saved into profile", saved?["primary"]?.ToString() == "balanced" && saved?["annoyances"]?[0]?.ToString() == "ramping", final?.ToJsonString()[..Math.Min(200, final.ToJsonString().Length)] ?? "");

    // restore the user's profile as it was
    await Call("save_system_profile", new JsonObject { ["reset"] = true, ["patch"] = backup?["structuredContent"]?.DeepClone() ?? new JsonObject() });
}

// Hardware detection + baseline planning on data shaped like a real system (Ryzen 9600X, RTX 2070, 4 case fans, air cooler).
void WizardTests()
{
    var temps = JsonNode.Parse("""
        [
          {"id":"/amdcpu/0/temperature/2","name":"Core (Tctl/Tdie)","origin":"AMD Ryzen 5 9600X","value":72},
          {"id":"/gpu-amd/0/temperature/0","name":"GPU Core","origin":"AMD Radeon(TM) Graphics","value":44},
          {"id":"NVApiWrapper/0-TU106-A/sensor/0","name":"GPU","origin":"NVIDIA GeForce RTX 2070","value":69}
        ]
        """)!.AsArray();
    var fans = JsonNode.Parse("""
        [
          {"id":"/lpc/nct6799d/fan/0","name":"Fan #1","value":1064},{"id":"/lpc/nct6799d/fan/1","name":"CPU","value":1278},
          {"id":"/lpc/nct6799d/fan/2","name":"Front","value":1031},{"id":"/lpc/nct6799d/fan/3","name":"Fan #4","value":1065},
          {"id":"/lpc/nct6799d/fan/5","name":"Fan #6","value":1030},{"id":"/lpc/nct6799d/fan/4","name":"Fan #5","value":0},
          {"id":"NVApiWrapper/0-TU106-A/fan/0","name":"Fan 1 - NVIDIA GeForce RTX 2070","value":3040}
        ]
        """)!.AsArray();
    var controls = JsonNode.Parse("""
        [
          {"id":"/lpc/nct6799d/control/2","name":"Intake Front","originalName":"Fan #3","origin":"ASUS TUF GAMING B650-PLUS WIFI","pairedFanSensorId":"/lpc/nct6799d/fan/2","start":16,"stop":7,"enabled":true},
          {"id":"/lpc/nct6799d/control/5","name":"Intake Top","originalName":"Fan #6","origin":"ASUS TUF GAMING B650-PLUS WIFI","pairedFanSensorId":"/lpc/nct6799d/fan/5","start":16,"stop":7,"enabled":true},
          {"id":"/lpc/nct6799d/control/3","name":"Exhaust Top","originalName":"Fan #4","origin":"ASUS TUF GAMING B650-PLUS WIFI","pairedFanSensorId":"/lpc/nct6799d/fan/3","start":16,"stop":7,"enabled":true},
          {"id":"/lpc/nct6799d/control/0","name":"Exhaust Back","originalName":"Fan #1","origin":"ASUS TUF GAMING B650-PLUS WIFI","pairedFanSensorId":"/lpc/nct6799d/fan/0","start":16,"stop":7,"enabled":true},
          {"id":"/lpc/nct6799d/control/1","name":"CPU","originalName":"Fan #2","origin":"ASUS TUF GAMING B650-PLUS WIFI","pairedFanSensorId":"/lpc/nct6799d/fan/1","start":0,"stop":0,"enabled":true},
          {"id":"/lpc/nct6799d/control/4","name":"Fan #5","originalName":"Fan #5","origin":"ASUS TUF GAMING B650-PLUS WIFI","pairedFanSensorId":"/lpc/nct6799d/fan/4","enabled":false},
          {"id":"NVApiWrapper/0-TU106-A/control/0","name":"Control 1 - NVIDIA GeForce RTX 2070","originalName":"Control 1 - NVIDIA GeForce RTX 2070","origin":"NVIDIA GeForce RTX 2070","start":32,"stop":32,"enabled":true}
        ]
        """)!.AsArray();

    var detection = FanControl.MCP.Wizard.HardwareDetector.Detect(temps, fans, controls, [], ["explorer", "MSIAfterburner"]);
    Check("detect: CPU name", detection["cpu"]?["name"]?.ToString() == "AMD Ryzen 5 9600X");
    Check("detect: CPU family limits", detection["cpu"]?["limits"]?["family"]?.ToString() == "AMD Ryzen 7000/9000");
    var dgpu = detection["gpus"]!.AsArray()[0];
    Check("detect: discrete GPU first", dgpu?["name"]?.ToString() == "NVIDIA GeForce RTX 2070" && dgpu?["vendor"]?.ToString() == "nvidia", dgpu?["name"]?.ToString() ?? "");
    Check("detect: iGPU marked integrated", detection["gpus"]!.AsArray().Any(g => g?["integrated"]?.GetValue<bool>() == true));
    Check("detect: empty header", detection["controls"]!.AsArray().Any(c => c?["controlId"]?.ToString() == "/lpc/nct6799d/control/4" && c?["connected"]?.GetValue<bool?>() == false));
    Check("detect: Afterburner conflict", detection["fanSoftwareRunning"]!.AsArray().Any(s => s?["product"]?.ToString() == "MSI Afterburner"));

    var empty = new JsonObject();
    var state = FanControl.MCP.Wizard.SetupWizard.State(empty, detection);
    Check("wizard: starts at goal", state["nextStep"]?.ToString() == "goal");
    var fanRows = state["steps"]!.AsArray().First(s => s?["id"]?.ToString() == "fans")!["questions"]![0]!["rows"]!.AsArray();
    Check("wizard: fan rows prefilled", fanRows.Any(r => r?["prefill"]?["position"]?.ToString() == "rear" && r?["prefill"]?["direction"]?.ToString() == "exhaust"),
        string.Join("; ", fanRows.Select(r => $"{r?["name"]}={r?["prefill"]?["position"]}/{r?["prefill"]?["direction"]}")));

    var profile = JsonNode.Parse("""
        {"goal":{"primary":"quiet_no_perf_loss"},"environment":{"ambientC":22},"cpu":{"cooler":{"type":"air"}},
         "fans":[
           {"controlId":"/lpc/nct6799d/control/2","position":"front","direction":"intake","count":1},
           {"controlId":"/lpc/nct6799d/control/5","position":"top_front","direction":"intake"},
           {"controlId":"/lpc/nct6799d/control/3","position":"top_rear","direction":"exhaust"},
           {"controlId":"/lpc/nct6799d/control/0","position":"rear","direction":"exhaust"},
           {"controlId":"/lpc/nct6799d/control/1","position":"cpu_cooler","direction":"internal"},
           {"controlId":"NVApiWrapper/0-TU106-A/control/0","position":"gpu","direction":"internal"}]}
        """)!.AsObject();
    var plan = FanControl.MCP.Wizard.BaselinePlanner.Plan(profile, detection);
    var acts = plan["actions"]!.AsArray().OfType<JsonObject>().ToList();
    string Names(string type) => string.Join(",", acts.Where(a => a["type"]?.ToString() == type).Select(a => a["name"] ?? a["controlName"]));
    Check("plan: curves CPU/GPU/Case", Names("upsert_curve") == "MCP CPU,MCP GPU,MCP Case", Names("upsert_curve"));
    Check("plan: 6 assignments, empty header skipped", acts.Count(a => a["type"]?.ToString() == "assign") == 6 && acts.All(a => a["controlId"]?.ToString() != "/lpc/nct6799d/control/4"));
    Check("plan: exhausts -5 % for positive pressure", acts.Where(a => a["type"]?.ToString() == "assign" && a["controlName"]?.ToString()?.StartsWith("Exhaust") == true).All(a => a["offset"]?.GetValue<int>() == -5));
    Check("plan: no false short-circuit warning for top-front intake", !plan["warnings"]!.AsArray().Any(w => w!.ToString().Contains("short circuit", StringComparison.OrdinalIgnoreCase)));
    var gpuPts = acts.First(a => a["name"]?.ToString() == "MCP GPU")["points"]!.AsArray().Select(p => (T: p![0]!.GetValue<double>(), P: p[1]!.GetValue<double>())).ToList();
    Check("plan: GPU 100 % by 84 °C, floor ≥ 30 %", gpuPts[^1] == (84, 100) && gpuPts.Min(p => p.P) >= 30, string.Join(" ", gpuPts));
    var cpuPts = acts.First(a => a["name"]?.ToString() == "MCP CPU")["points"]!.AsArray().Select(p => (T: p![0]!.GetValue<double>(), P: p[1]!.GetValue<double>())).ToList();
    Check("plan: CPU curve monotonic, 100 % by 90 °C", cpuPts.Zip(cpuPts.Skip(1)).All(z => z.First.T < z.Second.T && z.First.P <= z.Second.P) && cpuPts[^1] == (90, 100), string.Join(" ", cpuPts));
    Check("plan: warns about Afterburner", plan["warnings"]!.AsArray().Any(w => w!.ToString().Contains("Afterburner")));

    // Liquid-cooled variant: pump fixed, radiator on liquid temperature.
    var aioProfile = profile.DeepClone().AsObject();
    aioProfile["cpu"] = JsonNode.Parse("""{"cooler":{"type":"aio","model":"Arctic Liquid Freezer III 360","radiatorPosition":"bottom"}}""");
    aioProfile["fans"] = JsonNode.Parse("""[{"controlId":"/lpc/nct6799d/control/1","position":"pump"},{"controlId":"/lpc/nct6799d/control/2","position":"radiator","direction":"intake"}]""");
    var liquidTemps = temps.DeepClone().AsArray();
    liquidTemps.Add(JsonNode.Parse("""{"id":"/aio/liquid","name":"Liquid Temperature","origin":"AIO","value":31}"""));
    var aioPlan = FanControl.MCP.Wizard.BaselinePlanner.Plan(aioProfile, FanControl.MCP.Wizard.HardwareDetector.Detect(liquidTemps, fans, controls, [], []));
    var aioActs = aioPlan["actions"]!.AsArray().OfType<JsonObject>().ToList();
    Check("plan AIO: Arctic pump at 100 %", aioActs.Any(a => a["name"]?.ToString() == "MCP Pump" && a["percent"]?.GetValue<int>() == 100));
    Check("plan AIO: radiator on liquid sensor", aioActs.Any(a => a["name"]?.ToString() == "MCP Radiator" && a["source"]?.ToString() == "/aio/liquid"));
    Check("plan AIO: bottom radiator warning", aioPlan["warnings"]!.AsArray().Any(w => w!.ToString().Contains("Bottom-mounted radiator")));

    // Profile merge: fans merged by controlId.
    var merged = FanControl.MCP.Wizard.SystemProfile.MergePatch(profile, JsonNode.Parse("""{"fans":[{"controlId":"/lpc/nct6799d/control/2","sizeMm":140}],"goal":{"notes":"x"}}"""));
    var front = merged["fans"]!.AsArray().First(f => f?["controlId"]?.ToString() == "/lpc/nct6799d/control/2");
    Check("profile merge keeps fields", front?["position"]?.ToString() == "front" && front?["sizeMm"]?.GetValue<int>() == 140 && merged["fans"]!.AsArray().Count == 6 && merged["goal"]?["primary"] is not null);
}

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
