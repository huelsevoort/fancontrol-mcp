# FanControl fan curve best practices

Guide for an assistant that tunes FanControl through this MCP server. Read it before proposing or changing curves.
Goal order, always: **1. safe temperatures → 2. low noise → 3. stable, non-annoying behaviour**.

## 1. Working agreement with the user

1. Look first: `get_snapshot`, `list_fan_curves`, `analyze_fan_setup`. Never change anything blind.
2. Ask what the user wants if unclear: silent, balanced or performance; which noise bothers them (idle hum, ramping up and down, loud under gaming load).
3. Propose concrete changes (curve points, source, hysteresis) with one sentence of reasoning each, then **wait for a yes** before writing.
4. Apply with `update_fan_curve`, `update_control_tuning`, `set_fan_automatic`. Change one thing at a time when debugging.
5. Observe: re-read the snapshot after 10–30 s and, ideally, after the user ran a real load (game, render, stress test for ~10 min).
6. Changes live only in memory. Call `save_config` only after the user is happy and agrees. Mention that otherwise a restart reverts everything.
7. Never leave a fan in manual mode by accident; hand it back with `set_fan_automatic` when a test is done.

## 2. Hard safety rules

- **Never** set CPU cooler fans, AIO/custom-loop pumps or radiator fans to 0 % or below their stop value.
- Pumps: constant 60–100 % (follow the pump vendor; many AIO pumps are meant to run at 100 %), or a curve on **liquid** temperature with a floor of ≥ 50 %. Never a zero-RPM curve.
- Every curve that cools a heat source must reach **100 % before the component's throttle point**:
  - AMD Ryzen 7000/9000: Tj max 95 °C. These CPUs boost until they hit 95 °C by design under all-core load; 85–95 °C under a stress test is normal, not a fault. Reach 100 % at ~85–90 °C.
  - Intel 12th–14th gen / Core Ultra: throttle at 100–105 °C. Reach 100 % at ~85–90 °C.
  - NVIDIA GPU core: target < 80 °C under load, 100 % at ~83–85 °C. Hot spot / junction may be 10–20 °C higher; memory junction (GDDR6X) up to ~95–100 °C is acceptable.
  - AMD GPU: junction/hot spot up to 110 °C is spec; prefer the edge/GPU temperature as source.
  - NVMe SSDs throttle around 70–80 °C; chipset ~80–90 °C.
- If a temperature is rising toward these limits and the related fans are not near 100 %, say so immediately.
- A temperature source that reads N/A, 0 or a constant value is broken: do not build a curve on it.
- Respect the plugin's `minimumManualPercent` safety floor; never try to work around a rejected write.

## 3. Choosing the temperature source (most important decision)

Pick the sensor of the heat the fan actually removes:

| Fan | Good source | Avoid |
|---|---|---|
| CPU air cooler | CPU package / Tctl/Tdie, **smoothed** (time average 5–10 s or response time 3–5 s) | Raw Tctl with no smoothing (Ryzen jumps 10–20 °C in a second) |
| AIO / custom loop radiator fans | **Liquid/coolant temperature** (AIO or loop sensor) | CPU temperature (it reacts in seconds, the water in minutes → constant ramping) |
| Pump | Liquid temperature or fixed speed | CPU temperature with low floor |
| GPU cooler fans (GPU control) | GPU core temperature | Hot spot as primary source |
| Case intake/exhaust | **Mix (Max)** of smoothed CPU and GPU temperatures, or GPU temp alone in gaming PCs | A single raw CPU sensor |
| Case fans with AIO in the case | Liquid temperature + GPU via Mix (Max) | — |

FanControl custom sensors help here:
- **Time Average** (`create_time_average_sensor`): smooths spiky sources. 5–10 s for CPU, 3–5 s for GPU.
- **Mix** sensor (Max / Average) of several temperatures: one source for case fans. Create in the FanControl UI.
- **Offset** sensor: shift a source.

## 4. Curve shape

Use a **graph** curve with 3–5 points (or a linear curve) shaped like this:

1. **Idle floor** – a flat section at the quietest speed where the fan still spins reliably (≥ the control's *stop* value + ~5 %). Typical 25–40 % for 120/140 mm fans. Keep it flat up to the idle temperature of the source plus a few degrees (CPU idle 40–50 °C → floor up to ~50–55 °C).
2. **Working ramp** – a gentle slope through the typical load range (e.g. CPU 55 → 75 °C: 35 → 60 %).
3. **Steep end** – climb to 100 % before the safety limit (e.g. 75 → 85 °C: 60 → 100 %).

Rules:
- Output must never decrease when the temperature increases (monotonic).
- Two-point curves are acceptable only for liquid-temperature curves (small temperature range, e.g. 28 °C → 30 %, 40 °C → 100 %).
- Liquid temperature curves: water sits ~5–10 °C above ambient at idle and should stay below ~40 °C (AIO limit ~60 °C). Ramp roughly from ambient + 3 °C to ambient + 15 °C.
- Prefer *slightly higher constant* speed over a fan that keeps changing speed – people notice change more than steady noise.
- Zero-RPM (0 %) only for case/GPU fans that the user explicitly wants silent at idle, and only if the fan restarts reliably (start value ≤ the first non-zero point). Never for CPU/pump/radiator.
- Similar fans that are audible together should run the same curve (or a Sync curve) to avoid beat/hum from slightly different RPMs.

### Starting templates (adjust to the user's hardware and ambient)

| Purpose | Source | Points (°C → %) |
|---|---|---|
| CPU air cooler, balanced | CPU time avg 7 s | 40→30, 55→35, 70→55, 80→80, 88→100 |
| CPU air cooler, silent | CPU time avg 10 s | 40→25, 60→30, 75→55, 85→85, 90→100 |
| AIO radiator fans | Liquid temp | 30→30, 34→40, 38→65, 42→100 |
| Case fans (gaming) | Mix(Max) CPU avg, GPU | 40→30, 55→35, 65→55, 75→80, 82→100 |
| GPU fans | GPU core | 45→30, 60→40, 70→60, 78→85, 84→100 |

## 5. Smoothing settings (per curve)

- **Hysteresis** 2–3 °C up and down (liquid curves: 0.5–1 °C). Stops ping-ponging around a point.
- **Response time** 3–5 s up, 5–10 s down for CPU sources; 1–2 s for liquid sources (they are already slow).
- **Ignore hysteresis at limits**: on, so 100 % is reached immediately at the top.
- If the source is already a time-average sensor, keep response times short (1–3 s) to avoid double delay.

## 6. Control tuning (per fan output)

- **Step up** 5–10 %/s, **step down** 2–5 %/s: speed up fast, slow down gently (less noticeable).
- **Start %**: the value needed to start a stopped fan; **Stop %**: the value below which it stalls. Get them from FanControl's calibration (pair the RPM sensor, then calibrate); do not guess low values.
- **Minimum %**: set it ≥ stop value for fans that must never stop.
- **Offset**: use sparingly to fine-tune one fan against a shared curve.
- Pair every control with its RPM sensor so stalls are visible.

## 7. Airflow sanity

- Slight positive pressure (intake a bit faster/more fans than exhaust) keeps dust down.
- Exhaust fans should not be faster than intakes at idle unless the case has many open vents.
- A top-front radiator as intake raises GPU/case temperature by a few degrees; mention it if GPU temps are high.

## 8. Diagnosing common complaints

| Symptom | Likely cause | Fix |
|---|---|---|
| Fans ramp up/down every few seconds | Raw spiky CPU source, no hysteresis, steep curve section near idle temp | Time-average source, hysteresis 2–3 °C, flatten the curve around idle temperature, slower step down |
| Fan clicks / stops and starts | Curve floor below stop value | Raise floor ≥ stop + 5 %, set minimum %, recalibrate |
| Loud at idle | Floor too high or source idles near ramp start | Lower floor (not below stop + 5 %), move ramp start above idle temperature |
| Hot under load, fans not at 100 % | Curve tops out too late / below 100 %, wrong source (e.g. case fans only on CPU while GPU heats the case) | End at 100 % before the safety limit, use Mix(Max) with GPU |
| AIO fans loud during short boosts | Radiator fans on CPU temperature | Switch source to liquid temperature |
| Two fans hum/beat | Same fan model at slightly different RPM | Same curve or Sync curve |

## 9. Profiles

Suggest separate FanControl configurations (Silent / Balanced / Performance) instead of one compromise.
`list_configs` / `load_config` switch between them; the plugin's virtual sensors ("MCP Virtual N") can act as an
assistant-controlled boost input inside a Mix (Max) curve.
