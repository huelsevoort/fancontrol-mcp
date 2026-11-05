using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FanControl.MCP.Wizard;

/// <summary>
/// Derives what can be known automatically from FanControl's sensors (CPU/GPU model, primary temperature sensors,
/// connected fan headers, liquid sensors, load/clock sensors) plus running fan-related software.
/// Pure function over the JSON the engine bridge produces, so it is testable without FanControl.
/// </summary>
internal static class HardwareDetector
{
    /// <summary>Process names (without .exe, lower case) of tools that commonly fight FanControl for fan/GPU control.</summary>
    private static readonly (string Process, string Product, string Risk)[] KnownSoftware =
    [
        ("armourycrate.service", "ASUS Armoury Crate", "Controls motherboard fan headers and AIOs; can override FanControl."),
        ("armourycrate.usersessionhelper", "ASUS Armoury Crate", "Controls motherboard fan headers and AIOs; can override FanControl."),
        ("aisuite3", "ASUS AI Suite / Fan Xpert", "Controls motherboard fan headers."),
        ("icue", "Corsair iCUE", "Controls Corsair fans/AIO pumps; keep only one tool in charge of each fan."),
        ("nzxt cam", "NZXT CAM", "Controls NZXT AIO pump/fans."),
        ("cam", "NZXT CAM", "Controls NZXT AIO pump/fans."),
        ("msiafterburner", "MSI Afterburner", "Can apply its own GPU fan curve on top of FanControl."),
        ("msi center", "MSI Center", "Controls motherboard fans."),
        ("gcc", "Gigabyte Control Center", "Controls motherboard fans."),
        ("signalrgb", "SignalRGB", "Can take over fan/pump control of some AIOs and hubs."),
        ("l-connect 3", "Lian Li L-Connect", "Controls Lian Li fans/hubs."),
        ("lconnect3", "Lian Li L-Connect", "Controls Lian Li fans/hubs."),
        ("precisionx1", "EVGA Precision X1", "Applies its own GPU fan curve."),
        ("radeonsoftware", "AMD Adrenalin", "Its GPU tuning/fan settings can override FanControl on AMD GPUs."),
        ("hwinfo64", "HWiNFO", "Fine for monitoring; its fan control module must be off if used."),
        ("argusmonitor", "Argus Monitor", "Full fan control tool; only one fan controller should run."),
        ("speedfan", "SpeedFan", "Full fan control tool; only one fan controller should run."),
    ];

