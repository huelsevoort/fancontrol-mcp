# MCP Elicitation and MCP Apps: wire-level reference for a hand-rolled C# server

Researched 2026-11-05 against primary sources: the MCP spec, the ext-apps spec and SDK source, Claude docs, OpenAI docs, VS Code and Cursor docs. Each claim links its source in [Sources](#sources). Anything marked **UNVERIFIED** could not be confirmed from a primary source in this session.

---

## 0. Read this first: there are now two protocol eras

The current MCP spec is **2026-07-28**. The docs site labels it "Version 2026-07-28 (latest)" [S1]. That revision is a breaking redesign [S2]:

| | **Legacy** (2025-11-25 and earlier) | **Modern** (2026-07-28) |
|---|---|---|
| Handshake | `initialize`, then `notifications/initialized` | none. Every request carries `_meta["io.modelcontextprotocol/protocolVersion"]` and `_meta["io.modelcontextprotocol/clientCapabilities"]`. Servers MUST implement `server/discover` |
| Session | Optional `MCP-Session-Id` header, issued with the InitializeResult | **Removed**. Ignore the header and never mint one |
| How the server asks for input | The server sends a JSON-RPC **request** (`elicitation/create`) on the POST's SSE stream. The client POSTs the JSON-RPC response back, and the server answers **202** | **MRTR**: the server *returns* `{"resultType":"input_required","inputRequests":{...},"requestState":"..."}`. The client **retries** the original `tools/call` with a new id plus `inputResponses` and `requestState`. Servers MUST NOT send requests on SSE |
| GET stream, resumability | GET opens an SSE stream. `Last-Event-ID` resumes a stream | Removed. GET or DELETE returns 405. Use `subscriptions/listen` for change notifications |
| Required headers | `MCP-Protocol-Version` (after init), `MCP-Session-Id` (if issued) | `MCP-Protocol-Version`, `Mcp-Method`, and `Mcp-Name` (the last on tools/call, resources/read, prompts/get). A mismatch returns 400 with JSON-RPC error `-32020` |
| `resultType` on results | absent | required: `"complete"` or `"input_required"` |
| `extensions` capability (needed for MCP Apps) | not in the 2025-11-25 schema, but hosts send `capabilities.extensions` anyway (ext-apps spec example) | formal `extensions` field in ClientCapabilities and ServerCapabilities |

**What clients do in practice:** Claude Code's "v2 runtime" (TS SDK 2.0) asks HTTP and stdio servers whether they support 2026-07-28. It uses that revision with servers that do and falls back to the legacy `initialize` flow with the rest [S20]. A dual-era client probes HTTP with a modern request first. On `400`, it falls back to `initialize` *unless* the body is a recognized modern JSON-RPC error [S4][S5].

**Recommendation for fancontrol:** implement **legacy 2025-11-25 first**, because every client can still speak it. Make the legacy path fall back cleanly: answer any non-`initialize` request that lacks `MCP-Session-Id` with **HTTP 400** and an empty or non-modern body. Add the modern path later as a dual-era server. The spec allows serving both eras on one endpoint, selected by how the client opens: `initialize` means legacy, per-request `_meta` means modern [S5].

---

## 1. Elicitation

### 1.1 Client capability declaration

**Legacy (2025-11-25):** the client sends this in `initialize` `params.capabilities` [S6]:

```json
{ "capabilities": { "elicitation": { "form": {}, "url": {} } } }
```

An empty object `"elicitation": {}` means **form mode only** (backwards compatibility). A client that declares `elicitation` MUST support at least one mode. A server **MUST NOT** send a mode the client did not declare. A client returns `-32602` if a server does [S6].

**Modern (2026-07-28):** the same object goes on **every request**, under `params._meta["io.modelcontextprotocol/clientCapabilities"].elicitation` [S7].

### 1.2 `elicitation/create` params

| Field | Mode | Notes |
|---|---|---|
| `mode` | both | `"form"` or `"url"`. It is optional for form mode, and clients MUST treat a missing value as form |
| `message` | both | human-readable reason |
| `requestedSchema` | form | flat object schema (see 1.3) |
| `url` | url | must be a valid URL |
| `elicitationId` | url, **legacy only** | removed in 2026-07-28, together with `notifications/elicitation/complete` [S2] |

Form mode is forbidden for secrets: servers MUST NOT request passwords, API keys, tokens or payment credentials through form mode, and must use URL mode for those [S6].

### 1.3 Allowed `requestedSchema` subset

```ts
requestedSchema: { $schema?: string; type: "object";
                   properties: { [k: string]: PrimitiveSchemaDefinition };
                   required?: string[] }
```

The `properties` are restricted to flat primitives. Nested objects and arrays of objects are not allowed [S6][S8]:

```jsonc
// string (formats: email, uri, date, date-time)
{ "type": "string", "title": "Display Name", "description": "…", "minLength": 3, "maxLength": 50,
  "pattern": "^[A-Za-z]+$", "format": "email", "default": "user@example.com" }
// number / integer
{ "type": "integer", "title": "Min RPM", "description": "…", "minimum": 0, "maximum": 3000, "default": 600 }
// boolean
{ "type": "boolean", "title": "Start with Windows", "default": false }
// single-select, untitled
{ "type": "string", "title": "Mode", "enum": ["Silent", "Balanced", "Performance"], "default": "Balanced" }
// single-select, titled (preferred over enumNames)
{ "type": "string", "title": "Mode",
  "oneOf": [ { "const": "silent", "title": "Silent" }, { "const": "balanced", "title": "Balanced" } ],
  "default": "balanced" }
// multi-select, untitled
{ "type": "array", "title": "Fans", "minItems": 1, "maxItems": 4,
  "items": { "type": "string", "enum": ["CPU", "Case1", "Case2"] }, "default": ["CPU"] }
// multi-select, titled
{ "type": "array", "title": "Fans", "items": { "anyOf": [ { "const": "fan0", "title": "CPU" }, { "const": "fan1", "title": "Front" } ] } }
// LEGACY titled enum: deprecated, scheduled for removal
{ "type": "string", "enum": ["silent","balanced"], "enumNames": ["Silent","Balanced"] }
```

The `pattern` field appears on the 2025-11-25 elicitation page but **not** in the 2026-07-28 `StringSchema` type (fetched schema excerpt) [S6][S8]. Treat it as optional and validate on the server. Clients SHOULD pre-fill `default` values.

### 1.4 Result (`ElicitResult`)

```json
{ "action": "accept", "content": { "mode": "balanced", "minRpm": 600 } }
```

`action` is one of three values [S6][S8]:

- `"accept"`: `content` is present for form mode and omitted for URL mode. For URL mode, `accept` only means the user consented to open the URL.
- `"decline"`: the user explicitly said no.
- `"cancel"`: the user dismissed the dialog without choosing.

Content values are typed `string | number | boolean | string[]`. Servers MUST handle decline and cancel, and SHOULD re-validate the data against the schema.

### 1.5 Legacy flow: elicitation *during* `tools/call` over Streamable HTTP (2025-11-25)

Rules from [S9]:

- The client POSTs every message with `Accept: application/json, text/event-stream`.
- For a **request**, the server returns either `Content-Type: application/json` (one object) or `Content-Type: text/event-stream`, and the client MUST support both.
- On that SSE stream, the server "MAY send JSON-RPC *requests* and *notifications* before sending the JSON-RPC *response*. These messages **SHOULD** relate to the originating client *request*." The stream SHOULD eventually include the response, after which the server SHOULD terminate the stream.
- If the POST body is a JSON-RPC **response** or notification, the server MUST return **202 Accepted with no body**, or an HTTP error such as 400.
- A disconnect SHOULD NOT be read as cancellation. The client cancels explicitly with `notifications/cancelled`.
- There is **no "related-request" header**. The association is implicit: the request is sent on the SSE stream of the POST it relates to. Request IDs MUST be unique per sender within the session, and so must SSE event IDs if you use them [S9]. Use your own namespace, for example `"srv-1"`.

**Session rules** [S9]:

- The server MAY issue `MCP-Session-Id` on the HTTP response that carries the `InitializeResult`. The value must be visible ASCII 0x21-0x7E and SHOULD be cryptographically random.
- If one is issued, clients MUST send it on **all** subsequent requests, including the POST that carries the elicitation response.
- Servers that require it SHOULD answer requests that lack it with 400. An unknown or expired session gets **404**, and the client then re-initializes.
- A DELETE with the header ends the session. The server may answer 405.
- Clients MUST also send `MCP-Protocol-Version: <negotiated>` on every request after init. An unsupported value gets 400.

Wire trace:

```http
POST /mcp HTTP/1.1
Content-Type: application/json
Accept: application/json, text/event-stream

{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25",
 "capabilities":{"elicitation":{"form":{},"url":{}},
   "extensions":{"io.modelcontextprotocol/ui":{"mimeTypes":["text/html;profile=mcp-app"]}}},
 "clientInfo":{"name":"some-client","version":"1.0"}}}
```

```http
HTTP/1.1 200 OK
Content-Type: application/json
MCP-Session-Id: 9f2c0d7e5b6a4f1e8c3d2b1a0f9e8d7c

{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-11-25",
 "capabilities":{"tools":{},"resources":{}},"serverInfo":{"name":"fancontrol","version":"0.1.0"}}}
```

The client then POSTs `notifications/initialized` with the session header, and the server answers `202`. Next, the tool call:

```http
POST /mcp HTTP/1.1
Content-Type: application/json
Accept: application/json, text/event-stream
MCP-Session-Id: 9f2c0d7e5b6a4f1e8c3d2b1a0f9e8d7c
MCP-Protocol-Version: 2025-11-25

{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"setup_wizard","arguments":{}}}
```

The server opens SSE on *this* response and keeps the connection open:

```http
HTTP/1.1 200 OK
Content-Type: text/event-stream
Cache-Control: no-cache
X-Accel-Buffering: no

event: message
data: {"jsonrpc":"2.0","id":"srv-1","method":"elicitation/create","params":{"mode":"form","message":"Choose a fan profile","requestedSchema":{"type":"object","properties":{"profile":{"type":"string","title":"Profile","oneOf":[{"const":"silent","title":"Silent"},{"const":"balanced","title":"Balanced"}],"default":"balanced"},"minRpm":{"type":"integer","title":"Minimum RPM","minimum":0,"maximum":3000,"default":600}},"required":["profile"]}}}

```

The client answers the elicitation with a separate POST:

```http
POST /mcp HTTP/1.1
Content-Type: application/json
Accept: application/json, text/event-stream
MCP-Session-Id: 9f2c0d7e5b6a4f1e8c3d2b1a0f9e8d7c
MCP-Protocol-Version: 2025-11-25

{"jsonrpc":"2.0","id":"srv-1","result":{"action":"accept","content":{"profile":"silent","minRpm":500}}}
```

```http
HTTP/1.1 202 Accepted
```

The server finishes the tool and writes the final response on the **original** SSE stream, then closes it:

```
event: message
data: {"jsonrpc":"2.0","id":7,"result":{"content":[{"type":"text","text":"Applied profile 'silent' (min 500 RPM)."}],"isError":false}}

```

SSE framing follows the WHATWG standard: `field: value` lines, and a blank line ends an event. `event:` defaults to `message`, so `event: message` is optional but harmless. Put the JSON on a single `data:` line. `id:` is optional. Add it only if you implement resumability. The spec says the server SHOULD first send a *priming* event, an `id:` with empty `data:`, so the client can reconnect. If you don't support `Last-Event-ID` resumption, don't hand out IDs that you cannot resume [S9].

C# implementation notes (my inference, not spec text):

- Disable response buffering and call `FlushAsync()` after every event.
- Park the tool handler on a `TaskCompletionSource` keyed by `(sessionId, "srv-1")`, and complete it from the POST handler that receives the JSON-RPC response.
- Time out and cancel if the user never answers.
- **Client request timeouts (UNVERIFIED per client):** a client may time out a `tools/call` that sits waiting on a human. Claude Code documents that a call waiting on an open elicitation dialog is not backgrounded [S20]. Sending `notifications/progress`, when the client supplied `_meta.progressToken`, may help with clients that reset their timeout on progress.

**URL mode and the `-32042` error (legacy only)** [S6]:

- URL-mode requests add `elicitationId`.
- When the out-of-band flow finishes, the server MAY send `{"jsonrpc":"2.0","method":"notifications/elicitation/complete","params":{"elicitationId":"…"}}`.
- Alternatively, a request can fail with `URLElicitationRequiredError`, `code: -32042`, with `data.elicitations: [ {mode:"url", elicitationId, url, message} ]`. The client opens the URL and retries.

### 1.6 Modern flow: MRTR (2026-07-28)

From [S3][S10]:

- `InputRequiredResult` is allowed only on `tools/call`, `resources/read` and `prompts/get`.
- `inputRequests` keys are server-chosen and must be unique.
- At least one of `inputRequests` and `requestState` must be present.
- The server must not include a request type the client didn't declare.
- `requestState` is attacker-controlled. HMAC or AEAD it if it affects logic. Include the principal, a TTL and the request identity.
- The retry MUST use a **new JSON-RPC id** and echo `requestState` exactly.

```http
POST /mcp HTTP/1.1
Content-Type: application/json
Accept: application/json, text/event-stream
MCP-Protocol-Version: 2026-07-28
Mcp-Method: tools/call
Mcp-Name: setup_wizard

{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"setup_wizard","arguments":{},
 "_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28",
          "io.modelcontextprotocol/clientInfo":{"name":"ExampleClient","version":"1.0.0"},
          "io.modelcontextprotocol/clientCapabilities":{"elicitation":{"form":{}}}}}}
```

```json
{"jsonrpc":"2.0","id":1,"result":{"resultType":"input_required",
 "inputRequests":{"profile":{"method":"elicitation/create","params":{"mode":"form","message":"Choose a fan profile",
   "requestedSchema":{"type":"object","properties":{"profile":{"type":"string","enum":["silent","balanced"]}},"required":["profile"]}}}},
 "requestState":"<HMAC-protected blob>"}}
```

```json
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"setup_wizard","arguments":{},
 "inputResponses":{"profile":{"action":"accept","content":{"profile":"silent"}}},
 "requestState":"<HMAC-protected blob>",
 "_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{"elicitation":{"form":{}}}}}}
```

```json
{"jsonrpc":"2.0","id":2,"result":{"resultType":"complete","content":[{"type":"text","text":"Applied profile 'silent'."}]}}
```

`inputResponses` and `requestState` are fields of `InputResponseRequestParams`, which `CallToolRequestParams` extends [S10]. Other modern requirements [S2][S4][S7]:

- Implement `server/discover`, which returns `supportedVersions`, `capabilities` and `instructions`.
- Results of `tools/list`, `resources/list`, `resources/read` and `prompts/list` need `ttlMs` and `cacheScope`.
- An unsupported version gets HTTP 400 with `-32022 {"supported":[…],"requested":"…"}`.
- A header and body mismatch gets 400 with `-32020`.
- An unknown method gets **404** with `-32601`.
- `resultType` is required on all results.
- Closing the SSE stream counts as cancellation.

### 1.7 Which clients support elicitation (as of 2026-11-05)

| Client | Elicitation | Modes | Source |
|---|---|---|---|
| **Claude Code** (CLI, and the Desktop "Code" tab / claude.ai/code cloud sessions) | Yes. Shows a terminal dialog, or a card in cloud sessions | Declares `{form:{},url:{}}` on 2026-07-28 connections, and on legacy connections to the user's own stdio, sse, http and ws servers (v2.1.287 or later). Declares bare `{}` (form only) on other legacy connections, such as claude.ai connectors. Not served in non-interactive `-p` sessions. An `Elicitation` hook can auto-answer | [S20] |
| **Claude Desktop / claude.ai chat** | **UNVERIFIED, probably no.** No Anthropic doc found. Two vendor docs report that claude.ai and Cowork do not surface elicitation | | [S27][S28] |
| **VS Code (GitHub Copilot)** | Yes. "Elicitation: request input from the user" | Form vs URL is not documented. **UNVERIFIED** | [S21] |
| **Cursor** | Yes. Listed as a supported capability | Form vs URL not documented | [S22] |
| **Codex CLI** | Yes, but gated: `approval_policy = { granular = { mcp_elicitations = true } }` lets prompts surface "instead of being auto-rejected" | modes not documented | [S23] |
| **ChatGPT** | **UNVERIFIED.** The Apps SDK docs tell server authors to "Use MCP elicitation when the server needs structured information", but I found no explicit host-support statement | | [S24] |

Implication: always have a non-elicitation fallback. If the client did not declare `elicitation`, return a tool error or text that asks the model to collect the values as tool arguments, or point to the MCP App UI.

---

## 2. MCP Apps (SEP-1865, `io.modelcontextprotocol/ui`)

Status: the spec is **Stable 2026-01-26** (`specification/2026-01-26/apps.mdx`), with a `draft` with additions alongside. The SDK `@modelcontextprotocol/ext-apps` is at v2.0.4, released 2026-10-27 [S11][S12][S13].

### 2.1 Capability negotiation

The host advertises the extension. Legacy hosts put it in `initialize` `params.capabilities.extensions`. Modern hosts put it in every request's `_meta["io.modelcontextprotocol/clientCapabilities"].extensions` [S11][S4]:

```json
{ "capabilities": { "extensions": { "io.modelcontextprotocol/ui": { "mimeTypes": ["text/html;profile=mcp-app"] } } } }
```

`mimeTypes` is REQUIRED. The SDK helper `getUiCapability()` reads only `clientCapabilities.extensions["io.modelcontextprotocol/ui"]` [S14]. Servers SHOULD check this before registering UI tools, MUST still return meaningful `content`, and MAY register different tool variants per host [S11].

### 2.2 UI resource

- `uri` MUST use `ui://`.
- `mimeType` MUST be exactly `text/html;profile=mcp-app`.
- The content goes in `text` (HTML string) or `blob` (base64), and MUST be a valid HTML5 document.
- Servers MAY omit UI-only resources from `resources/list` [S11].

Claude Code hides `ui://` resources from `@` suggestions, but reading them by URI still works [S20].

```json
// resources/list entry (draft spec: _meta.ui may also go here as a static default; content-item value wins)
{ "uri": "ui://fancontrol/setup", "name": "fancontrol_setup", "description": "Setup wizard",
  "mimeType": "text/html;profile=mcp-app" }

// resources/read result
{ "contents": [{
    "uri": "ui://fancontrol/setup",
    "mimeType": "text/html;profile=mcp-app",
    "text": "<!DOCTYPE html><html>…</html>",
    "_meta": { "ui": {
        "csp": { "connectDomains": [], "resourceDomains": [], "frameDomains": [], "baseUriDomains": [] },
        "permissions": { "clipboardWrite": {} },
        "prefersBorder": true
        // "domain": "<host-specific>"   (omit for local/stdio servers)
    } } }] }
```

`_meta.ui` fields [S11]:

- `csp.connectDomains` maps to `connect-src`.
- `csp.resourceDomains` maps to script, style, img, font and media sources. Wildcard subdomains are allowed.
- `csp.frameDomains` maps to `frame-src`. Its default is `'none'`, and Claude restricts it [S17].
- `csp.baseUriDomains` maps to `base-uri`. Its default is `'self'`.
- `permissions` can request `camera`, `microphone`, `geolocation` and `clipboardWrite`.
- `domain` is a host-specific dedicated origin.
- `prefersBorder` is `true`, `false` or omitted. Hosts' defaults vary. Claude renders borderless on web and bordered on mobile [S17].

The default CSP when `ui.csp` is omitted (hosts MUST apply it) [S11]:

```
default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline';
img-src 'self' data:; media-src 'self' data:; connect-src 'none';
```

Inline `<script>` and `<style>` therefore always work, but `blob:` and `eval`-style code don't. That is why a single self-contained HTML file is the safest design.

Claude-specific `domain` format: `{first 32 hex chars of SHA-256(full connector URL)}.claudemcpcontent.com`. It is **not available for local stdio connectors** [S16][S18]. ChatGPT uses a different format, for example `www-example-com.oaiusercontent.com` [S11].

### 2.3 Linking a tool to the UI, and visibility

```json
{ "name": "setup_wizard", "description": "Open the FanControl setup wizard",
  "inputSchema": { "type": "object", "properties": {} },
  "_meta": {
    "ui": { "resourceUri": "ui://fancontrol/setup", "visibility": ["model", "app"] },
    "ui/resourceUri": "ui://fancontrol/setup",          // deprecated flat key, still written by the SDK
    "openai/outputTemplate": "ui://fancontrol/setup"     // ChatGPT compatibility alias (optional)
  } }

{ "name": "fan_apply_profile", "description": "Apply a profile (called by the UI)",
  "inputSchema": { "type": "object", "properties": { "profile": { "type": "string" } }, "required": ["profile"] },
  "_meta": { "ui": { "resourceUri": "ui://fancontrol/setup", "visibility": ["app"] } } }
```

Rules from [S11]:

- `visibility` defaults to `["model","app"]`.
- Hosts MUST hide tools whose `visibility` lacks `"model"` from the agent.
- Hosts MUST reject app `tools/call` requests for tools that lack `"app"`.
- Cross-server calls to app-only tools are always blocked.
- The flat `_meta["ui/resourceUri"]` key is deprecated and "will be removed before GA". The SDK's `registerAppTool` still writes both keys [S19].
- Hosts MUST fetch the UI with `resources/read` and MAY prefetch or cache it.

ChatGPT calls `_meta["openai/outputTemplate"]` a "ChatGPT compatibility alias" for `ui.resourceUri` [S24]. **Caveat (my inference):** a host that doesn't support MCP Apps, such as Claude Code, ignores `visibility`, so it *would* show `fan_apply_profile` to the model. Only list app-only tools when the client advertised `io.modelcontextprotocol/ui`. If they are listed anyway, make them safe to call. In the modern era, a `tools/list` that varies per capability may also interact with `ttlMs`/`cacheScope` caching. **UNVERIFIED**; use `cacheScope:"private"`.

### 2.4 The postMessage protocol (View = iframe, Host = chat client)

The protocol is JSON-RPC 2.0 over `window.parent.postMessage(msg, "*")`. The SDK's `PostMessageTransport` sends with target origin `"*"` and accepts only messages whose `event.source === window.parent`. It ignores non-JSON-RPC frames [S15].

**View → Host requests:**

| Method | Params | Result |
|---|---|---|
| `ui/initialize` | `{ appInfo:{name,version}, appCapabilities:{ experimental?, tools?:{listChanged?}, availableDisplayModes?:["inline","fullscreen","pip"] }, protocolVersion:"2026-01-26" }` | `{ protocolVersion, hostInfo, hostCapabilities, hostContext }` |
| `tools/call` | `{ name, arguments }`. Proxied to *your* server. Requires `hostCapabilities.serverTools` | standard `CallToolResult` |
| `resources/read` | `{ uri }`. Requires `hostCapabilities.serverResources` | standard result |
| `ui/message` | `{ role:"user", content:{ type:"text", text } }` | `{}`. The host adds it to the chat and MAY ask for consent |
| `ui/update-model-context` | `{ content?: ContentBlock[], structuredContent?: {} }` | `{}`. Each call overwrites the previous one. Used in future turns, possibly deferred until the next user message |
| `ui/open-link` | `{ url }` | `{}` |
| `ui/request-display-mode` | `{ mode:"inline"\|"fullscreen"\|"pip" }` | `{ mode }`, the actual mode |
| `ui/download-file` (**draft only**) | `{ contents:[EmbeddedResource\|ResourceLink] }` | `{}` |
| `ping` | | `{}` |

`ui/initialize` params use `appInfo`, `appCapabilities` and `protocolVersion`, taken from the SDK's `spec.types.ts` and `App.connect()`. The spec's prose sample still shows `initialize` with `clientInfo` and `capabilities`; follow the SDK [S13][S15]. The `LATEST_PROTOCOL_VERSION` is `"2026-01-26"`, and the SDK's `ui/initialize` sends that value [S13].

**View → Host notifications:**

- `ui/notifications/initialized` (params `{}` or none). Send it after the initialize result arrives. **The host MUST NOT send anything to the View before this** [S11].
- `ui/notifications/size-changed` `{ width, height }`. Hosts MUST honor it when the container is flexible, meaning `maxHeight` or `maxWidth` rather than a fixed size.
- `notifications/message` (logging).
- `ui/notifications/request-teardown` `{}` (**draft only**).

**Host → View notifications:**

| Method | Params |
|---|---|
| `ui/notifications/tool-input` | `{ arguments }`. Sent at most once, after init, always before tool-result |
| `ui/notifications/tool-input-partial` | `{ arguments }`. Sent 0..n times while the model streams args, best-effort JSON. Do not rely on it |
| `ui/notifications/tool-result` | a full `CallToolResult`: `{ content, structuredContent?, _meta?, isError? }` |
| `ui/notifications/tool-cancelled` | `{ reason }` |
| `ui/notifications/host-context-changed` | `Partial<HostContext>`. Merge it into the state you hold |

**Host → View requests:**

- `ui/resource-teardown` `{ reason }`. Reply `{}`, and the host SHOULD wait for that reply.
- `ping`.
- In the draft, `tools/call` and `tools/list` for app-registered tools, only if the app declared `tools`.

**HostContext** [S11]:

- `toolInfo{id,tool}`
- `theme` (`"light"` or `"dark"`)
- `styles.variables` (CSS custom properties) and `styles.css.fonts`
- `displayMode` and `availableDisplayModes`
- `containerDimensions`, for example `{width}` or `{maxWidth}` combined with `{height}` or `{maxHeight}`
- `locale`, `timeZone`, `userAgent`, `platform` (`web`, `desktop` or `mobile`), `deviceCapabilities{touch,hover}`, and `safeAreaInsets{top,right,bottom,left}`

**HostCapabilities:** `openLinks`, `serverTools{listChanged}`, `serverResources{listChanged}`, `logging`, and `sandbox{permissions,csp}`. The draft adds `downloadFile`, `updateModelContext`, `message` and `sampling`.

**Standard CSS variables:** the `--color-background-*`, `--color-text-*`, `--color-border-*` and `--color-ring-*` families (primary, secondary, tertiary, inverse, ghost, info, danger, success, warning, disabled), plus `--font-sans` and `--font-mono`, `--font-weight-{normal,medium,semibold,bold}`, `--font-text-{xs,sm,md,lg}-size` and `-line-height`, `--font-heading-{xs..3xl}-size` and `-line-height`, `--border-radius-{xs,sm,md,lg,xl,full}`, `--border-width-regular`, and `--shadow-{hairline,sm,md,lg}`. Hosts use `light-dark()` values. Views MUST define their own fallbacks [S11]. Claude's values are listed in [S17].

**Web-host sandbox (informational):** the stable spec's double iframe passes `ui/notifications/sandbox-proxy-ready` and `ui/notifications/sandbox-resource-ready {html,csp,permissions,sandbox}` between host and proxy. The proxy never forwards these to your View. The draft relaxes this to any single or double iframe on a host-controlled cross-origin, with `allow-scripts allow-same-origin` [S11][S12]. Claude Desktop shows your View as an iframe nested inside another iframe [S18]. Claude mobile uses a native WKWebView or Android WebView, with no camera, microphone or location [S17].

### 2.5 Minimal vanilla-JS View: single file, no bundler, no network

The file below completes the handshake, applies theme variables, renders the tool result, calls a server tool on click, reports its size, and answers ping and teardown. It needs no `csp` entries because it uses only inline script and style.

```html
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="color-scheme" content="light dark">
<title>FanControl setup</title>
<style>
  :root {                                   /* fallbacks; host variables override these */
    color-scheme: light dark;
    --color-background-secondary: light-dark(#f5f5f5, #262626);
    --color-text-primary: light-dark(#171717, #fafafa);
    --color-text-secondary: light-dark(#525252, #a3a3a3);
    --color-border-primary: light-dark(#d4d4d4, #404040);
    --color-background-inverse: light-dark(#171717, #fafafa);
    --color-text-inverse: light-dark(#fafafa, #171717);
    --font-sans: system-ui, sans-serif;
    --border-radius-md: 8px;
  }
  html, body { margin: 0; background: transparent; }
  body { font-family: var(--font-sans); color: var(--color-text-primary); padding: 12px; }
  .card { background: var(--color-background-secondary); border: 1px solid var(--color-border-primary);
          border-radius: var(--border-radius-md); padding: 12px; }
  #status { color: var(--color-text-secondary); font-size: 13px; margin-bottom: 8px; }
  pre { white-space: pre-wrap; margin: 8px 0; font-size: 13px; }
  button { background: var(--color-background-inverse); color: var(--color-text-inverse); border: 0;
           border-radius: var(--border-radius-md); padding: 8px 14px; font: inherit; cursor: pointer; }
  button:disabled { opacity: .5; cursor: default; }
</style>
</head>
<body>
<div class="card">
  <div id="status">Connecting…</div>
  <pre id="result">(waiting for tool result)</pre>
  <button id="apply" disabled>Apply "Silent" profile</button>
</div>
<script>
(() => {
  "use strict";
  const APP_PROTOCOL_VERSION = "2026-01-26";
  const $ = (id) => document.getElementById(id);
  let nextId = 1;
  const pending = new Map();
  let hostCaps = {};
  let lastArgs = {};

  // ---- JSON-RPC over postMessage ---------------------------------------------
  const send = (msg) => window.parent.postMessage(msg, "*");
  const notify = (method, params) => send({ jsonrpc: "2.0", method, params: params ?? {} });
  const request = (method, params) => new Promise((resolve, reject) => {
    const id = nextId++;
    pending.set(id, { resolve, reject });
    send({ jsonrpc: "2.0", id, method, params: params ?? {} });
  });
  const reply = (id, result) => send({ jsonrpc: "2.0", id, result });
  const replyError = (id, code, message) => send({ jsonrpc: "2.0", id, error: { code, message } });

  // ---- host context / theming ------------------------------------------------
  function applyHostContext(ctx) {
    if (!ctx) return;
    const root = document.documentElement;
    if (ctx.theme) root.style.colorScheme = ctx.theme;           // makes light-dark() follow host theme
    const vars = ctx.styles && ctx.styles.variables;
    if (vars) for (const [k, v] of Object.entries(vars)) if (v) root.style.setProperty(k, v);
    const s = ctx.safeAreaInsets;
    if (s) document.body.style.padding =
      `${12 + s.top}px ${12 + s.right}px ${12 + s.bottom}px ${12 + s.left}px`;
  }

  // ---- rendering -------------------------------------------------------------
  function renderResult(result) {
    if (!result) return;
    const text = (result.content || []).filter(c => c.type === "text").map(c => c.text).join("\n");
    $("result").textContent = result.structuredContent
      ? JSON.stringify(result.structuredContent, null, 2)
      : (text || "(empty result)");
    $("status").textContent = result.isError ? "Tool reported an error" : "Ready";
  }

  // ---- inbound messages ------------------------------------------------------
  window.addEventListener("message", (event) => {
    if (event.source !== window.parent) return;                  // only trust the host frame
    const msg = event.data;
    if (!msg || msg.jsonrpc !== "2.0") return;

    if (msg.method === undefined && msg.id !== undefined) {      // response to one of our requests
      const p = pending.get(msg.id);
      if (!p) return;
      pending.delete(msg.id);
      if (msg.error) p.reject(Object.assign(new Error(msg.error.message), { code: msg.error.code }));
      else p.resolve(msg.result);
      return;
    }
    if (msg.id !== undefined) {                                   // request from host
      switch (msg.method) {
        case "ping": return reply(msg.id, {});
        case "ui/resource-teardown": /* persist state here */ return reply(msg.id, {});
        default: return replyError(msg.id, -32601, "Method not found: " + msg.method);
      }
    }
    switch (msg.method) {                                         // notifications from host
      case "ui/notifications/tool-input":
        lastArgs = (msg.params && msg.params.arguments) || {};
        $("status").textContent = "Running tool…";
        break;
      case "ui/notifications/tool-input-partial": break;          // optional streaming preview
      case "ui/notifications/tool-result": renderResult(msg.params); break;
      case "ui/notifications/tool-cancelled":
        $("status").textContent = "Cancelled: " + ((msg.params && msg.params.reason) || "");
        break;
      case "ui/notifications/host-context-changed": applyHostContext(msg.params); break;
    }
  });

  // ---- size reporting (mirrors SDK autoResize) ---------------------------------
  let lastW = 0, lastH = 0, scheduled = false;
  function reportSize() {
    if (scheduled) return;
    scheduled = true;
    requestAnimationFrame(() => {
      scheduled = false;
      const html = document.documentElement, prev = html.style.height;
      html.style.height = "max-content";
      const height = Math.ceil(html.getBoundingClientRect().height);
      html.style.height = prev;
      const width = Math.ceil(window.innerWidth);
      if (width !== lastW || height !== lastH) {
        lastW = width; lastH = height;
        notify("ui/notifications/size-changed", { width, height });
      }
    });
  }

  // ---- button -> server tool via host ----------------------------------------
  $("apply").addEventListener("click", async () => {
    $("apply").disabled = true;
    $("status").textContent = "Applying…";
    try {
      const result = await request("tools/call",
        { name: "fan_apply_profile", arguments: { profile: "silent" } });
      renderResult(result);
      // optional: let the model know what the user did in the UI
      request("ui/update-model-context",
        { content: [{ type: "text", text: "User applied the Silent fan profile in the setup UI." }] })
        .catch(() => {});
    } catch (e) {
      $("status").textContent = "Error: " + e.message;
    } finally {
      $("apply").disabled = !hostCaps.serverTools;
    }
  });

  // ---- handshake -------------------------------------------------------------
  (async () => {
    try {
      const init = await request("ui/initialize", {
        appInfo: { name: "fancontrol-setup", version: "0.1.0" },
        appCapabilities: { availableDisplayModes: ["inline"] },
        protocolVersion: APP_PROTOCOL_VERSION,
      });
      hostCaps = init.hostCapabilities || {};
      applyHostContext(init.hostContext);
      notify("ui/notifications/initialized");                    // host may now push tool-input/result
      $("status").textContent = "Connected to " + ((init.hostInfo && init.hostInfo.name) || "host");
      $("apply").disabled = !hostCaps.serverTools;
      const ro = new ResizeObserver(reportSize);
      ro.observe(document.documentElement); ro.observe(document.body);
      reportSize();
    } catch (e) {
      $("status").textContent = "Handshake failed: " + e.message;
    }
  })();
})();
</script>
</body>
</html>
```

The tool result your server returns for `setup_wizard` must work for text-only hosts too:

```json
{ "content": [ { "type": "text", "text": "Setup wizard opened. Current profile: balanced. Fans: CPU (820 RPM), Front (640 RPM)." } ],
  "structuredContent": { "profile": "balanced", "fans": [ { "id": "fan0", "name": "CPU", "rpm": 820 } ] } }
```

`structuredContent` is meant for the UI and is "not added to model context" per the ext-apps best-practice note [S11]. However, Claude Code passes `structuredContent`, not `content`, as the tool result to the model when it is present [S19]. Keep it small and meaningful. Claude.ai and Desktop divert results over about 150,000 characters to a file, and the app then never hydrates [S18].

### 2.6 What the official SDK does that a hand-rolled View must replicate

From reading `src/app.ts`, `src/message-transport.ts` and `src/spec.types.ts` [S13][S15]:

1. **Transport:** `postMessage(msg, "*")` to `window.parent`. Drop events whose `event.source !== window.parent`. Silently ignore non-JSON-RPC frames.
2. **Handshake:** `ui/initialize` with `{appCapabilities, appInfo, protocolVersion:"2026-01-26"}`, store `hostCapabilities`, `hostInfo` and `hostContext`, then send `ui/notifications/initialized`. On failure, close.
3. **Handlers before connect:** register tool-input and tool-result handlers *before* the handshake. The first notifications arrive right after `initialized` [S16][S18].
4. **`ping` handler:** returns `{}`.
5. **Host-context merge:** merge every `host-context-changed` into the stored context. The helpers `applyHostStyleVariables`, `applyDocumentTheme` and `applyHostFonts` inject the CSS variables, the theme and the `styles.css.fonts` font CSS.
6. **autoResize (default on):** a `ResizeObserver` on `<html>` and `<body>`, debounced with `requestAnimationFrame`. Height comes from `html.style.height="max-content"` plus `getBoundingClientRect()`, width from `window.innerWidth`, and the notification is sent only on change.
7. **Teardown:** `onteardown` answers `ui/resource-teardown`.
8. **Capability guards:** the SDK warns or throws when a capability is missing. Check `hostCapabilities.serverTools` before `tools/call`, `openLinks` before `ui/open-link`, and so on.
9. **Extras not needed for the wizard:** app-registered tools (`tools/list`, `tools/call` from host to app), WebMCP interop, zod validation (`jitless`, so it runs without `eval`), and `sampling/createMessage` (draft).

**Can the SDK be inlined into one HTML file?** Yes:

- The package exports a prebuilt self-contained ESM bundle at `@modelcontextprotocol/ext-apps/app-with-deps` (`dist/src/app-with-deps.js`). v2.0.4 is about **530 KB** and ends in `export {…, App}` [S19b].
- The official examples build single-file HTML with **Vite + `vite-plugin-singlefile`** [S13b].
- Claude's no-build quickstart instead imports the bundle from `unpkg.com` and must add `csp.resourceDomains: ["https://unpkg.com"]`. That breaks the "no external network" goal and is discouraged for production [S19].
- **You cannot** paste an ESM bundle into an inline `<script type="module">` and then `import` from it. Turning it into a `blob:` URL is blocked by the default CSP, which has no `blob:` in `script-src`. Use the bundler, or serve the bundle from a `resourceDomains` origin. For a small wizard, the hand-rolled ~120 lines above avoid a 530 KB payload.
- The Claude docs say the SDK's `App.connect()` "detects the host and picks the transport" [S16]. I did not find host-detection code in `app.ts` or `message-transport.ts` v2.0.4, where the default is `PostMessageTransport(window.parent, window.parent)`. **UNVERIFIED** whether any host needs a non-postMessage transport.

---

## 3. Hosts that render MCP Apps today, and fallbacks

| Host | Renders MCP Apps? | Notes / source |
|---|---|---|
| Claude (web), Claude Desktop, Claude mobile, Cowork | **Yes** | Listed in the extension matrix [S25]. Claude docs: the user must click **Allow** (or "Always allow") [S16]. Display modes are an inline card, an inline carousel and fullscreen. `frameDomains` is restricted [S17]. Debug with Help > Troubleshooting > Enable Developer Mode, then Ctrl+Shift+I [S18]. **Important for a localhost server:** a server added *by URL* is reached from Anthropic's infrastructure, so `localhost` is unreachable. Locally, Claude Desktop runs stdio servers from `claude_desktop_config.json`, and the docs suggest a proxy such as `mcp-remote` for testing [S16][S19] |
| **Claude Code** | **No** | "Claude Code calls the tool as text and doesn't render the UI" [S19]. Not ticked in the matrix [S25] |
| ChatGPT (web/desktop) | **Yes** | Implements the MCP Apps bridge (`ui/initialize`, `ui/notifications/tool-input` and `tool-result`, `ui/message`, `ui/update-model-context`, `tools/call`). Keeps `window.openai` only for ChatGPT-specific extras such as `widgetState`, `requestModal`, checkout and files. MIME type is `text/html;profile=mcp-app`, and `openai/outputTemplate` is an alias [S24][S25]. It requires a server reachable from OpenAI (developer mode, SSE or streaming HTTP) [S26]. **UNVERIFIED** whether ChatGPT *desktop* can reach `localhost` |
| Codex (CLI / IDE / app) | **UNVERIFIED, probably no** | Not in the matrix [S25]. Codex docs mention no MCP Apps rendering |
| VS Code GitHub Copilot | **Yes (marked Experimental)** | `inline` only. `ui/message` fills the chat input without sending. Context updates appear as attachments. Clipboard write is supported. Camera, microphone and geolocation are not [S21][S21b] |
| Microsoft 365 Copilot, Goose, Postman, MCPJam, Cursor, Archestra.AI, PostHog Code, Kiro Crew, Glean | **Yes** | Community-maintained extension matrix [S25]. Cursor also lists "Apps (extension)" [S22] |

**Fallback rules:**

- A host that doesn't support MCP Apps treats the tool as a normal tool, so it must return meaningful `content` [S11].
- The servers SHOULD rule: check the capability, and provide text-only behavior [S11].
- Any extension rule (core spec): if one side lacks the extension, revert to core behavior or reject with an error [S4].
- For fancontrol, run the wizard in this order. If the host has MCP Apps, use the UI. Else, if the client declared `elicitation`, use a form sequence. Otherwise use plain tool arguments the model collects in chat.

---

## 4. Concrete recommendations for fancontrol-mcp (C#)

1. **Legacy HTTP first.**
   - Issue `MCP-Session-Id` on the initialize response and require it afterwards: 400 if it is missing, 404 if unknown.
   - Validate `MCP-Protocol-Version` and validate `Origin` (403 if invalid). Bind to 127.0.0.1.
   - Return `application/json` by default. Switch the `tools/call` response to `text/event-stream` only when you need to elicit or stream progress.
   - Accept POSTed JSON-RPC responses and notifications with **202**.
   - Store per-session client capabilities from `initialize`, including `elicitation.form` and `url`, and `extensions["io.modelcontextprotocol/ui"].mimeTypes`.
2. **Gate features on capabilities:**
   - Only send `elicitation/create` with a declared mode.
   - Only list app-only (`visibility:["app"]`) tools and the `ui://` resource when the UI extension is advertised.
   - Always include text `content`.
3. **Dual-era later:**
   - Detect a modern request by `params._meta["io.modelcontextprotocol/protocolVersion"]` without `initialize`.
   - Implement `server/discover`, `resultType`, `ttlMs`/`cacheScope`, the `Mcp-Method`/`Mcp-Name` checks, `-32022`, and MRTR. Use an HMAC'd `requestState` that carries the wizard step.
   - Answer GET and DELETE with 405 on the modern path.
4. **Desktop reachability:** Claude Desktop and ChatGPT reach URL-added servers from the cloud. Consider shipping a **stdio entry point**, for example `fancontrol.exe --stdio` bridging to the running app, so Claude Desktop can launch it from `claude_desktop_config.json`. A loopback HTTP server alone won't do this. Claude Code, VS Code, Cursor and Codex can connect to `http://127.0.0.1:PORT/mcp` directly.
5. **Never elicit secrets in form mode.** For a fan controller this is unlikely to matter.

---

## 5. Unverified / open items

- Claude Desktop chat and claude.ai elicitation support: no Anthropic doc found. Third-party reports say it is not surfaced [S27][S28].
- ChatGPT elicitation support. Whether ChatGPT desktop can reach a localhost server.
- Codex MCP Apps rendering. Which elicitation modes Codex declares.
- Which elicitation modes VS Code and Cursor declare (form vs url). Which MCP protocol revisions VS Code and Cursor speak (2026-07-28 or legacy only).
- Whether `mcp-remote` transparently forwards SSE-embedded server requests and the `extensions` capability. It is a stdio-to-HTTP proxy, so this is likely but untested.
- Client-side request timeouts while waiting on an elicitation, per client.
- `pattern` in string schemas: it is on the 2025-11-25 page but absent from the 2026-07-28 type excerpt.
- The legacy OpenAI MIME type `text/html+skybridge` and the keys `openai/widgetAccessible`, `openai/widgetCSP` and `openai/widgetDomain` come from my prior knowledge. The current OpenAI page doesn't mention them, so prefer the `ui.*` keys.
- The Claude doc claim that `App.connect()` auto-detects the host transport (see 2.6).
- How Claude Desktop advertises the UI extension in legacy `initialize`, whether in `capabilities.extensions` or elsewhere. The ext-apps spec and SDK read `extensions`. As a defensive measure, also accept `capabilities.experimental["io.modelcontextprotocol/ui"]` (my suggestion, not a spec rule).

---

## Sources

- [S1] MCP docs config, "Version 2026-07-28 (latest)": https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/docs.json
- [S2] 2026-07-28 changelog: https://modelcontextprotocol.io/specification/2026-07-28/changelog
- [S3] MRTR pattern: https://modelcontextprotocol.io/specification/2026-07-28/basic/patterns/mrtr
- [S4] Versioning and extension negotiation: https://modelcontextprotocol.io/specification/2026-07-28/basic/versioning
- [S5] Streamable HTTP (2026-07-28): https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http
- [S6] Elicitation (2025-11-25): https://modelcontextprotocol.io/specification/2025-11-25/client/elicitation
- [S7] Elicitation (latest = 2026-07-28): https://modelcontextprotocol.io/specification/latest/client/elicitation
- [S8] Schema 2026-07-28 (Elicit*, enum schemas, RequestMetaObject): https://modelcontextprotocol.io/specification/2026-07-28/schema and https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/schema/2026-07-28/schema.ts
- [S9] Transports (2025-11-25), Streamable HTTP, sessions, resumability: https://modelcontextprotocol.io/specification/2025-11-25/basic/transports
- [S10] `CallToolRequestParams extends InputResponseRequestParams`, DiscoverResult example: https://github.com/modelcontextprotocol/modelcontextprotocol/tree/main/schema/2026-07-28
- [S11] MCP Apps spec, Stable 2026-01-26: https://github.com/modelcontextprotocol/ext-apps/blob/main/specification/2026-01-26/apps.mdx
- [S12] MCP Apps draft spec: https://github.com/modelcontextprotocol/ext-apps/blob/main/specification/draft/apps.mdx
- [S13] ext-apps SDK `src/spec.types.ts` and `src/app.ts` (v2.0.4): https://github.com/modelcontextprotocol/ext-apps/tree/main/src ; releases: https://github.com/modelcontextprotocol/ext-apps/releases
- [S13b] Vanilla example build (vite-plugin-singlefile): https://github.com/modelcontextprotocol/ext-apps/tree/main/examples/basic-server-vanillajs
- [S14] SDK server helpers (`EXTENSION_ID`, `getUiCapability`): https://github.com/modelcontextprotocol/ext-apps/blob/main/src/server/index.ts
- [S15] SDK `src/message-transport.ts`: https://github.com/modelcontextprotocol/ext-apps/blob/main/src/message-transport.ts
- [S16] Claude: Get started with MCP Apps: https://claude.com/docs/connectors/building/mcp-apps/getting-started
- [S17] Claude: MCP Apps design guidelines: https://claude.com/docs/connectors/building/mcp-apps/design-guidelines
- [S18] Claude: Troubleshoot MCP Apps: https://claude.com/docs/connectors/building/mcp-apps/troubleshooting
- [S19] Claude: Add an interactive UI to your MCP server (quickstart): https://claude.com/docs/connectors/building/mcp-apps/quickstart
- [S19b] Bundle size check: https://unpkg.com/@modelcontextprotocol/ext-apps@2.0.4/dist/src/app-with-deps.js
- [S20] Claude Code MCP docs (elicitation modes, runtimes, UI resources): https://code.claude.com/docs/en/mcp
- [S21] VS Code MCP servers (MCP Apps experimental): https://code.visualstudio.com/docs/copilot/customization/mcp-servers
- [S21b] VS Code MCP extension guide (feature list, MCP Apps limitations table): https://code.visualstudio.com/api/extension-guides/ai/mcp
- [S22] Cursor MCP docs: https://cursor.com/docs/context/mcp
- [S23] Codex config reference (`approval_policy.granular.mcp_elicitations`): https://learn.chatgpt.com/docs/config-file/config-reference
- [S24] OpenAI Apps SDK, ChatGPT UI: https://developers.openai.com/apps-sdk/build/chatgpt-ui ; MCP server: https://developers.openai.com/apps-sdk/build/mcp-server
- [S25] MCP extension client matrix: https://modelcontextprotocol.io/extensions/client-matrix (raw: https://github.com/modelcontextprotocol/modelcontextprotocol/blob/main/docs/extensions/client-matrix.mdx)
- [S26] OpenAI developer mode: https://developers.openai.com/api/docs/guides/developer-mode/index.html
- [S27] Relativity (third-party) on Claude.ai/Cowork elicitation: https://help.relativity.com/RelativityOne/Content/Relativity/MCP/Anthropic_Claude.htm
- [S28] Consilio (third-party) on claude.ai elicitation: https://mcp-docs.ai.consilio.com/docs/integrations/claude-ai/
- MCP Apps overview: https://modelcontextprotocol.io/extensions/apps/overview
