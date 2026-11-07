using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using FanControl.Domain;
using FanControl.MCP.Engine;
using FanControl.MCP.Mcp;
using FanControl.Serializable.Interfaces;

namespace FanControl.MCP.Wizard;

/// <summary>
/// Records temperatures, fan speeds, control outputs and load/clock/power while the user runs a real workload,
/// split into labelled phases (A/B testing of fan settings). An optional guard restores a restore point when a
/// temperature limit is crossed. Runs on its own timer, independent of the MCP client.
/// </summary>
internal sealed class MeasurementRecorder : IDisposable
{
    private const int MaxSamples = 4000;

    private readonly Func<EngineBridge?> _bridge;
    private readonly Action<string> _log;
    private readonly object _lock = new();
    private readonly List<Sample> _samples = [];
    private readonly List<JsonObject> _events = [];
    private Timer? _timer;
    private DateTime _started;
    private DateTime _endsAt;
    private string _phase = "";
    private Guard? _guard;
    private Keys? _keys;
    private int _sampling;
    private readonly string? _nvidiaSmi;

    private sealed record Sample(DateTime Time, string Phase, Dictionary<string, double> Values, string? GpuThrottle);
    private sealed record Guard(double CpuMax, double GpuMax, string RestorePointId);
    private sealed record Keys(string? CpuTemp, string? GpuTemp, IReadOnlyList<(string Id, string Name)> Fans, IReadOnlyList<string> CpuClocks, string? CpuEffective,
        string? CpuLoad, string? GpuLoad, string? GpuClock, string? GpuPower, string? CpuPower);

    public MeasurementRecorder(Func<EngineBridge?> bridge, Action<string> log)
    {
        _bridge = bridge;
        _log = log;
        var smi = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
        _nvidiaSmi = File.Exists(smi) ? smi : null;
    }

    public bool Running { get { lock (_lock) return _timer is not null; } }

