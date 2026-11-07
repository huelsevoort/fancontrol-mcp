```text
.---------------------.
| o                 o |    ┌─┐┌─┐┌┐┌┌─┐┌─┐┌┐┌┌┬┐┬─┐┌─┐┬
|      _.-----._      |    ├┤ ├─┤││││  │ ││││ │ ├┬┘│ ││
|    .-'\  |  /'-.    |    └  ┴ ┴┘└┘└─┘└─┘┘└┘ ┴ ┴└─└─┘┴─┘
|   /   \  |  /   \   |    ███╗   ███╗ ██████╗██████╗
|  |-----( @ )-----|  |    ████╗ ████║██╔════╝██╔══██╗   AI-tuned fan curves
|   \   /  |  \   /   |    ██╔████╔██║██║     ██████╔╝   for FanControl
|    '-./  |  \.-'    |    ██║╚██╔╝██║██║     ██╔═══╝
|      '-.___.-'      |    ██║ ╚═╝ ██║╚██████╗██║        v1.1.0 · open source
| o                 o |    ╚═╝     ╚═╝ ╚═════╝╚═╝
'---------------------'
```

**Open Source FanControl plugin that allows your AI models to find the ideal fan settings for your setup.**

A [FanControl](https://getfancontrol.com) plugin that runs a **Model Context Protocol (MCP) server inside FanControl**, so AI assistants
(Claude, ChatGPT/Codex, Copilot, Cursor …) can read temperatures and fan speeds, tune fan curves with built-in expert knowledge,
and walk you through a complete fan setup.

```
AI client ──HTTP──▶ http://127.0.0.1:48620/mcp ──▶ FanControl.MCP.dll (inside FanControl) ──▶ FanControl engine
```

- One DLL, no dependencies; works in FanControl's **desktop** and **Windows service** mode
- **Guided setup wizard**: interviews you about goal, room, case, fan layout, coolers and GPU, then proposes a baseline configuration
- **Built-in best practices**: research-backed guide (thermal limits, airflow, noise, pumps, FanControl specifics) plus an automatic setup analyzer
- **Measure, don't guess**: A/B measurements under your real workload, including CPU/GPU clocks, load, power and GPU limit reasons, with a temperature guard that restores the previous settings automatically
- Every change is applied in memory first, with restore points; nothing is written to your config until you agree
- Local only: binds to `127.0.0.1`, checks `Origin`, optional bearer token, optional read-only mode
- Tested with FanControl **V285** (.NET 10) on Windows 11

> **Status:** early release. The engine access uses FanControl internals (see [How it works](#how-it-works)); a FanControl update can
> break those tools until the plugin is rebuilt. FanControl itself keeps running either way.

## Quick start

Requirements: FanControl V285+, [.NET 10 SDK](https://dotnet.microsoft.com/download) to build.

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

The script builds the plugin, copies `FanControl.MCP.dll` into `C:\Program Files (x86)\FanControl\Plugins` (asks for admin rights)
and restarts FanControl. Options: `-FanControlDir <path>`, `-NoRestart`, `-Uninstall`.
FanControl's `log.txt` then shows `[FanControl.MCP] MCP server listening on http://127.0.0.1:48620/mcp`.

Then connect your AI client (below) and say *"Set up my fans"* or run the `setup_fan_control` prompt.

## Connecting clients

| Client | How | Wizard experience |
|---|---|---|
| **Claude Code** | `claude mcp add --transport http fancontrol http://127.0.0.1:48620/mcp` | native **forms** (elicitation) |
| **Claude Desktop** | via [`mcp-remote`](https://www.npmjs.com/package/mcp-remote) (Node.js), see below | interactive **cards UI** (MCP Apps) |
| **Codex** (CLI/IDE/app) & **ChatGPT desktop** | `~/.codex/config.toml`, see below | conversation; forms if enabled |
| **VS Code (Copilot)**, **Cursor** | add an HTTP MCP server with the URL | cards UI and/or forms |
| ChatGPT in the browser | needs a public HTTPS endpoint: **not supported** on purpose (it would expose fan control to the internet) | – |

Claude Desktop (`claude_desktop_config.json`); Claude Desktop reaches URL-based connectors from the cloud, so local servers go through a stdio bridge:

```json
{
  "mcpServers": {
    "fancontrol": { "command": "npx", "args": ["-y", "mcp-remote", "http://127.0.0.1:48620/mcp"] }
  }
}
```

Codex / ChatGPT desktop (`~/.codex/config.toml`):

```toml
[mcp_servers.fancontrol]
url = "http://127.0.0.1:48620/mcp"
# optional, to allow wizard forms in Codex:
# [approval_policy.granular]
# mcp_elicitations = true
```

With a token (`authToken` in the settings): Claude Code `--header "Authorization: Bearer <token>"`, mcp-remote `"--header", "Authorization: Bearer <token>"`,
Codex `bearer_token_env_var = "FANCONTROL_MCP_TOKEN"`.

## The setup wizard

The wizard collects what an assistant needs to judge a fan setup:

| Step | Asks for | Why it matters |
|---|---|---|
| Goal | quietest without performance loss · balanced · maximum cooling · silent first · steady speeds; noise sensitivity; what bothers you | where curves end, how warm parts may run |
| Usage & room | workloads, typical games/apps, room and summer temperature, where the PC stands | idle floors, seasonal headroom |
| Case | model, size, front panel (mesh/solid/glass), dust filters | how much air fans can actually move |
| Fans & airflow | per fan output: position, direction, size, model, fans per header (pre-filled from names and RPM) | which temperature should drive which fan, pressure balance, empty headers |
| CPU cooling | CPU (detected), air/AIO/loop, model, radiator size/position/direction, pump header | air coolers react in seconds, AIOs follow the slow liquid temperature |
| Graphics card | exact model and partner card (detected), cooler type, mount, zero-RPM wish | fan minimums, temperature targets |
| BIOS & software | BIOS fallback, header PWM/DC mode, other fan/RGB tools (detected), autostart | conflicts and what happens if FanControl stops |

Hardware is detected from FanControl's sensors (CPU/GPU model and limits, connected headers, liquid sensors, conflicting software such as
Afterburner, Armoury Crate, iCUE, NZXT CAM). The answers are stored in `Plugins\FanControl.MCP.profile.json`, so the wizard can be resumed.

**Three presentation levels, same questions:**

1. **Conversation** (every client): `setup_wizard` returns the steps; the assistant asks in your language and saves with `save_system_profile`.
2. **Forms** (clients with MCP elicitation, e.g. Claude Code, VS Code, Cursor): `setup_wizard_form` shows one native form per step and per fan.
3. **Cards UI** (clients with MCP Apps, e.g. Claude Desktop/web, ChatGPT, VS Code, Cursor, Goose): an interactive view with option tiles, a fan table,
   and the resulting plan with curve charts. Applying goes back through the chat so the assistant explains it first.

**From answers to a validated configuration:**

```
setup_wizard ─▶ propose_baseline_config ─▶ (you agree) ─▶ apply_baseline_config ─▶ start_measurement / mark_phase ─▶ save_config
                 sensors, curves, tuning,     restore point     guard restores on       compare clocks & noise
                 pressure offsets, warnings   created first     temperature alarm       per phase
```

The baseline planner places curve end points relative to each component's limits (e.g. Ryzen 7000/9000: 100 % fan by ~90 °C;
NVIDIA: by ~84 °C because GPU Boost drops bins from ~83 °C), uses smoothed CPU/GPU sources, a Max(CPU, GPU) source for case fans,
liquid temperature for radiators, fixed pump speeds, slight positive pressure, and warns about anti-patterns (bottom radiators,
bottom exhausts, top-front exhaust with air coolers, competing fan software, AMD GPU control resetting Adrenalin tuning …).

## Built-in knowledge

- **Guide** ([fan-curve-best-practices.md](src/FanControl.MCP/Guides/fan-curve-best-practices.md)), delivered as server instructions,
  the `get_fan_curve_guide` tool, the resource `fancontrol://guides/fan-curve-best-practices` and embedded in the prompts.
- **Prompts**: `tune_fan_curves`, `diagnose_fan_noise`, `review_fan_setup` (read-only).
- **Analyzer** (`analyze_fan_setup`): 0 % on CPU/pump/radiator fans, pumps below 60 %, curves that never or too late reach 100 %,
  unsmoothed CPU sources, radiator fans on CPU temperature, floors below the stall value, NVIDIA fans in the invalid 1–29 % range,
  hot liquid, missing sources, forgotten manual overrides, missing calibration, unused curves.
- **Research** with sources and confidence levels: [docs/research](docs/research/README.md).

## Tools

| Area | Tools |
|---|---|
| Read | `get_status`, `get_snapshot`, `list_temperatures`, `list_fans`, `list_controls`, `list_fan_curves`, `list_configs`, `list_hardware_sensors`, `detect_hardware` |
| Control | `set_fan_speed`, `set_fan_automatic`, `set_control_enabled`, `set_virtual_sensor` |
| Curves & sensors | `update_fan_curve`, `create_fan_curve`, `delete_fan_curve`, `update_control_tuning`, `create_time_average_sensor`, `create_mix_sensor`, `delete_custom_sensor` |
| Wizard | `setup_wizard`, `setup_wizard_form`, `get_system_profile`, `save_system_profile`, `propose_baseline_config`, `apply_baseline_config` |
| Safety & validation | `create_restore_point`, `list_restore_points`, `restore_restore_point`, `start_measurement`, `mark_phase`, `stop_measurement`, `get_measurement_summary`, `analyze_fan_setup` |
| Configs | `load_config`, `save_config` |
| Knowledge | `get_fan_curve_guide` |

All changes take effect within about a second but are only written to FanControl's config file by `save_config`.

### Virtual sensors

`MCP Virtual 1`–`4` appear as temperature sources in FanControl. Use them in your own curves (e.g. a Mix (Max) with a "boost" curve)
so an assistant can trigger a boost without touching your configuration. This uses only the official plugin API.

## Settings

`Plugins\FanControl.MCP.json` (created on first start):

| Setting | Default | Meaning |
|---|---|---|
| `port` | `48620` | TCP port |
| `bindAddress` | `127.0.0.1` | keep on loopback unless you secure access otherwise |
| `authToken` | empty | when set, every request needs `Authorization: Bearer <token>` |
| `allowWrite` | `true` | `false` = read-only, all changing tools are rejected |
| `minimumManualPercent` | `0` | safety floor for manual speeds and minimum % |
| `virtualSensorCount` | `4` | number of virtual sensors (0–16) |

## How it works

FanControl's official plugin API (`IPlugin`/`IPlugin2`/`IPlugin3`) can only *add* sensors. To read and control everything, the plugin
finds the running `FanControlEngine` in its own process: FanControl subscribes to the plugin's `IPlugin3.RefreshRequested` event,
and the chain of event subscribers leads `PluginBackendProvider → ComputerAccessLayer → FanControlEngine`. The search is limited to
FanControl's own types and works in desktop and service mode. Writes go through FanControl's own configuration objects
(`UpdateConfig` with copies of `Serializable*Config`), on the WPF dispatcher in desktop mode and under the engine lock in service mode.
Load, clock and power values come from FanControl's LibreHardwareMonitor backend; NVIDIA clocks, power and limit reasons from `nvidia-smi`.

The MCP server is a small hand-written Streamable HTTP implementation (no ASP.NET Core, which FanControl does not load): sessions,
JSON responses, SSE responses when the server needs to ask the client (elicitation), MCP Apps resources (`ui://`, `text/html;profile=mcp-app`).
Protocol versions 2024-11-05 to 2025-11-25.

**Limitations:** the engine tools depend on FanControl internals (public types in `FanControl.Library.dll`); after a FanControl update
they may return an error until rebuilt against the new version (`install.ps1`). Virtual sensors keep working. The 2026-07-28 MCP
revision (stateless, `input_required`) is not implemented yet; clients fall back to 2025-11-25.

## Development

```powershell
dotnet build -c Release                                   # plugin + tests (references FanControl from its install folder)
dotnet run --project tests/SmokeTest                       # protocol, analyzer, wizard and planner tests without FanControl
dotnet run --project tests/SmokeTest -- http://127.0.0.1:48620/mcp            # read-only against a running FanControl
dotnet run --project tests/SmokeTest -- http://127.0.0.1:48620/mcp --elicit   # + form (elicitation) round trip
dotnet run --project tests/SmokeTest -- http://127.0.0.1:48620/mcp --write --control <controlId> --curve "<curve>"   # + reversible writes
```

`tools/fan-logger.ps1` and `tools/summarize-log.ps1` log and summarise temperatures, fans, load and clocks to CSV outside of MCP.

```
src/FanControl.MCP/
  McpPlugin.cs            plugin entry (IPlugin3), life-cycle, engine lookup
  FanControlTools.cs      core tools        Wizard/WizardTools.cs   wizard, measurement, restore tools
  Engine/                 engine locator, bridge (read/write, restore points), change validation
  Wizard/                 profile, hardware detection, wizard catalogue, baseline planner, plan executor, forms, measurement
  Mcp/                    HTTP server (sessions, SSE), JSON-RPC/MCP protocol
  SetupAnalyzer.cs        rule checks          TuningSkill.cs   guide resource + prompts
  Guides/                 best-practice guide  Ui/wizard.html   MCP Apps view
docs/research/            research notes with sources
tests/SmokeTest/          end-to-end and unit-style tests
```

## Safety

Anyone who can reach the endpoint can slow down or stop fans. The server therefore binds to `127.0.0.1`, rejects foreign browser
origins, and supports a token, a read-only mode and a minimum-speed floor. Measurements run with a temperature guard by default.
Keep a sane BIOS fan fallback: it is what protects the PC if FanControl is not running.

## License

MIT, see [LICENSE](LICENSE). FanControl is a separate product by Rem0o; this repository contains no FanControl binaries.
