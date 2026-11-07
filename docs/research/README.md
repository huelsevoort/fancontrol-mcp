# Research notes

Background research behind the plugin's guide (`src/FanControl.MCP/Guides/fan-curve-best-practices.md`), the setup analyzer and the
baseline planner. Collected in November 2026 from vendor documentation, independent testing and FanControl's own docs and issues.
Every rule carries a source link and a confidence level (H/M/L); open questions and conflicting evidence are listed per file.

| File | Topic | Feeds |
|---|---|---|
| [thermal-limits.md](thermal-limits.md) | Tjmax, temperature-dependent boost (Ryzen, Intel, NVIDIA, Radeon), memory, NVMe, chipset, VRM, ambient | curve end points (`ThermalLimits`), guard limits |
| [airflow.md](airflow.md) | pressure, layouts, fan count, mesh vs solid, radiator placement, sensors for case fans | planner pressure offsets and layout warnings |
| [fans-and-noise.md](fans-and-noise.md) | PWM/DC, start/stop, zero-RPM, noise perception, ramp rates, hubs, GPU fan limits | floors, zero-RPM rules, NVIDIA 30 % range |
| [liquid-cooling.md](liquid-cooling.md) | pump speeds per type, liquid temperature limits and curves, missing liquid sensors, pump position, failure signs | pump and radiator rules, liquid alarms |
| [fancontrol-practices.md](fancontrol-practices.md) | BIOS setup, PawnIO, service mode, pairing/calibration, hysteresis semantics, software conflicts | guide sections 2, 5, 6, 8 |
| [mcp-elicitation-and-apps.md](mcp-elicitation-and-apps.md) | MCP elicitation over Streamable HTTP, MCP Apps protocol, client support | forms and cards UI implementation |

Some numbers are heuristics derived by the researcher rather than measurements; they are tagged as such and rated low confidence.
Measured on the author's system (Ryzen 5 9600X, RTX 2070, Battlefield 6): the RTX 2070 dropped its first boost bin (1950 → 1935 MHz) at ~83 °C;
more case airflow did not lower GPU temperature; the CPU fan could run ~15 % slower for +1 °C without clock loss.