    public JsonObject Start(string phase, int intervalSeconds, int maxMinutes, double? cpuMax, double? gpuMax, bool guard)
    {
        var bridge = _bridge() ?? throw new ToolException("FanControl engine not reachable.");
        intervalSeconds = Math.Clamp(intervalSeconds, 2, 60);
        maxMinutes = Math.Clamp(maxMinutes, 1, 180);

        var temps = bridge.Temperatures(false);
        var detection = HardwareDetector.Detect(temps, bridge.Fans(false), bridge.Controls(false),
            bridge.HardwareSensorsJson(BackendSensorType.UsagePercent, BackendSensorType.Frequency, BackendSensorType.Power), []);
        var keys = BuildKeys(detection, bridge);

        Guard? g = null;
        if (guard)
        {
            var cpuLimit = cpuMax ?? Num(detection["cpu"]?["limits"]?["alarm"]) ?? 92;
            var dgpu = (detection["gpus"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(x => x["integrated"]?.GetValue<bool>() != true);
            var gpuLimit = gpuMax ?? Num(dgpu?["limits"]?["alarm"]) ?? 86;
            var rp = bridge.CreateRestorePoint($"before measurement '{phase}'");
            g = new Guard(cpuLimit, gpuLimit, rp["id"]!.ToString());
        }

        lock (_lock)
        {
            _timer?.Dispose();
            _samples.Clear();
            _events.Clear();
            _keys = keys;
            _guard = g;
            _phase = string.IsNullOrWhiteSpace(phase) ? "A" : phase.Trim();
            _started = DateTime.Now;
            _endsAt = _started.AddMinutes(maxMinutes);
            _timer = new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(intervalSeconds));
        }
        AddEvent("start", $"phase '{_phase}'");
        return Status();
    }

    public JsonObject MarkPhase(string phase)
    {
        if (!Running) throw new ToolException("No measurement is running. Call start_measurement first.");
        lock (_lock) _phase = string.IsNullOrWhiteSpace(phase) ? $"phase {DateTime.Now:HHmmss}" : phase.Trim();
        AddEvent("phase", _phase);
        return Status();
    }

    public JsonObject Stop()
    {
        lock (_lock) { _timer?.Dispose(); _timer = null; }
        AddEvent("stop", "");
        return Status();
    }

    public JsonObject Status()
    {
        lock (_lock)
        {
            return new JsonObject
            {
                ["running"] = _timer is not null,
                ["phase"] = _phase,
                ["started"] = _started == default ? null : _started.ToString("HH:mm:ss"),
                ["endsAt"] = _timer is null ? null : _endsAt.ToString("HH:mm:ss"),
                ["samples"] = _samples.Count,
                ["guard"] = _guard is null ? null : new JsonObject { ["cpuMax"] = _guard.CpuMax, ["gpuMax"] = _guard.GpuMax, ["restorePoint"] = _guard.RestorePointId },
                ["sources"] = _keys is null ? null : new JsonObject
                {
                    ["cpuTemperature"] = _keys.CpuTemp, ["gpuTemperature"] = _keys.GpuTemp,
                    ["cpuClocks"] = _keys.CpuClocks.Count, ["gpu"] = _nvidiaSmi is not null ? "nvidia-smi" : _keys.GpuClock is not null ? "FanControl sensors" : "temperature only",
                },
                ["events"] = new JsonArray(_events.Select(e => (JsonNode)e.DeepClone()).ToArray()),
            };
        }
    }

    /// <summary>Per-phase averages/maxima; the first <paramref name="skipSeconds"/> of every phase are ignored (settling).</summary>
    public JsonObject Summary(int skipSeconds)
    {
        List<Sample> samples;
        lock (_lock) samples = [.. _samples];
        var phases = new JsonArray();
        foreach (var group in samples.GroupBy(s => s.Phase))
        {
            var list = group.ToList();
            var start = list[0].Time;
            var settled = list.Where(s => (s.Time - start).TotalSeconds >= skipSeconds).ToList();
            if (settled.Count == 0) settled = list;
            var keys = settled.SelectMany(s => s.Values.Keys).Distinct().OrderBy(k => Order(k)).ThenBy(k => k);
            var metrics = new JsonObject();
            foreach (var k in keys)
            {
                var vals = settled.Where(s => s.Values.ContainsKey(k)).Select(s => s.Values[k]).ToList();
                if (vals.Count == 0) continue;
                metrics[k] = new JsonObject { ["avg"] = Math.Round(vals.Average(), 1), ["max"] = Math.Round(vals.Max(), 1), ["min"] = Math.Round(vals.Min(), 1) };
            }
            var throttled = settled.Where(s => s.GpuThrottle is not null and not "none" and not "idle").GroupBy(s => s.GpuThrottle!).ToList();
            phases.Add(new JsonObject
            {
                ["phase"] = group.Key,
                ["from"] = start.ToString("HH:mm:ss"),
                ["to"] = list[^1].Time.ToString("HH:mm:ss"),
                ["samplesUsed"] = settled.Count,
                ["metrics"] = metrics,
                ["gpuLimitReasons"] = new JsonObject(throttled.Select(t => KeyValuePair.Create(t.Key, (JsonNode?)JsonValue.Create(t.Count())))),
            });
        }
        return new JsonObject
        {
            ["phases"] = phases,
            ["note"] = "Compare phases under the same workload. Temperature-driven clock loss shows as lower cpu_clock_mhz/gpu_clock_mhz at similar load; " +
                       "gpuLimitReasons 'power' means the GPU is power-limited (fans cannot help), 'thermal' means it is too hot.",
            ["status"] = Status(),
        };
    }

    public void Dispose()
    {
        lock (_lock) { _timer?.Dispose(); _timer = null; }
    }

    // ------------------------------------------------------------------ sampling

    private void Tick()
    {
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return; // previous tick still running
        try
        {
            if (DateTime.Now > _endsAt) { Stop(); AddEvent("auto-stop", "maximum duration reached"); return; }
            var bridge = _bridge();
            var keys = _keys;
            if (bridge is null || keys is null) return;

            var values = new Dictionary<string, double>();
            var temps = bridge.Temperatures(true).OfType<JsonObject>().ToDictionary(t => t["id"]!.ToString(), t => Num(t["value"]), StringComparer.OrdinalIgnoreCase);
            if (keys.CpuTemp is not null && temps.GetValueOrDefault(keys.CpuTemp) is { } ct) values["cpu_temp_c"] = ct;
            if (keys.GpuTemp is not null && temps.GetValueOrDefault(keys.GpuTemp) is { } gt) values["gpu_temp_c"] = gt;

            var fans = bridge.Fans(true).OfType<JsonObject>().ToDictionary(f => f["id"]!.ToString(), f => Num(f["value"]), StringComparer.OrdinalIgnoreCase);
            foreach (var (id, name) in keys.Fans)
                if (fans.GetValueOrDefault(id) is { } rpm) values[$"rpm {name}"] = rpm;
            foreach (var c in bridge.Controls(false).OfType<JsonObject>())
                if (Num(c["currentPercent"]) is { } pct) values[$"pct {c["name"]}"] = pct;

            var hw = bridge.HardwareSensors(BackendSensorType.UsagePercent, BackendSensorType.Frequency, BackendSensorType.Power)
                .ToDictionary(s => s.Identifier, s => s.Value, StringComparer.OrdinalIgnoreCase);
            var clocks = keys.CpuClocks.Select(k => hw.GetValueOrDefault(k)).Where(v => v is > 0).Select(v => (double)v!.Value).ToList();
            if (clocks.Count > 0) { values["cpu_clock_mhz"] = clocks.Average(); values["cpu_clock_max_mhz"] = clocks.Max(); }
            if (keys.CpuEffective is not null && hw.GetValueOrDefault(keys.CpuEffective) is { } ce) values["cpu_effective_clock_mhz"] = ce;
            if (keys.CpuLoad is not null && hw.GetValueOrDefault(keys.CpuLoad) is { } cl) values["cpu_load_pct"] = cl;
            if (keys.CpuPower is not null && hw.GetValueOrDefault(keys.CpuPower) is { } cp) values["cpu_power_w"] = cp;
            if (keys.GpuLoad is not null && hw.GetValueOrDefault(keys.GpuLoad) is { } gl) values["gpu_load_pct"] = gl;
            if (keys.GpuClock is not null && hw.GetValueOrDefault(keys.GpuClock) is { } gc) values["gpu_clock_mhz"] = gc;
            if (keys.GpuPower is not null && hw.GetValueOrDefault(keys.GpuPower) is { } gp) values["gpu_power_w"] = gp;

            string? throttle = null;
            if (_nvidiaSmi is not null && QueryNvidia() is { } nv)
            {
                values["gpu_load_pct"] = nv.Util;
                values["gpu_clock_mhz"] = nv.Clock;
                values["gpu_power_w"] = nv.Power;
                throttle = nv.Throttle;
            }

            string phase;
            lock (_lock)
            {
                phase = _phase;
                _samples.Add(new Sample(DateTime.Now, phase, values, throttle));
                if (_samples.Count > MaxSamples) _samples.RemoveAt(0);
            }
            CheckGuard(bridge, values);
        }
        catch (Exception e)
        {
            _log($"Measurement sample failed: {e.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _sampling, 0);
        }
    }

    private void CheckGuard(EngineBridge bridge, Dictionary<string, double> values)
    {
        Guard? g;
        lock (_lock) g = _guard;
        if (g is null) return;
        var cpu = values.GetValueOrDefault("cpu_temp_c");
        var gpu = values.GetValueOrDefault("gpu_temp_c");
        if (cpu < g.CpuMax && gpu < g.GpuMax) return;

        lock (_lock) { _guard = null; _phase = "SAFETY restore"; }
        try
        {
            bridge.Restore(g.RestorePointId);
            AddEvent("guard", $"CPU {cpu:0.#} °C / GPU {gpu:0.#} °C reached the limit ({g.CpuMax:0}/{g.GpuMax:0} °C): settings restored from {g.RestorePointId}.");
        }
        catch (Exception e)
        {
            AddEvent("guard-error", $"Limit reached but restore failed: {e.Message}");
        }
    }

    private sealed record Nvidia(double Util, double Clock, double Power, string Throttle);

    private Nvidia? QueryNvidia()
    {
        try
        {
            var psi = new ProcessStartInfo(_nvidiaSmi!, "--query-gpu=utilization.gpu,clocks.gr,power.draw,clocks_throttle_reasons.active --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var line = p.StandardOutput.ReadLine();
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return null; }
            var parts = line?.Split(',').Select(x => x.Trim()).ToArray();
            if (parts is not { Length: >= 4 }) return null;
            var inv = CultureInfo.InvariantCulture;
            if (!double.TryParse(parts[0], NumberStyles.Float, inv, out var util) || !double.TryParse(parts[1], NumberStyles.Float, inv, out var clock)) return null;
            double.TryParse(parts[2], NumberStyles.Float, inv, out var power);
            var reasons = Convert.ToUInt64(parts[3].Replace("0x", ""), 16);
            // NVML clock event reasons: 0x1 idle, 0x4 SW power cap, 0x8 HW slowdown, 0x20 SW thermal, 0x40 HW thermal, 0x80 power brake.
            var throttle = reasons switch
            {
                0 => "none",
                1 => "idle",
                _ when (reasons & 0x60) != 0 => "thermal",
                _ when (reasons & 0x8C) != 0 => "power",
                _ => $"0x{reasons:x}",
            };
            return new Nvidia(util, clock, power, throttle);
        }
        catch
        {
            return null;
        }
    }

    private static Keys BuildKeys(JsonObject detection, EngineBridge bridge)
    {
        var cpu = detection["cpu"];
        var dgpu = (detection["gpus"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(x => x["integrated"]?.GetValue<bool>() != true);
        var fans = (detection["controls"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(c => c["rpmSensorId"] is not null && c["connected"]?.GetValue<bool?>() != false)
            .Select(c => (c["rpmSensorId"]!.ToString(), c["name"]?.ToString() ?? c["rpmSensorId"]!.ToString())).ToList();
        // Also GPU fans that are not paired with a control.
        foreach (var f in bridge.Fans(false).OfType<JsonObject>())
        {
            var id = f["id"]?.ToString();
            if (id is not null && (id.Contains("nvapi", StringComparison.OrdinalIgnoreCase) || id.Contains("gpu", StringComparison.OrdinalIgnoreCase)) && fans.All(x => x.Item1 != id))
                fans.Add((id, f["name"]?.ToString() ?? id));
        }

        var hw = bridge.HardwareSensors(BackendSensorType.UsagePercent, BackendSensorType.Frequency, BackendSensorType.Power);
        bool IsCpu(ISensorSource s) => s.Identifier.Contains("cpu", StringComparison.OrdinalIgnoreCase);
        bool IsDGpu(ISensorSource s) => dgpu is not null && (s.Origin == dgpu["name"]?.ToString() || s.Identifier.Contains("gpu-nvidia", StringComparison.OrdinalIgnoreCase));
        return new Keys(
            cpu?["primaryTemperatureId"]?.ToString(),
            dgpu?["temperatureId"]?.ToString(),
            fans,
            hw.Where(s => IsCpu(s) && s.Type == BackendSensorType.Frequency && System.Text.RegularExpressions.Regex.IsMatch(s.Name, @"^(P-)?Core #\d+$")).Select(s => s.Identifier).ToList(),
            hw.FirstOrDefault(s => IsCpu(s) && s.Type == BackendSensorType.Frequency && s.Name.Contains("Average Effective", StringComparison.OrdinalIgnoreCase))?.Identifier,
            hw.FirstOrDefault(s => IsCpu(s) && s.Type == BackendSensorType.UsagePercent && s.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))?.Identifier,
            hw.FirstOrDefault(s => IsDGpu(s) && s.Type == BackendSensorType.UsagePercent && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))?.Identifier,
            hw.FirstOrDefault(s => IsDGpu(s) && s.Type == BackendSensorType.Frequency && s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))?.Identifier,
            hw.FirstOrDefault(s => IsDGpu(s) && s.Type == BackendSensorType.Power)?.Identifier,
            hw.FirstOrDefault(s => IsCpu(s) && s.Type == BackendSensorType.Power && s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))?.Identifier);
    }

    private void AddEvent(string type, string text)
    {
        lock (_lock)
        {
            _events.Add(new JsonObject { ["time"] = DateTime.Now.ToString("HH:mm:ss"), ["type"] = type, ["text"] = text });
            if (_events.Count > 100) _events.RemoveAt(0);
        }
    }

    private static int Order(string key) => key switch
    {
        "cpu_temp_c" => 0, "gpu_temp_c" => 1, "cpu_clock_mhz" => 2, "cpu_clock_max_mhz" => 3, "cpu_effective_clock_mhz" => 3, "cpu_load_pct" => 4, "cpu_power_w" => 5,
        "gpu_clock_mhz" => 6, "gpu_load_pct" => 7, "gpu_power_w" => 8,
        _ => key.StartsWith("rpm", StringComparison.Ordinal) ? 10 : 20,
    };

    private static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}