    public static JsonObject Detect(JsonArray temperatures, JsonArray fans, JsonArray controls, JsonArray hardwareSensors, IEnumerable<string> processNames)
    {
        var result = new JsonObject();

        // ---------------------------------------------------------------- CPU
        var cpuTemps = temperatures.OfType<JsonObject>().Where(t => Str(t, "id") is { } id && (id.StartsWith("/amdcpu", StringComparison.OrdinalIgnoreCase) || id.StartsWith("/intelcpu", StringComparison.OrdinalIgnoreCase))).ToList();
        var cpuName = cpuTemps.Select(t => Str(t, "origin")).FirstOrDefault(o => o is not null)
                      ?? hardwareSensors.OfType<JsonObject>().Where(s => Str(s, "id")?.Contains("cpu", StringComparison.OrdinalIgnoreCase) == true).Select(s => Str(s, "origin")).FirstOrDefault();
        var primaryCpu = cpuTemps.FirstOrDefault(t => Has(Str(t, "name"), "tctl", "package", "tdie"))
                         ?? cpuTemps.FirstOrDefault(t => Has(Str(t, "name"), "core (")) ?? cpuTemps.FirstOrDefault();
        var cpu = new JsonObject
        {
            ["name"] = cpuName,
            ["vendor"] = cpuName is null ? null : Has(cpuName, "amd", "ryzen") ? "amd" : Has(cpuName, "intel", "core") ? "intel" : "unknown",
            ["primaryTemperatureId"] = Str(primaryCpu, "id"),
            ["primaryTemperatureName"] = Str(primaryCpu, "name"),
            ["currentTemperature"] = primaryCpu?["value"]?.DeepClone(),
        };
        var limits = ThermalLimits.ForCpu(cpuName);
        cpu["limits"] = limits.ToJson();
        var cpuLoad = hardwareSensors.OfType<JsonObject>().FirstOrDefault(s => Str(s, "type") == "UsagePercent" && Has(Str(s, "id"), "cpu") && Has(Str(s, "name"), "total"));
        if (cpuLoad is not null) cpu["loadSensorId"] = Str(cpuLoad, "id");
        var cpuClocks = hardwareSensors.OfType<JsonObject>().Where(s => Str(s, "type") == "Frequency" && Has(Str(s, "id"), "cpu") && Has(Str(s, "name"), "core #", "p-core", "core ")).Select(s => Str(s, "id")).OfType<string>().ToList();
        if (cpuClocks.Count > 0) cpu["clockSensorIds"] = new JsonArray(cpuClocks.Select(c => (JsonNode)c).ToArray());
        result["cpu"] = cpu;

        // ---------------------------------------------------------------- GPUs (discrete first)
        var gpus = new JsonArray();
        var gpuTemps = temperatures.OfType<JsonObject>().Where(t => Has(Str(t, "id"), "gpu", "nvapi", "adlx") && Str(t, "customSensorType") is null).ToList();
        foreach (var group in gpuTemps.GroupBy(t => Str(t, "origin") ?? "GPU"))
        {
            var name = group.Key;
            var integrated = Has(name, "radeon(tm) graphics", "uhd graphics", "iris", "intel(r) graphics") && !Has(name, "rx ", "arc a", "arc b");
            var vendor = Has(name, "nvidia", "geforce", "rtx", "gtx") ? "nvidia" : Has(name, "radeon", "amd") ? "amd" : Has(name, "intel", "arc") ? "intel" : "unknown";
            var core = group.FirstOrDefault(t => Has(Str(t, "name"), "core", "gpu") && !Has(Str(t, "name"), "hot", "junction", "memory", "vr ", "soc")) ?? group.First();
            var hot = group.FirstOrDefault(t => Has(Str(t, "name"), "hot spot", "hotspot", "junction"));
            var gpuControls = controls.OfType<JsonObject>().Where(c => Has($"{Str(c, "id")} {Str(c, "originalName")} {Str(c, "origin")}", name.ToLowerInvariant(), "nvapi", "adlx") && !integrated).Select(c => Str(c, "id")).OfType<string>().ToList();
            var g = new JsonObject
            {
                ["name"] = name,
                ["vendor"] = vendor,
                ["integrated"] = integrated,
                ["temperatureId"] = Str(core, "id"),
                ["currentTemperature"] = core["value"]?.DeepClone(),
                ["hotspotId"] = Str(hot, "id"),
                ["controlIds"] = new JsonArray(gpuControls.Select(c => (JsonNode)c).ToArray()),
                ["limits"] = ThermalLimits.ForGpu(name, vendor).ToJson(),
            };
            gpus.Add(g);
        }
        result["gpus"] = new JsonArray(gpus.OfType<JsonObject>().OrderBy(g => g["integrated"]?.GetValue<bool>() == true).Select(g => (JsonNode)g.DeepClone()).ToArray());

        // ---------------------------------------------------------------- mainboard, liquid
        result["mainboard"] = controls.OfType<JsonObject>().Where(c => Str(c, "id")?.StartsWith("/lpc", StringComparison.OrdinalIgnoreCase) == true).Select(c => Str(c, "origin")).FirstOrDefault()
                              ?? temperatures.OfType<JsonObject>().Where(t => Str(t, "id")?.StartsWith("/lpc", StringComparison.OrdinalIgnoreCase) == true).Select(t => Str(t, "origin")).FirstOrDefault();
        var liquid = temperatures.OfType<JsonObject>().Where(t => Has($"{Str(t, "name")} {Str(t, "originalName")} {Str(t, "origin")}", "liquid", "coolant", "water", "fluid")).Select(t => new JsonObject { ["id"] = Str(t, "id"), ["name"] = Str(t, "name"), ["value"] = t["value"]?.DeepClone() }).ToList();
        result["liquidSensors"] = new JsonArray(liquid.Select(l => (JsonNode)l).ToArray());

        // ---------------------------------------------------------------- headers
        var rpmById = fans.OfType<JsonObject>().Where(f => Str(f, "id") is not null).ToDictionary(f => Str(f, "id")!, f => Num(f, "value"), StringComparer.OrdinalIgnoreCase);
        var pairedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var headers = new JsonArray();
        foreach (var c in controls.OfType<JsonObject>())
        {
            var paired = Str(c, "pairedFanSensorId");
            if (paired is not null) pairedIds.Add(paired);
            double? rpm = paired is not null && rpmById.TryGetValue(paired, out var r) ? r : null;
            headers.Add(new JsonObject
            {
                ["controlId"] = Str(c, "id"),
                ["name"] = Str(c, "name"),
                ["originalName"] = Str(c, "originalName"),
                ["origin"] = Str(c, "origin"),
                ["percent"] = c["currentPercent"]?.DeepClone(),
                ["rpmSensorId"] = paired,
                ["rpm"] = rpm,
                ["connected"] = rpm is null ? null : rpm > 0,
                ["enabled"] = c["enabled"]?.DeepClone(),
                ["fanCurve"] = Str(c, "fanCurve"),
                ["start"] = c["start"]?.DeepClone(),
                ["stop"] = c["stop"]?.DeepClone(),
            });
        }
        result["controls"] = headers;
        var unpaired = fans.OfType<JsonObject>().Where(f => Str(f, "id") is { } id && !pairedIds.Contains(id) && (Num(f, "value") ?? 0) > 0)
            .Select(f => new JsonObject { ["id"] = Str(f, "id"), ["name"] = Str(f, "name"), ["rpm"] = f["value"]?.DeepClone() }).ToList();
        result["unpairedSpinningFans"] = new JsonArray(unpaired.Select(u => (JsonNode)u).ToArray());
        result["idleHeaders"] = new JsonArray(fans.OfType<JsonObject>().Where(f => (Num(f, "value") ?? -1) == 0).Select(f => (JsonNode)(Str(f, "name") ?? "")).ToArray());

        // ---------------------------------------------------------------- software
        var procs = processNames.Select(p => p.ToLowerInvariant()).ToHashSet();
        var conflicts = KnownSoftware.Where(k => procs.Contains(k.Process)).GroupBy(k => k.Product)
            .Select(g => new JsonObject { ["product"] = g.Key, ["risk"] = g.First().Risk }).ToList();
        result["fanSoftwareRunning"] = new JsonArray(conflicts.Select(c => (JsonNode)c).ToArray());
        result["loadSensorsAvailable"] = hardwareSensors.Count > 0;
        return result;
    }

