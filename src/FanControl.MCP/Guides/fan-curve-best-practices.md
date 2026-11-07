# FanControl fan curve best practices

Guide for an assistant that tunes FanControl through this MCP server. Read it before proposing or changing curves.
Goal order, always: **1. safe temperatures → 2. the user's goal (quiet / balanced / performance) → 3. stable, non-annoying behaviour**.
Sources and confidence levels: `docs/research/*.md` in the plugin repository (thermal limits, airflow, fans & noise, liquid cooling, FanControl practices).

## 1. Working agreement with the user

1. Look first: `get_snapshot`, `list_fan_curves`, `analyze_fan_setup` (and `detect_hardware`). Never change anything blind.
2. For a full setup use the guided wizard (`setup_wizard` → `save_system_profile` → `propose_baseline_config`). Use `setup_wizard_form` if the client supports forms.
3. Clarify the goal: quietest without performance loss, balanced, maximum cooling, silent first, or steady speeds; and what noise bothers them (ramping, idle hum, load noise, beating, whine).
4. Propose concrete changes with one sentence of reasoning each, then **wait for a yes** before writing.
5. Apply (`apply_baseline_config`, `update_fan_curve`, `update_control_tuning`, `set_fan_automatic`). A restore point is created by the baseline; create one yourself before larger experiments.
6. Validate under the user's real load: `start_measurement` (guard on) → change → `mark_phase` → `get_measurement_summary`. Compare clocks at similar load: lower clocks = performance loss. Re-run a baseline phase at the end to catch heat-soak drift.
7. Changes live in memory only. `save_config` only after the user agrees; otherwise a restart reverts everything.
8. Never leave a fan in manual mode by accident; hand it back with `set_fan_automatic`.

## 2. Hard safety rules

- **Never** 0 % or below the stall value for CPU cooler fans, pumps or radiator fans.
- **Pumps (AIO/loop): fixed speed, not a temperature curve.** NZXT and Arctic: 100 % (low speed shortens pump life). Others (e.g. Corsair): a fixed 70–80 % is acceptable (~1–2 °C cost). **Never below 60 %.** A 4-pin pump on a header in DC mode below 100 % can drop under its start voltage. Pump RPM 0/N/A → critical (but check temperatures: tach wiring can lie).
- Curves that cool a heat source must reach **100 % before the component's limit** (table below).
- Liquid temperature: warn at 50 °C, critical at 55 °C; 60 °C is the typical AIO/pump maximum.
- A temperature source that reads N/A, 0 or a constant value is broken; do not build a curve on it.
- Respect `minimumManualPercent`; never work around a rejected write.
- Only **one** program may control each fan: disable fan control in Afterburner, Precision X1, Armoury Crate, iCUE, NZXT CAM, L-Connect, SignalRGB for fans FanControl drives. A "?" on a FanControl control means something else sets that header.
- BIOS: disable "smart" fan modes on headers FanControl drives and leave a **sane fixed fallback (~50 %) or moderate BIOS curve**: it is the only protection when FanControl is closed, crashes or starts late.

### Thermal limits (curve end points)

"No loss below" is where temperature-driven clock loss stays under ~1 %. Boost algorithms lose clock **gradually**, not at a cliff.

| Component (sensor) | No loss below | 100 % fan by | Alarm | Notes |
|---|---|---|---|---|
| Ryzen 7000/9000 non-X3D (Tctl/Tdie) | ~80–82 °C | ~90 °C | 95 °C | Tjmax 95 °C, sitting there under all-core load is by design. Precision Boost loses ~8 MHz/°C above ~55 °C (<1 % per 5 °C). |
| Ryzen 9000 X3D | ~80 °C | ~88 °C | 95 °C | |
| Ryzen 7000 X3D / 5800X3D | ~75 °C | ~85 °C | 89–90 °C | lower Tjmax |
| Ryzen 5000 | ~75 °C | ~85 °C | 90 °C (5600X 95) | |
| Intel 12th–14th gen (Package/max core) | ~85 °C (i9 TVB: 70) | 85–90 °C | 100 °C | below TjMax mostly power-limited |
| Intel Core Ultra 200S | ~88 °C (TVB: 70) | 90–95 °C | 105 °C | |
| NVIDIA RTX 20–50 (GPU core, **not** hot spot) | ~80 °C | ~84 °C | 85–88 °C | 15 MHz bins drop with temperature; default target 83 °C. Measured: RTX 2070 lost its first bin at 83 °C. Usually **power-limited** before thermal: fans cannot fix a power limit. |
| AMD Radeon RX 6000–9000 (edge) | ~75 °C | ~82 °C | 90 °C | junction runs 15–25 °C higher, throttles at 110 °C; edge–junction gap > 30 °C = cooler problem |
| GDDR6X memory junction | < 95 °C | – | 105 °C | throttles ~110 °C |
| NVMe Gen4/Gen5 | < 70 °C | 70–75 °C (if a fan is dedicated) | 80 °C | |
| Liquid (AIO/loop) | – | ~45 °C (ambient + 13–18) | 50 / 55 / 60 °C | |

