using System.Text.Json;
using System.Text.Json.Nodes;

namespace FanControl.MCP.Mcp;

/// <summary>A tool exposed over MCP.</summary>
internal sealed record McpTool(
    string Name,
    string Title,
    string Description,
    JsonObject InputSchema,
    bool ReadOnly,
    Func<JsonObject, JsonNode?> Handler,
    bool Destructive = false,
    bool Idempotent = true,
    JsonObject? Meta = null);

/// <summary>A static, readable document exposed over MCP (e.g. the tuning guide or an MCP Apps UI).</summary>
internal sealed record McpResource(string Uri, string Name, string Title, string Description, string MimeType, Func<string> Read, JsonObject? Meta = null);

/// <summary>What a connected client told us in initialize (per MCP-Session-Id).</summary>
internal sealed class ClientSession(string id, JsonObject capabilities, string? clientName)
{
    public string Id { get; } = id;
    public JsonObject Capabilities { get; } = capabilities;
    public string? ClientName { get; } = clientName;
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    /// <summary>Form-mode elicitation: "elicitation": {} (form implied) or {"form": {...}}.</summary>
    public bool SupportsForms => Capabilities["elicitation"] is JsonObject e && (e.Count == 0 || e.ContainsKey("form"));

    /// <summary>MCP Apps (interactive HTML views).</summary>
    public bool SupportsApps => Capabilities["extensions"]?["io.modelcontextprotocol/ui"] is not null;
}

/// <summary>
/// Per-request context for tool handlers: the client session and a channel to send server-to-client requests
/// (elicitation) on the open response stream of the current tools/call.
/// </summary>
internal sealed class McpCallContext
{
    private static readonly AsyncLocal<McpCallContext?> CurrentContext = new();

    public static McpCallContext? Current
    {
        get => CurrentContext.Value;
        set => CurrentContext.Value = value;
    }

    public ClientSession? Session { get; init; }

    /// <summary>Writes one JSON-RPC message to the client on the current response stream (switches it to SSE).</summary>
    public Action<string>? SendToClient { get; init; }

    internal McpProtocol? Protocol { get; set; }

    /// <summary>
    /// Asks the user for structured input (MCP elicitation, form mode). Returns the client's result
    /// ({action, content}) or null if the client cannot show forms.
    /// </summary>
    public JsonObject? Elicit(string message, JsonObject requestedSchema, TimeSpan timeout)
    {
        if (Session?.SupportsForms != true || SendToClient is null || Protocol is null) return null;
        return Protocol.RequestFromClient(this, "elicitation/create", new JsonObject
        {
            ["mode"] = "form",
            ["message"] = message,
            ["requestedSchema"] = requestedSchema,
        }, timeout);
    }
}

/// <summary>Argument of a prompt template.</summary>
internal sealed record McpPromptArgument(string Name, string Description, bool Required = false);

/// <summary>
/// A prompt template (shown by clients as slash command). <see cref="Build"/> returns the user message text;
/// <see cref="EmbeddedResources"/> are attached to the message as embedded resources.
/// </summary>
internal sealed record McpPrompt(
    string Name,
    string Title,
    string Description,
    IReadOnlyList<McpPromptArgument> Arguments,
    Func<IReadOnlyDictionary<string, string>, string> Build,
    IReadOnlyList<string> EmbeddedResources);

/// <summary>Error raised by a tool; reported to the model as a tool result with isError=true.</summary>
internal sealed class ToolException(string message) : Exception(message);

/// <summary>
/// JSON-RPC 2.0 / MCP message handling (lifecycle, ping, tools, resources, prompts). Transport independent.
/// </summary>
internal sealed class McpProtocol
{
    public const string LatestProtocolVersion = "2025-06-18";
    private static readonly string[] SupportedProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Dictionary<string, McpTool> _tools;
    private readonly string _serverName;
    private readonly string _serverVersion;
    private readonly string _instructions;
    private readonly Action<string> _log;
    private readonly Dictionary<string, McpResource> _resources;
    private readonly Dictionary<string, McpPrompt> _prompts;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ClientSession> _sessions = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pendingClientRequests = new();
    private long _serverRequestId;

    public bool KnowsSession(string id) => _sessions.TryGetValue(id, out var s) && Touch(s);

    public ClientSession? GetSession(string? id) => id is not null && _sessions.TryGetValue(id, out var s) ? s : null;

    public void EndSession(string id) => _sessions.TryRemove(id, out _);

    private static bool Touch(ClientSession s) { s.LastSeen = DateTime.UtcNow; return true; }