    private static bool Has(string? text, params string[] needles) => text is not null && needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));
    private static string? Str(JsonNode? o, string key) => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static double? Num(JsonNode? o, string key) => o?[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}

/// <summary>
/// Temperatures that drive curve end points. "NoLossBelow": no measurable clock loss below this; "FullSpeedBy": the
/// curve should be at 100 % here; "Alarm": warn the user / stop experiments. Values per research (docs/research).
/// </summary>
internal sealed record ThermalLimits(string Family, double NoLossBelow, double FullSpeedBy, double Alarm, string Note)
{
    public JsonObject ToJson() => new()
    {
        ["family"] = Family, ["noLossBelow"] = NoLossBelow, ["fullSpeedBy"] = FullSpeedBy, ["alarm"] = Alarm, ["note"] = Note,
    };

    // Values: docs/research/thermal-limits.md. "NoLossBelow" is where temperature-driven clock loss stays below ~1 %:
    // Precision Boost and GPU Boost lose clock gradually (no hard step), so this is a practical threshold, not a cliff.
    public static ThermalLimits ForCpu(string? name)
    {
        var n = name ?? "";
        if (Regex.IsMatch(n, @"Ryzen.*\b(7\d{3}X3D|5[78]00X3D)", RegexOptions.IgnoreCase))
            return new("AMD Ryzen X3D (5000/7000)", 75, 85, 89, "Tjmax 89 °C (5800X3D/5700X3D: 90 °C). Lower limit than non-X3D parts.");
        if (Regex.IsMatch(n, @"Ryzen.*\b9\d{3}X3D", RegexOptions.IgnoreCase))
            return new("AMD Ryzen 9000 X3D", 80, 88, 95, "Tjmax 95 °C like other Ryzen 9000.");
        if (Regex.IsMatch(n, @"Ryzen.*\b[79]\d{3}", RegexOptions.IgnoreCase))
            return new("AMD Ryzen 7000/9000", 82, 90, 95, "Tjmax 95 °C; reaching 95 °C under all-core load is by design. Precision Boost loses roughly 8 MHz per °C above ~55 °C (<1 % per 5 °C), so there is no hard cliff.");
        if (Regex.IsMatch(n, @"Ryzen.*\b5\d{3}", RegexOptions.IgnoreCase))
            return new("AMD Ryzen 5000", 75, 85, 90, "Tjmax 90 °C (5600X: 95 °C).");
        if (Regex.IsMatch(n, @"Ryzen", RegexOptions.IgnoreCase))
            return new("AMD Ryzen (older)", 75, 85, 90, "Conservative defaults for older Ryzen.");
        if (Regex.IsMatch(n, @"Core\s*(\(TM\)\s*)?Ultra", RegexOptions.IgnoreCase))
            return new("Intel Core Ultra 200S", 88, 93, 105, "TjMax 105 °C; below that clocks are power-limited, not thermal (TVB parts lose one 100 MHz bin above 70 °C).");
        if (Regex.IsMatch(n, @"Intel", RegexOptions.IgnoreCase))
            return new("Intel Core 12th-14th gen", 85, 90, 100, "TjMax 100 °C; below that clocks are power-limited (i9 TVB loses one 100 MHz bin above 70 °C).");
        return new("Unknown CPU", 75, 85, 90, "Unknown model: conservative defaults.");
    }

    public static ThermalLimits ForGpu(string? name, string vendor)
    {
        if (vendor == "nvidia")
            return new("NVIDIA GeForce", 80, 84, 87, "GPU Boost drops 15 MHz bins as temperature rises (<1 % each); default temperature target 83 °C. Usually power-limited before thermal. Use the core sensor, not the hot spot.");
        if (vendor == "amd")
            return new("AMD Radeon", 75, 82, 90, "Edge temperature; junction/hot spot runs 15-25 °C higher and throttles at 110 °C. Edge-junction gap > 30 °C suggests a cooler problem.");
        if (vendor == "intel")
            return new("Intel Arc", 75, 85, 90, "Conservative defaults.");
        return new("Unknown GPU", 75, 82, 88, "Unknown model: conservative defaults.");
    }
}