Ambient adds ~1:1 to component temperatures: tune with 8–10 °C summer headroom.

## 3. Choosing the temperature source (most important decision)

| Fan | Good source | Avoid |
|---|---|---|
| CPU air cooler | CPU Tctl/Package **smoothed**: Time Average 5–10 s, or hysteresis 2–4 °C + response 3–5 s | raw Tctl without smoothing (spikes 10–20 °C in < 1 s) |
| AIO / loop radiator fans | **liquid temperature** (CPU-to-liquid gap of 30–60 °C under load is normal) | CPU temperature (reacts in seconds, water in minutes → constant ramping) |
| Radiator fans, no liquid sensor | plugin exposing the coolant sensor (FanControl.CorsairLink, NzxtKraken, AquacomputerDevices, Liquidctl, HWInfo) or 10K thermistor on T_SENSOR; fallback: CPU Time Average 30–60 s + 3–5 °C hysteresis | raw CPU |
| Pump | fixed speed (see safety) | any curve with a low floor |
| GPU fans | GPU core temperature (smoothed 3–5 s) | hot spot as primary source |
| Case fans | **Mix (Max)** of smoothed CPU and GPU temperatures (GPU is usually the bigger heat source while gaming) | a single raw CPU sensor |

FanControl helpers: `create_time_average_sensor`, `create_mix_sensor` (via the baseline plan), Offset sensors (UI).

## 4. Curve shape

Graph curve with 3–6 points:

1. **Idle floor** – flat at the quietest speed the fan runs reliably: calibrated *stop* value + 2–5 points (typ. 20–35 % for 120/140 mm). Keep it flat up to the source's idle temperature + a few degrees, so idle wobble never crosses a ramp.
2. **Working ramp** – gentle slope through the typical load range.
3. **Steep end** – reach 100 % before the limit (table above).

Rules:
- Monotonic: speed never decreases with rising temperature.
- No steep segment near typical temperatures (> ~5 %/°C is audible as jumps).
- Liquid curves: ~idle liquid + 2 °C (28–30 °C) at 20–35 % → 100 % at ~45 °C; hysteresis 1–2 °C, response 1–3 s.
- Prefer a slightly higher **constant** speed over changing speed: fluctuating noise annoys about as much as steady noise ~5 dB louder.
- Noise ≈ 50·log10(RPM ratio): −20 % RPM ≈ −5 dB, half speed ≈ −15 dB, while airflow drops only linearly. Trimming the top of a curve is cheap.
- Similar fans at slightly different RPM beat/hum: give them the same curve, or offset one by ~50 RPM.
- **Zero-RPM** only for case fans and only with ≥ 5 °C between stop and start plus 10–30 s response time; if idle sits within 3 °C of the stop point, use a steady low speed instead. Never for CPU cooler, pump or radiator fans. 3-pin DC fans: only if calibration proves they restart.
- **NVIDIA GPU fans**: valid range is 30–100 %; 0 % hands control back to the card's own fan-stop. For fan-stop put 0 % at/below the card's stop temperature and jump straight to ≥ 30 %; never 1–29 %. Set start/stop to 30.
- **AMD GPU fans**: applying any FanControl fan setting resets Adrenalin overclock/undervolt; ADLX is unreliable in service mode and around sleep. Default to monitoring only unless the user accepts that.

### Starting templates (the planner computes these from the limits; adjust to ambient)

| Purpose | Source | Points (°C → %) |
|---|---|---|
| CPU air cooler, quiet w/o perf loss (Ryzen 9000) | CPU avg 7 s | 47→28, 62→36, 74→48, 79→61, 87→85, 90→100 |
| GPU (NVIDIA), quiet w/o perf loss | GPU avg 5 s | 45→32, 60→40, 72→52, 77→65, 81→85, 84→100 |
| Case fans on Max(CPU, GPU), quiet | Mix max | 45→20, 60→25, 72→35, 80→50, 86→75, 90→100 |
| AIO radiator fans | liquid | 26→30, 31→40, 37→60, 41→100 (ambient 22 °C) |
| Pump | – | flat 100 % (NZXT/Arctic) or 80 % (others) |

