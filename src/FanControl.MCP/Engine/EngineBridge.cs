using System.Reflection;
using System.Text.Json.Nodes;
using FanControl.Domain;
using FanControl.MCP.Mcp;
using FanControl.Domain.Engine;
using FanControl.Domain.Interfaces;
using FanControl.Serializable;
using FanControl.Serializable.Interfaces;

namespace FanControl.MCP.Engine;

/// <summary>
/// Read/write access to FanControl's engine (sensors, controls, fan curves, configurations).
/// Compiled against FanControl.Library; every call is guarded so that an incompatible FanControl
/// version degrades to an error message instead of crashing FanControl.
/// </summary>
internal sealed class EngineBridge
{
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private readonly IFanControlEngine _engine;
    private readonly IApplicationState? _state;
    private readonly object? _engineLock;

    public EngineBridge(IFanControlEngine engine)
    {
        _engine = engine;
        _state = GetField<IApplicationState>(engine, "_state");
        _engineLock = GetField<object>(engine, "_lock");
    }

    public bool CanManageConfigs => _state is not null;

    // ------------------------------------------------------------------ reading

    public JsonObject Status()
    {
        return new JsonObject
        {
            ["engineAttached"] = true,
            ["currentConfig"] = _state?.CurrentConfigFileName,
            ["configFolder"] = _state?.ConfigFolder,
            ["temperatureCount"] = Read(() => _engine.Temperatures.Count),
            ["customSensorCount"] = Read(() => _engine.CustomSensors.Count),
            ["fanSensorCount"] = Read(() => _engine.SpeedSensors.Count),
            ["controlCount"] = Read(() => _engine.Controls.Count),
            ["fanCurveCount"] = Read(() => _engine.FanCurves.Count),
        };
    }

    public JsonArray Temperatures(bool includeHidden)
        => Read(() => SensorsToJson(_engine.Temperatures.Cast<object>().Concat(_engine.CustomSensors), includeHidden));

    public JsonArray Fans(bool includeHidden)
        => Read(() => SensorsToJson(_engine.SpeedSensors.Cast<object>(), includeHidden));

    public JsonArray Controls(bool includeHidden)
        => Read(() =>
        {
            var arr = new JsonArray();
            foreach (var c in _engine.Controls.Cast<object>())
            {
                var json = ControlToJson(c);
                if (json is null) continue;
                if (!includeHidden && json["hidden"]?.GetValue<bool>() == true) continue;
                arr.Add(json);
            }
            return arr;
        });

    public JsonArray FanCurves()
        => Read(() =>
        {
            var arr = new JsonArray();
            foreach (var curve in _engine.FanCurves.Cast<object>())
            {
                var o = new JsonObject
                {
                    ["name"] = (curve as IFanCurveId)?.Name,
                    ["type"] = curve.GetType().Name,
                };
                if (curve is ICommandMode mode) o["commandMode"] = mode.CommandMode.ToString();
                if (curve is IFanCurveCommand cmd) o["currentOutput"] = Round(cmd.GetCommand());
                var config = (curve as IConfigured<IFanCurveConfig>)?.Config ?? GetProperty(curve, "Config");
                if (config is not null) o["config"] = ConfigToJson(config);
                arr.Add(o);
            }
            return arr;
        });

    public JsonArray Configs()
    {
        var state = _state ?? throw new InvalidOperationException("Configuration management is not available in this FanControl version.");
        var arr = new JsonArray();
        foreach (var name in state.GetAvailableConfigs())
            arr.Add(new JsonObject { ["name"] = name, ["current"] = string.Equals(name, state.CurrentConfigFileName, StringComparison.OrdinalIgnoreCase) });
        return arr;
    }

    // ------------------------------------------------------------------ writing

    public JsonObject SetManual(string controlRef, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        return UpdateControl(controlRef, cfg =>
        {
            cfg.Enable = true;
            cfg.ManualControl = true;
            cfg.ManualControlValue = percent;
        });
    }