    /// <summary>Sends a request to the client on the current stream and blocks until its response arrives (separate POST).</summary>
    internal JsonObject RequestFromClient(McpCallContext ctx, string method, JsonObject @params, TimeSpan timeout)
    {
        var id = $"srv-{Interlocked.Increment(ref _serverRequestId)}";
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingClientRequests[id] = tcs;
        try
        {
            ctx.SendToClient!(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params }.ToJsonString(Compact));
            if (!tcs.Task.Wait(timeout)) throw new ToolException("The user did not answer the form in time.");
            var response = tcs.Task.Result;
            if (response["error"] is JsonObject err) throw new ToolException($"Client rejected {method}: {err["message"]}");
            return response["result"] as JsonObject ?? new JsonObject();
        }
        finally
        {
            _pendingClientRequests.TryRemove(id, out _);
        }
    }

    public McpProtocol(IEnumerable<McpTool> tools, string serverName, string serverVersion, string instructions, Action<string> log,
        IEnumerable<McpResource>? resources = null, IEnumerable<McpPrompt>? prompts = null)
    {
        _resources = (resources ?? []).ToDictionary(r => r.Uri, StringComparer.Ordinal);
        _prompts = (prompts ?? []).ToDictionary(p => p.Name, StringComparer.Ordinal);
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        _serverName = serverName;
        _serverVersion = serverVersion;
        _instructions = instructions;
        _log = log;
    }

    public IReadOnlyCollection<McpTool> Tools => _tools.Values;

    /// <summary>
    /// Handles one HTTP body (single message or batch). Returns null when nothing has to be sent back
    /// (only notifications/responses were received).
    /// </summary>
    public string? HandleRaw(string body, out string? negotiatedVersion) => HandleRaw(body, null, out negotiatedVersion, out _);

    /// <summary>Handles one HTTP body within a call context; <paramref name="newSessionId"/> is set when initialize created a session.</summary>
    public string? HandleRaw(string body, McpCallContext? context, out string? negotiatedVersion, out string? newSessionId)
    {
        _newSessionId = null;
        if (context is not null) context.Protocol = this;
        McpCallContext.Current = context;
        try
        {
            return HandleRawCore(body, out negotiatedVersion);
        }
        finally
        {
            newSessionId = _newSessionId;
            McpCallContext.Current = null;
        }
    }

    [ThreadStatic] private static string? _newSessionId;

    private string? HandleRawCore(string body, out string? negotiatedVersion)
    {
        negotiatedVersion = null;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException e)
        {
            return Serialize(Error(null, -32700, "Parse error: " + e.Message));
        }

        if (root is JsonArray batch)
        {
            if (batch.Count == 0) return Serialize(Error(null, -32600, "Invalid Request: empty batch"));
            var replies = new JsonArray();
            foreach (var item in batch)
            {
                var reply = HandleMessage(item, ref negotiatedVersion);
                if (reply is not null) replies.Add(reply);
            }
            return replies.Count == 0 ? null : Serialize(replies);
        }

        var single = HandleMessage(root, ref negotiatedVersion);
        return single is null ? null : Serialize(single);
    }

    private JsonObject? HandleMessage(JsonNode? node, ref string? negotiatedVersion)
    {
        if (node is not JsonObject msg || msg["jsonrpc"]?.GetValue<string>() != "2.0")
            return Error(null, -32600, "Invalid Request");

        var id = msg["id"]?.DeepClone();
        var method = msg["method"]?.GetValue<string>();

        if (method is null)
        {
            // A response to one of our server-to-client requests (elicitation).
            if (id is JsonValue v && v.TryGetValue<string>(out var rid) && _pendingClientRequests.TryGetValue(rid, out var tcs))
                tcs.TrySetResult(msg);
            return null;
        }

        var isNotification = !msg.ContainsKey("id");
        var @params = msg["params"] as JsonObject ?? new JsonObject();

        try
        {
            JsonNode? result = method switch
            {
                "initialize" => Initialize(@params, out negotiatedVersion),
                "ping" => new JsonObject(),
                "tools/list" => ListTools(),
                "tools/call" => CallTool(@params),
                "resources/list" => ListResources(),
                "resources/read" => ReadResource(@params),
                "resources/templates/list" => new JsonObject { ["resourceTemplates"] = new JsonArray() },
                "prompts/list" => ListPrompts(),
                "prompts/get" => GetPrompt(@params),
                "logging/setLevel" => new JsonObject(),
                _ when method.StartsWith("notifications/", StringComparison.Ordinal) => null,
                _ => throw new RpcException(-32601, $"Method not found: {method}"),
            };

            if (isNotification) return null;
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? new JsonObject() };
        }
        catch (RpcException e)
        {
            return isNotification ? null : Error(id, e.Code, e.Message);
        }
        catch (Exception e)
        {
            _log($"MCP request '{method}' failed: {e}");
            return isNotification ? null : Error(id, -32603, "Internal error: " + e.Message);
        }
    }

    private JsonObject Initialize(JsonObject @params, out string? negotiatedVersion)
    {
        var requested = @params["protocolVersion"]?.GetValue<string>();
        negotiatedVersion = requested is not null && SupportedProtocolVersions.Contains(requested) ? requested : LatestProtocolVersion;

        var sessionId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var caps = @params["capabilities"] as JsonObject ?? new JsonObject();
        _sessions[sessionId] = new ClientSession(sessionId, caps.DeepClone().AsObject(), @params["clientInfo"]?["name"]?.ToString());
        foreach (var stale in _sessions.Values.Where(s => DateTime.UtcNow - s.LastSeen > TimeSpan.FromDays(2)).ToList()) _sessions.TryRemove(stale.Id, out _);
        _newSessionId = sessionId;
        return new JsonObject
        {
            ["protocolVersion"] = negotiatedVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
                ["resources"] = new JsonObject { ["listChanged"] = false, ["subscribe"] = false },
                ["prompts"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = _serverName,
                ["title"] = "FanControl",
                ["version"] = _serverVersion,
            },
            ["instructions"] = _instructions,
        };
    }

    private JsonObject ListTools()
    {
        var list = new JsonArray();
        foreach (var tool in _tools.Values)
        {
            var entry = new JsonObject
            {
                ["name"] = tool.Name,
                ["title"] = tool.Title,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
                ["annotations"] = new JsonObject
                {
                    ["title"] = tool.Title,
                    ["readOnlyHint"] = tool.ReadOnly,
                    ["destructiveHint"] = tool.Destructive,
                    ["idempotentHint"] = tool.Idempotent,
                    ["openWorldHint"] = false,
                },
            };
            if (tool.Meta is not null) entry["_meta"] = tool.Meta.DeepClone();
            list.Add(entry);
        }
        return new JsonObject { ["tools"] = list };
    }

    private JsonObject ListResources()
    {
        var list = new JsonArray();
        foreach (var r in _resources.Values)
        {
            list.Add(new JsonObject
            {
                ["uri"] = r.Uri,
                ["name"] = r.Name,
                ["title"] = r.Title,
                ["description"] = r.Description,
                ["mimeType"] = r.MimeType,
            });
        }
        return new JsonObject { ["resources"] = list };
    }

    private JsonObject ReadResource(JsonObject @params)
    {
        var uri = @params["uri"]?.GetValue<string>() ?? throw new RpcException(-32602, "Missing uri");
        if (!_resources.TryGetValue(uri, out var r)) throw new RpcException(-32002, $"Resource not found: {uri}");
        return new JsonObject { ["contents"] = new JsonArray(ResourceContent(r)) };
    }

    private static JsonObject ResourceContent(McpResource r)
    {
        var o = new JsonObject { ["uri"] = r.Uri, ["mimeType"] = r.MimeType, ["text"] = r.Read() };
        if (r.Meta is not null) o["_meta"] = r.Meta.DeepClone();
        return o;
    }

    private JsonObject ListPrompts()
    {
        var list = new JsonArray();
        foreach (var p in _prompts.Values)
        {
            var args = new JsonArray();
            foreach (var a in p.Arguments)
                args.Add(new JsonObject { ["name"] = a.Name, ["description"] = a.Description, ["required"] = a.Required });
            list.Add(new JsonObject
            {
                ["name"] = p.Name,
                ["title"] = p.Title,
                ["description"] = p.Description,
                ["arguments"] = args,
            });
        }
        return new JsonObject { ["prompts"] = list };
    }

    private JsonObject GetPrompt(JsonObject @params)
    {
        var name = @params["name"]?.GetValue<string>() ?? throw new RpcException(-32602, "Missing prompt name");
        if (!_prompts.TryGetValue(name, out var p)) throw new RpcException(-32602, $"Unknown prompt: {name}");

        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        if (@params["arguments"] is JsonObject given)
            foreach (var (k, v) in given)
                if (v is JsonValue jv && jv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) args[k] = s.Trim();
        foreach (var a in p.Arguments.Where(a => a.Required && !args.ContainsKey(a.Name)))
            throw new RpcException(-32602, $"Missing required argument: {a.Name}");

        var messages = new JsonArray();
        foreach (var uri in p.EmbeddedResources)
        {
            if (!_resources.TryGetValue(uri, out var r)) continue;
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonObject { ["type"] = "resource", ["resource"] = ResourceContent(r) },
            });
        }
        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonObject { ["type"] = "text", ["text"] = p.Build(args) },
        });
        return new JsonObject { ["description"] = p.Description, ["messages"] = messages };
    }

    private JsonObject CallTool(JsonObject @params)
    {
        var name = @params["name"]?.GetValue<string>() ?? throw new RpcException(-32602, "Missing tool name");
        if (!_tools.TryGetValue(name, out var tool)) throw new RpcException(-32602, $"Unknown tool: {name}");
        var args = @params["arguments"] as JsonObject ?? new JsonObject();

        try
        {
            var result = tool.Handler(args);
            var text = result switch
            {
                null => "OK",
                JsonValue v when v.TryGetValue<string>(out var s) => s, // plain text (e.g. markdown guide)
                _ => result.ToJsonString(Pretty),
            };
            var reply = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["isError"] = false,
            };
            if (result is JsonObject obj) reply["structuredContent"] = obj.DeepClone();
            return reply;
        }
        catch (Exception e) when (e is ToolException or ArgumentException or InvalidOperationException)
        {
            return ToolError(e.Message);
        }
        catch (Exception e)
        {
            _log($"Tool '{name}' failed: {e}");
            return ToolError($"{e.GetType().Name}: {e.Message}");
        }
    }

    private static JsonObject ToolError(string message) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
        ["isError"] = true,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static string Serialize(JsonNode node) => node.ToJsonString(Compact);

    private sealed class RpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