Validated example (Ryzen 5 9600X + RTX 2070, Battlefield 6): CPU fan −40 %, case fans −31…−39 %, GPU fan −11 % vs. a 70 %-capped config, full CPU/GPU clocks, no throttling.

## 5. Smoothing settings (FanControl semantics)

- **Hysteresis** is measured from the temperature of the last change, so the dead window is ±value. FanControl's author recommends 3–5 °C on CPU sources with response time ≥ 3 s; 2 °C on already smoothed sources; liquid 1–2 °C. For the GPU near its limit use ≤ 1 °C up (measured: 2 °C made the fan lag at 83 °C).
- **Response time**: CPU 3–5 s up, 5–10 s down; liquid 1–3 s; smoothed sources 1–3 s (avoid double delay).
- **Ignore hysteresis at limits**: on.
- Big hysteresis to fight ramping is a smell: use a Time Average source or a Trigger curve instead.

## 6. Control tuning (per fan output)

- **Step up** 5–10 %/s, **step down** 2–5 %/s: speed up fast, slow down gently.
- **Start/Stop %** from FanControl's calibration (pair the RPM sensor first). Wrong values make FanControl kick the fan to 100 % repeatedly.
- **Minimum %** ≥ stop value for fans that must never stop.
- **Offset**: fine-tune one fan against a shared curve (pressure balance, beat frequencies).
- Hubs/splitters: one curve, and only port 1 reports RPM: stalls on other ports are invisible; set the floor for the weakest fan.

## 7. Airflow

- **Pressure barely changes temperatures** (positive, negative, balanced within a few °C); choose it for dust: slightly positive with filtered intakes. With equal fan counts run intakes ~5–15 % faster (or exhausts −5 %).
- All-intake (no exhaust) is the worst layout; all-exhaust is clearly worse than balanced.
- Default: front intake, rear exhaust, top exhaust in the rear-most slot.
- **With an air cooler a top-front fan should be intake** (or off): as exhaust it pulls fresh front air straight out. Keep a rear/top-rear exhaust.
- **Bottom fans: intake only** – they help GPU temperatures a lot; bottom exhaust works against rising heat.
- Fan count: 1→2 fans ≈ −18 °C, 4→6 fans ≈ −2 °C. Beyond 4–5 fans fix placement, don't add fans.
- Mesh front beats a solid front by ~5–13 °C; a vertical GPU closer than ~30 mm to the glass runs ~6 °C hotter.
- Radiator: top exhaust is the balanced default; front intake is best for the CPU but costs the GPU ~6–7 °C. **Pump never the highest point**: no bottom radiator; front mount tubes-down preferred. Gurgling = air in the pump: run 100 % for hours, never lower pump speed to fix it.

## 8. Diagnosing common complaints

| Symptom | Likely cause | Fix |
|---|---|---|
| Fans ramp up/down every few seconds | raw CPU source, steep segment at idle temperature, BIOS smart mode fighting, bad start/stop | Time Average source, flatten curve around idle, BIOS fixed mode, recalibrate |
| Fan clicks / stops and starts | floor below stall value, zero-RPM without enough hysteresis | floor ≥ stop + 2–5 %, minimum %, ≥ 5 °C zero-RPM gap |
| Loud at idle | floor too high or idle temperature on the ramp | lower floor (not below stop + 5), move ramp start above idle |
| Hot under load, fans not at 100 % | curve tops out < 100 % or too late, wrong source (case fans ignore GPU) | 100 % before the limit, Mix(Max) with GPU |
| GPU clocks low although cool | power limit (nvidia-smi reason "power") | not a fan problem |
| AIO fans loud during short boosts | radiator fans on CPU temperature | liquid temperature source |
| Two fans hum/beat | same model at slightly different RPM | same curve or ~50 RPM offset |
| Fans 100 % after sleep / control shows "?" | another tool (Armoury Crate, vendor app) took the header | disable it for those fans |
| Manual mode cannot move a fan | header mode (PWM vs DC) wrong, hub without PWM | fix BIOS header mode; curves cannot fix this |

## 9. Profiles

Separate FanControl configurations (Silent / Balanced / Performance) beat one compromise; `list_configs` / `load_config` switch them.
The plugin's virtual sensors ("MCP Virtual N") can act as an assistant-controlled boost input inside a Mix (Max) curve.