    public JsonObject SetAutomatic(string controlRef, string? fanCurve)
    {
        IFanCurveId? curveId = null;
        if (!string.IsNullOrWhiteSpace(fanCurve))
        {
            var match = Read(() => _engine.FanCurves.Cast<object>().OfType<IFanCurveId>()
                .FirstOrDefault(c => string.Equals(c.Name, fanCurve, StringComparison.OrdinalIgnoreCase)));
            if (match is null)
                throw new ToolException($"Fan curve '{fanCurve}' not found. Available: {string.Join(", ", Read(() => _engine.FanCurves.Cast<object>().OfType<IFanCurveId>().Select(c => c.Name)))}");
            curveId = new SerializableFanCurve(match);
        }

        return UpdateControl(controlRef, cfg =>
        {
            cfg.Enable = true;
            cfg.ManualControl = false;
            if (curveId is not null) cfg.SelectedFanCurve = curveId;
        });
    }

    public JsonObject SetEnabled(string controlRef, bool enabled)
        => UpdateControl(controlRef, cfg => cfg.Enable = enabled);

    public JsonObject LoadConfig(string name)
    {
        var state = _state ?? throw new InvalidOperationException("Configuration management is not available in this FanControl version.");
        var available = state.GetAvailableConfigs();
        var match = available.FirstOrDefault(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase))
                    ?? available.FirstOrDefault(c => string.Equals(Path.GetFileNameWithoutExtension(c), Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase))
                    ?? throw new ToolException($"Configuration '{name}' not found. Available: {string.Join(", ", available)}");
        var ok = OnFanControlThread(() => state.Load(match));
        if (!ok) throw new ToolException($"FanControl refused to load '{match}'.");
        return new JsonObject { ["loaded"] = match };
    }

    public JsonObject SaveConfig()
    {
        var state = _state ?? throw new InvalidOperationException("Configuration management is not available in this FanControl version.");
        var file = state.CurrentConfigFileName;
        var ok = OnFanControlThread(() => state.SaveAllConfig(file));
        if (!ok) throw new ToolException($"FanControl could not save '{file}'.");
        return new JsonObject { ["saved"] = file };
    }

    // ------------------------------------------------------------------ curve & tuning editing

    public JsonObject UpdateControlTuning(string controlRef, ControlTuning t)
    {
        static int Pct(int? v, string name) => v is null or (>= 0 and <= 100) ? v ?? 0 : throw new ToolException($"{name} must be 0-100.");
        Pct(t.StepUp, "step_up"); Pct(t.StepDown, "step_down"); Pct(t.Start, "start"); Pct(t.Stop, "stop"); Pct(t.MinimumPercent, "minimum_percent");
        if (t.StepUp is 0 || t.StepDown is 0) throw new ToolException("step_up/step_down must be at least 1 %/s.");
        if (t.Offset is < -100 or > 100) throw new ToolException("offset must be between -100 and 100.");

        return UpdateControl(controlRef, cfg =>
        {
            if (t.StepUp is { } up) cfg.SelectedCommandStepUp = up;
            if (t.StepDown is { } down) cfg.SelectedCommandStepDown = down;
            if (t.Start is { } start) cfg.SelectedStart = start;
            if (t.Stop is { } stop) cfg.SelectedStop = stop;
            if (t.MinimumPercent is { } min) cfg.MinimumPercent = min;
            if (t.Offset is { } offset) cfg.SelectedOffset = offset;
        });
    }

    public JsonObject UpdateFanCurve(string curveName, FanCurveChange change)
    {
        var curve = FindCurve(curveName);
        var source = change.TemperatureSource is null ? null : new SerializableSensorId { Identifier = ResolveTemperatureId(change.TemperatureSource) };
        change.Validate();

        OnFanControlThread(() =>
        {
            switch (curve)
            {
                case IConfigurable<IGraphFanCurveConfig> graph:
                {
                    if (change.LinearOnlySet) throw new ToolException("min/max temperature and speed apply to linear curves; use 'points' for graph curves.");
                    if (change.Percent is not null) throw new ToolException("'percent' applies to flat curves only.");
                    var c = new SerializableGraphFanCurve(graph.Config);
                    if (change.Points is { } pts) c.Points = pts.Select(p => new System.Windows.Point(p.Temperature, p.Percent)).ToList();
                    if (source is not null) c.SelectedTempSource = source;
                    c.HysteresisConfig = change.ApplyTo(c.HysteresisConfig);
                    graph.UpdateConfig(c);
                    break;
                }
                case IConfigurable<ILinearFanCurveConfig> linear:
                {
                    if (change.Points is not null) throw new ToolException("Linear curves have no points; use min/max temperature and speed.");
                    if (change.Percent is not null) throw new ToolException("'percent' applies to flat curves only.");
                    var c = new SerializableLinearFanCurve(linear.Config);
                    if (change.MinTemperature is { } tmin) c.MinimumTemperature = tmin;
                    if (change.MaxTemperature is { } tmax) c.MaximumTemperature = tmax;
                    if (change.MinSpeed is { } smin) c.MinimumFanSpeed = smin;
                    if (change.MaxSpeed is { } smax) c.MaximumFanSpeed = smax;
                    if (c.MinimumTemperature >= c.MaximumTemperature) throw new ToolException("min_temperature must be below max_temperature.");
                    if (c.MinimumFanSpeed > c.MaximumFanSpeed) throw new ToolException("min_speed must not exceed max_speed.");
                    if (source is not null) c.SelectedTempSource = source;
                    c.HysteresisConfig = change.ApplyTo(c.HysteresisConfig);
                    linear.UpdateConfig(c);
                    break;
                }
                case IConfigurable<IFlatFanCurveConfig> flat:
                {
                    if (change.Points is not null || change.LinearOnlySet || source is not null || change.HysteresisSet)
                        throw new ToolException("Flat curves only support 'percent'.");
                    var c = new SerializableFlatFanCurve
                    {
                        Name = flat.Config.Name,
                        Percent = change.Percent ?? flat.Config.Percent,
                        CommandMode = flat.Config.CommandMode,
                        IsHidden = flat.Config.IsHidden,
                    };
                    flat.UpdateConfig(c);
                    break;
                }
                default:
                    throw new ToolException($"Editing curves of type {curve.GetType().Name} is not supported (graph, linear and flat are). Edit it in the FanControl UI.");
            }
            return true;
        });

        return FanCurves().OfType<JsonObject>().First(c => string.Equals(c["name"]?.GetValue<string>(), (curve as IFanCurveId)?.Name, StringComparison.Ordinal)).DeepClone().AsObject();
    }

    public JsonObject CreateFanCurve(string name, string type, FanCurveChange initial)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ToolException("A name is required.");
        if (Read(() => _engine.FanCurves.Cast<object>().OfType<IFanCurveId>().Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))))
            throw new ToolException($"A fan curve named '{name}' already exists.");
        var curveType = type.ToLowerInvariant() switch
        {
            "graph" => FanCurveType.Graph,
            "linear" => FanCurveType.Linear,
            "flat" => FanCurveType.Flat,
            _ => throw new ToolException("type must be graph, linear or flat. Mix/sync/trigger curves can be created in the FanControl UI."),
        };
        if (initial.TemperatureSource is not null) ResolveTemperatureId(initial.TemperatureSource); // validate before creating
        initial.Validate();

        OnFanControlThread(() => _engine.AddFanCurve(curveType, name));
        return UpdateFanCurve(name, initial);
    }

    public JsonObject CreateTimeAverageSensor(string name, string sourceRef, int seconds)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ToolException("A name is required.");
        if (seconds is < 1 or > 600) throw new ToolException("seconds must be 1-600.");
        var sourceId = ResolveTemperatureId(sourceRef);
        if (Read(() => _engine.CustomSensors.Cast<object>().Any(s => string.Equals(DisplayName(s), name, StringComparison.OrdinalIgnoreCase))))
            throw new ToolException($"A custom sensor named '{name}' already exists.");

        var created = OnFanControlThread(() =>
        {
            var sensor = _engine.AddCustomSensor(CustomSensorType.TimeAverage, name);
            if (sensor is not IConfigurable<ITimeAverageSensorConfig> avg)
                throw new ToolException("This FanControl version does not allow configuring the time-average sensor; finish it in the UI.");
            var c = new SerializableTimeAverageSensor(avg.Config)
            {
                SelectedTime = seconds,
                SelectedTempSource = new SerializableSensorId { Identifier = sourceId },
            };
            avg.UpdateConfig(c);
            return (object)sensor;
        });

        return new JsonObject
        {
            ["id"] = (created as ISensorId)?.Identifier,
            ["name"] = DisplayName(created),
            ["source"] = sourceId,
            ["seconds"] = seconds,
            ["note"] = "Use this id as temperature_source in update_fan_curve.",
        };
    }

    public JsonObject DeleteFanCurve(string name)
    {
        var curve = FindCurve(name);
        var curveName = ((IFanCurveId)curve).Name;
        var users = Read(() => _engine.Controls.Cast<object>()
            .Where(c => string.Equals((c as IConfigured<IControlConfig>)?.Config.SelectedFanCurve?.Name, curveName, StringComparison.OrdinalIgnoreCase))
            .Select(c => DisplayName(c)).ToList());
        var mixUsers = Read(() => _engine.FanCurves.Cast<object>()
            .Where(c => (c as IConfigured<IFanCurveConfig>)?.Config is IMixFanCurveConfig mix && mix.SelectedFanCurves.Any(m => string.Equals(m.Name, curveName, StringComparison.OrdinalIgnoreCase)))
            .Select(c => ((IFanCurveId)c).Name).ToList());
        if (users.Count > 0 || mixUsers.Count > 0)
            throw new ToolException($"'{curveName}' is still used by: {string.Join(", ", users.Concat(mixUsers))}. Assign another curve first.");
        OnFanControlThread(() => { _engine.RemoveFanCurve((IFanCurveId)curve); return true; });
        return new JsonObject { ["deleted"] = curveName };
    }

    public JsonObject DeleteCustomSensor(string reference)
    {
        var sensors = Read(() => _engine.CustomSensors.Cast<object>().OfType<ISensorSource>().ToList());
        var sensor = sensors.FirstOrDefault(s => string.Equals(s.Identifier, reference, StringComparison.OrdinalIgnoreCase))
                     ?? sensors.FirstOrDefault(s => string.Equals(DisplayName(s), reference, StringComparison.OrdinalIgnoreCase))
                     ?? throw new ToolException($"Custom sensor '{reference}' not found. Only custom sensors (mix, time average, offset, file) can be deleted.");

        // Refuse while anything still reads from it (curves or other custom sensors).
        var users = Read(() => _engine.FanCurves.Cast<object>().Select(c => (Name: (c as IFanCurveId)?.Name, Config: (object?)(c as IConfigured<IFanCurveConfig>)?.Config))
            .Concat(_engine.CustomSensors.Cast<object>().Where(s => !ReferenceEquals(s, sensor)).Select(s => (Name: DisplayName(s), Config: GetProperty(s, "Config"))))
            .Where(x => x.Config is not null && ConfigToJson(x.Config).ToJsonString().Contains($"\"{sensor.Identifier}\"", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Name).ToList());
        if (users.Count > 0) throw new ToolException($"'{DisplayName(sensor)}' is still used by: {string.Join(", ", users)}.");

        OnFanControlThread(() => { _engine.RemoveCustomSensor(sensor); return true; });
        return new JsonObject { ["deleted"] = sensor.Identifier };
    }

    private object FindCurve(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ToolException("A fan curve name is required.");
        var curves = Read(() => _engine.FanCurves.Cast<object>().ToList());
        return curves.FirstOrDefault(c => string.Equals((c as IFanCurveId)?.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? throw new ToolException($"Fan curve '{name}' not found. Available: {string.Join(", ", curves.OfType<IFanCurveId>().Select(c => c.Name))}");
    }

    private string ResolveTemperatureId(string reference)
    {
        var sensors = Read(() => _engine.Temperatures.Cast<object>().Concat(_engine.CustomSensors).OfType<ISensorSource>().ToList());
        var byId = sensors.FirstOrDefault(s => string.Equals(s.Identifier, reference, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId.Identifier;
        var byName = sensors.Where(s => string.Equals(DisplayName(s), reference, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(s.Name, reference, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1) return byName[0].Identifier;
        if (byName.Count > 1) throw new ToolException($"Temperature source '{reference}' is ambiguous, use the id: {string.Join(", ", byName.Select(s => s.Identifier))}");
        throw new ToolException($"Temperature source '{reference}' not found. Use list_temperatures for ids.");
    }

    // ------------------------------------------------------------------ helpers

    private JsonObject UpdateControl(string controlRef, Action<SerializableControlConfig> mutate)
    {
        var control = FindControl(controlRef);
        var configurable = control as IConfigurable<IControlConfig>
                           ?? throw new ToolException("This control cannot be configured.");

        OnFanControlThread(() =>
        {
            var copy = new SerializableControlConfig(configurable.Config);
            mutate(copy);
            configurable.UpdateConfig(copy);
            return true;
        });
        return ControlToJson(control) ?? new JsonObject();
    }

    private object FindControl(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) throw new ToolException("A control id or name is required.");
        var controls = Read(() => _engine.Controls.Cast<object>().ToList());

        object? match = controls.FirstOrDefault(c => string.Equals((c as ISensorId)?.Identifier, reference, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;

        var byName = controls.Where(c =>
            string.Equals(DisplayName(c), reference, StringComparison.OrdinalIgnoreCase) ||
            string.Equals((c as ISensorSource)?.Name, reference, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1) return byName[0];
        if (byName.Count > 1) throw new ToolException($"'{reference}' is ambiguous, use the id. Candidates: {string.Join(", ", byName.Select(c => (c as ISensorId)?.Identifier))}");

        throw new ToolException($"Control '{reference}' not found. Use list_controls to see ids and names.");
    }

    private static string? DisplayName(object sensor)
    {
        var nick = ((sensor as IConfigured<ISensorConfig>)?.Config ?? (sensor as IConfigured<IControlConfig>)?.Config)?.NickName;
        return string.IsNullOrWhiteSpace(nick) ? (sensor as ISensorSource)?.Name : nick;
    }

    private static JsonArray SensorsToJson(IEnumerable<object> sensors, bool includeHidden)
    {
        var arr = new JsonArray();
        foreach (var s in sensors)
        {
            if (s is not ISensorSource src) continue;
            var config = (s as IConfigured<ISensorConfig>)?.Config;
            var hidden = (config as IHidden)?.IsHidden ?? false;
            if (hidden && !includeHidden) continue;
            var o = new JsonObject
            {
                ["id"] = src.Identifier,
                ["name"] = DisplayName(s),
                ["originalName"] = src.Name,
                ["origin"] = src.Origin,
                ["type"] = src.Type.ToString(),
                ["value"] = Round(src.Value),
                ["unit"] = Unit(src.Type),
            };
            if (s is FanControl.Domain.CustomSensors.BaseCustomSensor)
            {
                o["customSensorType"] = s.GetType().Name.Replace("Sensor", "");
                var customConfig = GetProperty(s, "Config");
                if (customConfig is not null) o["config"] = ConfigToJson(customConfig);
            }
            if (hidden) o["hidden"] = true;
            arr.Add(o);
        }
        return arr;
    }

    private static JsonObject? ControlToJson(object c)
    {
        if (c is not ISensorSource src) return null;
        var config = (c as IConfigured<IControlConfig>)?.Config;
        var o = new JsonObject
        {
            ["id"] = src.Identifier,
            ["name"] = DisplayName(c),
            ["originalName"] = src.Name,
            ["origin"] = src.Origin,
            ["currentPercent"] = Round(src.Value),
        };
        if (c is IControl ctrl)
        {
            o["targetCommand"] = Round(ctrl.CurrentCommand);
            o["externalOverride"] = ctrl.ExternalSourceOverride;
            o["avoidMode"] = ctrl.InAvoidMode;
        }
        if (config is not null)
        {
            o["enabled"] = config.Enable;
            o["manualControl"] = config.ManualControl;
            o["manualPercent"] = config.ManualControlValue;
            o["fanCurve"] = config.SelectedFanCurve?.Name;
            o["pairedFanSensorId"] = config.PairedFanSensor?.Identifier;
            o["offset"] = config.SelectedOffset;
            o["minimumPercent"] = config.MinimumPercent;
            o["stepUp"] = config.SelectedCommandStepUp;
            o["stepDown"] = config.SelectedCommandStepDown;
            o["start"] = config.SelectedStart;
            o["stop"] = config.SelectedStop;
            if (config.IsHidden) o["hidden"] = true;
        }
        return o;
    }

    /// <summary>Generic dump of public scalar config properties (fan curve points, sources, hysteresis...).</summary>
    private static JsonObject ConfigToJson(object config)
    {
        var o = new JsonObject();
        foreach (var p in config.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (p.GetIndexParameters().Length > 0) continue;
            object? value;
            try { value = p.GetValue(config); } catch { continue; }
            o[ToCamel(p.Name)] = ToNode(value, 0);
        }
        return o;
    }

    private static JsonNode? ToNode(object? value, int depth)
    {
        switch (value)
        {
            case null: return null;
            case string s: return s;
            case bool b: return b;
            case float f: return Math.Round(f, 2);
            case double d: return Math.Round(d, 2);
            case int or long or short or byte: return JsonValue.Create(Convert.ToInt64(value));
            case Enum e: return e.ToString();
            case ISensorId sid: return sid.Identifier;
            case IFanCurveId fid: return fid.Name;
        }
        if (depth > 2) return value.ToString();
        if (value is System.Collections.IEnumerable list)
        {
            var arr = new JsonArray();
            foreach (var item in list) arr.Add(ToNode(item, depth + 1));
            return arr;
        }
        var t = value.GetType();
        if (t.IsValueType || t.Namespace?.StartsWith("FanControl", StringComparison.Ordinal) == true)
        {
            var o = new JsonObject();
            foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                try { o[ToCamel(p.Name)] = ToNode(p.GetValue(value), depth + 1); } catch { }
            }
            return o;
        }
        return value.ToString();
    }

    private static string ToCamel(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    private static string? Unit(BackendSensorType type) => type switch
    {
        BackendSensorType.Temperature => "°C",
        BackendSensorType.RPM => "RPM",
        BackendSensorType.Control or BackendSensorType.UsagePercent => "%",
        BackendSensorType.Frequency => "MHz",
        BackendSensorType.Voltage => "V",
        BackendSensorType.Power => "W",
        _ => null,
    };

    private static JsonNode? Round(float? v) => v is null || float.IsNaN(v.Value) ? null : JsonValue.Create(Math.Round(v.Value, 1));

    /// <summary>Reads engine collections while the engine is not mid-update.</summary>
    private T Read<T>(Func<T> read)
    {
        if (_engineLock is null) return read();
        var taken = false;
        try
        {
            Monitor.TryEnter(_engineLock, TimeSpan.FromSeconds(2), ref taken);
            return read();
        }
        finally
        {
            if (taken) Monitor.Exit(_engineLock);
        }
    }

    /// <summary>
    /// Runs a mutation on the thread FanControl itself uses for it: the WPF dispatcher in desktop mode
    /// (so the UI stays consistent), otherwise under the engine lock.
    /// </summary>
    private T OnFanControlThread<T>(Func<T> action)
    {
        var app = EngineLocator.GetWpfApplication();
        if (app is not null)
        {
            var dispatcher = app.GetType().GetProperty("Dispatcher")?.GetValue(app);
            var invoke = dispatcher?.GetType().GetMethod("Invoke", [typeof(Func<T>)])
                         ?? dispatcher?.GetType().GetMethods().FirstOrDefault(m => m.Name == "Invoke" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1)?.MakeGenericMethod(typeof(T));
            if (invoke is not null)
            {
                try { return (T)invoke.Invoke(dispatcher, [action])!; }
                catch (TargetInvocationException e) when (e.InnerException is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                    throw;
                }
            }
        }
        return Read(action);
    }

    private static T? GetField<T>(object target, string name) where T : class
    {
        for (var t = target.GetType(); t is not null; t = t.BaseType)
        {
            var f = t.GetField(name, AnyInstance | BindingFlags.DeclaredOnly);
            if (f is not null) return f.GetValue(target) as T;
        }
        return null;
    }

    private static object? GetProperty(object target, string name)
    {
        try { return target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(target); }
        catch { return null; }
    }
}

