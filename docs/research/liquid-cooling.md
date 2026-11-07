# Liquid Cooling (AIOs and Custom Loops): Rules for Wizard and Analyzer

Scope: pump control, coolant temperature, radiator fan control, sensors in FanControl
(Rem0o), mounting, failure detection. Written for machine use: every rule has a number,
a source, and a confidence level.

Confidence levels:
- **High**: manufacturer spec or manual, or several independent sources agree.
- **Medium**: one manufacturer statement, or consistent community or reviewer consensus.
- **Low**: anecdotal, inferred by reasoning, or sources disagree.

Researched 2026-11. Some vendor pages (EKWB PDFs, NZXT support) blocked direct fetch, so
those points come from search snippets or retailer copies of the spec and are marked.

---

## 1. Key rules

### A. Pump speed

**R1. Default for every AIO pump: a fixed high speed. Use 100% unless the vendor says
otherwise; 70-80% fixed is an acceptable quiet option.**
- NZXT says not to run the pump below 100% for long periods (Kraken 120, M22, Core),
  because it shortens pump life. Set 100% in BIOS when there is no CAM control.
  [NZXT support: Kraken fan control](https://support.nzxt.com/hc/en-us/articles/360057023314-How-do-I-control-the-fan-speed-on-my-Kraken-cooler)
  (seen via search snippet; page returns 403 to fetch).
- Arctic Liquid Freezer III manuals: "100% or a high fixed speed for optimal coolant
  circulation". Connect to CPU_FAN or AIO_PUMP.
  [manuals.plus copy, LF III Pro](https://manuals.plus/asin/B0DPHRVNGL)
- Corsair suggests a fixed 70-80%. Going from 100% to about 80% costs about 1-2 °C CPU
  at extreme load, "if that".
  [Corsair: pump speed vs fan speed](https://www.corsair.com/us/en/explorer/diy-builder/cpu-coolers/pump-speed-vs-fan-speed-which-makes-the-biggest-difference-for-aio-performance/)
- Wizard default: 100% fixed. Offer 70-80% fixed as "quiet" only if the user complains
  about pump noise and the vendor is not NZXT.
- Confidence: **High** that the pump should be fixed and high. **Medium** for the exact
  70-80% quiet value.

**R2. Do not put the pump on a temperature curve that reaches low duty. If a curve is
used, floor it at 60% or more (AIO) and make it flat above about 50 °C CPU or 35 °C
liquid.**
- A pump curve saves only a little noise at idle. The risk is a pump left slow when load
  arrives. Fan speed decides heat rejection far more than pump speed (Corsair test: fans
  25% to "Extreme" dropped coolant about 10 °C; pump 80% to 100% changed CPU about 1-2 °C).
  [Corsair](https://www.corsair.com/us/en/explorer/diy-builder/cpu-coolers/pump-speed-vs-fan-speed-which-makes-the-biggest-difference-for-aio-performance/)
- Anecdote: an ML240L at 50% pump throttled (100 °C) where 100% held 93 °C under Prime95.
  [LTT forum](https://linustechtips.com/topic/1349894-unable-to-set-full-pump-speed-on-my-aio/)
- Confidence: **Medium** (the 60% floor is a conservative engineering choice, not a
  vendor number).

**R3. Pump floors by type (minimum duty or RPM the analyzer accepts):**

| Pump type | Typical max | Floor to enforce | Notes | Source / confidence |
|---|---|---|---|---|
| Asetek Gen 6/7 (many Corsair Hydro, NZXT X-series, Fractal, EVGA) | about 2800-3000 rpm | 60% duty, 100% recommended | Runs at full speed when no PWM signal is present. Rated 12 V; minimum start voltage 7.5 V. | [Asetek 6-Series SI spec](https://sliger-part-documents.s3.amazonaws.com/ASETEK_Standard_AIO_Product_Specification.pdf), **High** for spec, **Medium** for floor |
| Asetek Gen 8 / later (NZXT Kraken 2023, Corsair later Hydro, others) | about 2800-3200 rpm (NZXT reports about 3200 rpm as typical healthy) | 60% duty, 100% recommended | | [NZXT: is my AIO failing](https://nzxt.com/blogs/news/is-my-aio-cooler-failing), **Medium** |
| CoolIT (Corsair H80i-H110i GT, some others) | varies | 60% duty | Corsair plugin exposes CoolIT units; no public CoolIT floor found. | [CorsairLink plugin](https://github.com/EvanMulawski/FanControl.CorsairLink), **Low** |
| Corsair iCUE LINK and Commander Core pumps | about 2800-3000 rpm | 50% hard floor in the CorsairLink plugin | The plugin floors pump power at 50% to avoid the device "pump failure" state and low-RPM resonance. Default 100%. | [CorsairLink README](https://github.com/EvanMulawski/FanControl.CorsairLink), **High** |
| Corsair Hydro Platinum | 3 presets | n/a | Plugin maps 0-33% Quiet, 34-67% Balanced, 68-100% Performance. Use 68% or more ("Performance") for heavy loads. | CorsairLink README, **High** |
| Arctic Liquid Freezer II/III (own PWM pump) | 800-2800 rpm (240: 800-2000 on some listings) | 60% duty; manual recommends 100% or high fixed | The pump and fans often share one cable (LF II), so one PWM signal drives both. | [LF III Pro manual copy](https://manuals.plus/asin/B0DPHRVNGL), retailer specs, **Medium** |
| be quiet! Pure Loop 2 | 4000-5500 rpm, 4-pin PWM | 60% duty | Connect to AIO_PUMP or CPU_OPT. | [be quiet! Pure Loop 2 120](https://bequiet.com/en/watercooler/4397) via retailer copies, **Medium** |
| be quiet! Silent Loop 2 | 2800 rpm, **3-pin DC** | 9 V minimum (75% of 12 V) | The manual sets a DC range of 9-12 V. A PWM-mode header drives a 3-pin pump at 12 V. | [KitGuru Silent Loop 2 review](https://www.kitguru.net/components/cooling/luke-hill/be-quiet-silent-loop-2-240mm-aio-cooler-review/all/1), **High** |
| Lian Li Galahad II | up to 3800-4200 rpm by model | 60% duty | No vendor floor found. | Manual mirrors, **Low** |
| D5 (EK, Aquacomputer, Alphacool, Laing) | about 4700-4800 rpm | 20% PWM is the spec bottom. Aquacomputer floors at about 1800-2000 rpm. For low noise, keep at or above about 40% / 2000-2500 rpm. | EK D5 G3 PWM: duty range 20-100%, max coolant 60 °C. Aquacomputer staff: all their D5s have a minimum of about 2000 rpm by design. | [Scan: EK D5 G3 spec](https://www.scan.co.uk/products/ek-loop-d5-g3-pwm-pump-motor-1500l-h-ceramic-bearing-12v-sata-powered), [Aquacomputer forum](https://forum.aquacomputer.de/weitere-foren/english-forum/112864-d5-next-lowest-rpm/), **Medium** |
| DDC (Laing 3.1/3.2/4.2, Alphacool DDCzero, EK DDC) | 3300-4800 rpm, depends on model | 40-50% duty; never low with poor airflow | A DDC heats itself and its coolant more than a D5. Model names such as "DDCzero 3300-4800rpm" imply a 3300 rpm floor under PWM. | [Alphacool DDCzero datasheet](https://download.alphacool.com/datasheet/ENG_13867_Alphacool_Core_DDCzero_3300-4800rpm_PWM_Pump_datasheet.pdf), **Low/Medium** |

**R4. Custom loops (D5/DDC) may use a curve on liquid temperature, never on CPU
temperature.** Typical: D5 about 40-50% at liquid ≤ 30 °C, rising to 70-100% at
liquid 40 °C or above. Flow in a typical loop plateaus well before 100%, so the curve is
mostly about noise. Confidence: **Medium** (community practice, consistent with the
Aquacomputer floor rationale "below about 1800 rpm flow is too low").

**R5. Header and BIOS settings for the pump.**
- Use AIO_PUMP, W_PUMP+, PUMP_FAN or CPU_FAN. These are rated for higher current (often
  2-3 A) than SYS_FAN (often 1 A). NZXT warns not to connect AIO pumps to its fan
  controller because of the over-current risk.
  [NZXT support](https://support.nzxt.com/hc/en-us/articles/360057023314-How-do-I-control-the-fan-speed-on-my-Kraken-cooler) (snippet).
  Confidence: **High**.
- Many boards lock AIO_PUMP and W_PUMP to 100% by default. CPU_FAN gets a CPU-temperature
  curve by default, so a pump on CPU_FAN may run slow at idle.
  [darkFlash build guide](https://www.darkflash.com/article/000000000000593),
  [Microcenter community](https://community.microcenter.com/discussion/2774/powerspec-011d-build-best-header-for-pump).
  Confidence: **Medium**.
- 4-pin PWM pump: set the header to PWM mode. 3-pin pump: set DC mode and Full Speed.
  A 3-pin pump on a header in PWM mode always runs at 12 V, which is safe but
  uncontrolled.
  [ASRock forum](https://forum.asrock.com/water-pump-header-x370_topic4576.html).
  Confidence: **High**.
- **Danger: a 4-pin PWM pump on a header in DC mode at a low percentage can starve the
  pump of voltage.** Asetek's minimum start voltage is 7.5 V
  ([Asetek spec](https://sliger-part-documents.s3.amazonaws.com/ASETEK_Standard_AIO_Product_Specification.pdf)),
  so a DC duty below about 60% may stall it or stop it restarting. Confidence: **High**
  (from the spec).
- If the pump is not on CPU_FAN, the board may raise a "CPU fan error". Set CPU_FAN to
  "Ignore" or connect a radiator fan there. Ignore only silences the error; it does not
  change control.
  [Microcenter](https://community.microcenter.com/discussion/2774/powerspec-011d-build-best-header-for-pump).
  Confidence: **High**.
- AIOs with USB control (iCUE LINK, Commander Core, Kraken 2023/Elite, Hydro Platinum)
  drive the pump over USB. The SATA or USB cable powers it, and the motherboard header
  only reads tach, if it is connected at all. FanControl can control these only through
  a plugin (section C). Confidence: **High**.
- Asetek: with no PWM signal the pump runs at full speed. If FanControl cannot see the
  pump header, the safe fallback is "leave it uncontrolled at BIOS 100%".
  Confidence: **High**.

**R6. Pump RPM readings may be scaled.** Asetek's spec defines tach as 4 pulses per
revolution (RPM = f × 60/4), while motherboards assume 2. Displayed pump RPM can therefore
read about 2× the impeller speed, depending on the cooler. The analyzer should judge pump
health **against the pump's own baseline**, not against an absolute RPM.
[Asetek spec](https://sliger-part-documents.s3.amazonaws.com/ASETEK_Standard_AIO_Product_Specification.pdf).
Confidence: **Medium** (the spec is clear; how each vendor and board reports varies).

**R7. Pump noise vs performance.** Pump whine is tonal and annoys at idle more than its
loudness suggests. If the user wants quiet, lower the pump to a **fixed** 70-80% (not a
curve). Remove air first, because gurgling is not a speed problem (R19). Some low pump
speeds cause resonance; CorsairLink floors LINK pumps at 50% partly for this reason.
Confidence: **Medium**.

### B. Liquid (coolant) temperature

**R8. Radiator fans should follow liquid temperature, not CPU temperature.** Coolant has
large thermal mass and changes over minutes, while CPU temperature jumps second to second.
Heat leaves only through the radiator, and its rate depends on coolant-air delta and fan
speed. Fans that follow CPU temperature ramp audibly on every burst without moving any
more heat. NZXT recommends liquid temperature for Kraken radiator fans.
[NZXT: CAM fan curve](https://nzxt.com/blogs/news/nzxt-cam-fan-curve);
Corsair also recommends coolant-based fan control.
[Corsair](https://www.corsair.com/us/en/explorer/diy-builder/cpu-coolers/pump-speed-vs-fan-speed-which-makes-the-biggest-difference-for-aio-performance/).
Confidence: **High**.

**R9. Typical coolant values (closed case, ambient 20-25 °C):**
- Idle: ambient +2 to +6 °C, so about 25-32 °C.
- Gaming or sustained mixed load: ambient +5 to +12 °C, so about 30-38 °C.
- Heavy all-core load, quiet fans: ambient +10 to +20 °C, so about 35-45 °C. Corsair
  measured 49 °C with fans at 25% under full load and called it "quite hot".
  [Corsair](https://www.corsair.com/us/en/explorer/diy-builder/cpu-coolers/pump-speed-vs-fan-speed-which-makes-the-biggest-difference-for-aio-performance/).
- Custom loop target: delta 10 °C over ambient under load is the common target; 10-15 °C
  is normal, and about 15 °C is the usual upper comfort limit.
  [Tom's Hardware: fluid temp sensor](https://www.tomshardware.com/news/installing-a-fluid-temperature-sensor-the-best-dollar10-upgrade-for-a-custom-loop),
  [HardForum](https://hardforum.com/threads/what-max-coolant-temp-should-i-be-targeting.1994676/post-1044543111).
- Confidence: **Medium** for the ranges, **High** for the 10 °C delta target in custom
  loops.

**R10. Maximum safe coolant temperature: 60 °C.**
- Asetek AIO specs: working liquid 2-60 °C (6-Series, 8-Series, 645LT); pump local ambient
  2-50 °C.
  [Asetek 6-Series spec](https://sliger-part-documents.s3.amazonaws.com/ASETEK_Standard_AIO_Product_Specification.pdf),
  [Asetek 645LT data sheet](https://www.asetek.com/wp-content/uploads/2021/07/645lt_data_sheet.pdf).
- EK D5 G3: maximum liquid 60 °C (retailer copy of the EK spec).
  [Scan](https://www.scan.co.uk/products/ek-loop-d5-g3-pwm-pump-motor-1500l-h-ceramic-bearing-12v-sata-powered).
- Acrylic tops, soft PVC tubing and PETG hardline in custom loops are also commonly
  limited to about 60 °C (PETG softens near 70-80 °C).
- Analyzer thresholds: **warn at 50 °C, critical at 55 °C, emergency at 60 °C or above**.
  At critical, force radiator fans to 100% and the pump to 100%.
- Confidence: **High** for the 60 °C limit. The 50/55 margins are engineering choices.

**R11. Radiator fan curve on liquid temperature, starting values (AIO, ambient about
22 °C):**

| Liquid °C | Fan % (120/140 mm static-pressure fans) |
|---|---|
| ≤ 28-30 | minimum audible-safe floor, 20-35% (fans must keep spinning; see R14) |
| 33 | about 40% |
| 36-38 | about 55-65% |
| 40-42 | about 80% |
| ≥ 45 | 100% |

- Rationale: the curve should reach 100% before coolant gets near 50 °C (R10). Liquid at
  idle sits a few degrees above ambient, so the curve start must sit above idle liquid or
  the fans never settle.
- **Adapt to ambient:** set the start point = measured idle liquid + 2 °C, and full speed
  = start + 12-15 °C.
- Custom loops with a lot of radiator area can use a flatter curve: about 30% at
  delta ≤ 3 °C, rising to 100% at delta 12-15 °C. This matches a reported working curve
  of 27% PWM at Δ1.5 °C up to 85% at Δ15 °C, settling at Δ9-10 °C at about 60%.
  [Aquacomputer / HardForum threads](https://forum.aquacomputer.de/weitere-foren/english-forum/p1390911-maximum-fan-output-selection-for-controller/).
- Confidence: **Medium**.

**R12. Hysteresis and timing for liquid-temperature curves in FanControl.**
- Liquid moves slowly, so it needs only a little smoothing: Hysteresis 1-2 °C, Response
  time 1-3 s, Step up 2-5 %/s, Step down 1-2 %/s. Turn on "Ignore hysteresis at minimum
  and maximum temps" so that 100% engages immediately at the top point.
- Avoid large hysteresis (≥ 3 °C) on liquid. A 3 °C swing in coolant is already a big load
  change, and the fans would lag badly.
- Feature names and semantics are from the
  [FanControl docs](https://getfancontrol.com/docs/) (Hysteresis, Response time,
  Step up/down %, Ignore hysteresis at min/max).
- Confidence: **Medium** (values from engineering judgment; features verified).

**R13. Coolant-to-CPU offset is normal and large.** Under load, CPU minus liquid of
30-60 °C is normal for modern high-power CPUs, because the bottleneck is die-to-coldplate
transfer. A hot CPU with cool liquid is **not** a fan problem; more radiator fan speed
will barely help. The analyzer must not raise fans in response to a CPU-liquid gap.
Confidence: **Medium/High** (physics; consistent with Corsair's statement that fans
dominate liquid temperature but not die temperature).

### C. When FanControl has no liquid sensor

**R14. Order of preference for the radiator-fan source sensor:**
1. **A native liquid sensor in FanControl** (LibreHardwareMonitor or a plugin), e.g.
   "Liquid temperature", "Coolant", "Water".
2. **A plugin that reads the AIO over USB**:
   - **FanControl.CorsairLink** ([GitHub](https://github.com/EvanMulawski/FanControl.CorsairLink)):
     iCUE LINK Hub and devices (H-series LINK, TITAN), Commander Core / Core XT / Pro,
     Hydro Platinum, Hydro Asetek and Asetek Pro (these need the SiUsbXpress driver), and
     CoolIT Hydro. It exposes liquid temperature on many AIOs, plus pump and fan RPM and
     control. **Requires iCUE and the Corsair Service to be stopped.** Pick the net48 or
     net8.0 build to match FanControl. Unblock the zip and copy it into
     `Plugins`. Pump minimum is 50% (override with env var
     `FANCONTROL_CORSAIRLINK_MIN_PUMP_DUTY`). Hydro Asetek Pro has a built-in safety
     profile (100% at 58 °C liquid).
   - **FanControl.NzxtKraken** ([GitHub](https://github.com/brokenmass/FanControl.NzxtKraken)):
     Kraken X2 (X42-X72), X3 (X53-X73), Z3 (Z53-Z73), Kraken 2023 and Elite. Close NZXT
     CAM (or stop it controlling the device); HWiNFO users report clean readings only
     with CAM closed.
     [HWiNFO forum](https://hwinfo.com/forum/threads/nzxt-kraken-x53-not-showing-up.6285/).
   - **AquacomputerDevices** (HighFlow NEXT, Quadro, Octo; check its README for D5 NEXT,
     Aquaero, leakshield). **FanControl.Liquidctl**
     ([sp00n releases](https://github.com/sp00n/FanControl.Liquidctl/releases)) is an
     early build that wraps liquidctl's device list.
   - **FanControl.HWInfo** (official, Rem0o) can import liquid sensors from HWiNFO when
     HWiNFO supports the AIO but LHM does not.
     [Plugins wiki](https://github.com/Rem0o/FanControl.Releases/wiki/Plugins).
3. **A custom-loop inline sensor** (10K thermistor) on a motherboard T_SENSOR or
   EXT_TEMP header, or on a controller. Tom's Hardware calls it "the best $10 upgrade for
   a custom loop".
   [Tom's Hardware](https://www.tomshardware.com/news/installing-a-fluid-temperature-sensor-the-best-dollar10-upgrade-for-a-custom-loop).
   A Corsair user put a Commander probe in the radiator exhaust air as a proxy for an AIO
   with no probe.
   [CorsairLink discussion](https://github.com/EvanMulawski/FanControl.CorsairLink/discussions/128).
4. **Fallback: a CPU temperature proxy.** Use a FanControl **Time Average** custom sensor
   on CPU Package (or Tctl/Tdie) with Time = **30-60 s**, then a curve with Hysteresis
   **3-5 °C**, Response time **3-5 s**, Step down **1 %/s**. Shift the curve up about
   20-30 °C compared with a liquid curve, e.g. 35% at ≤ 55 °C up to 100% at ≥ 85 °C.
   This copies the slow liquid response. Confidence: **Medium** (the Time Average feature
   is from the [FanControl docs](https://getfancontrol.com/docs/); values are engineering
   choices).
5. **Optional delta-T:** a **Mix Sensor** with Function = Subtract (liquid − ambient)
   when a trustworthy ambient sensor exists. It lets the curve ignore room-temperature
   swings. Ambient must come from a probe in intake air, not a motherboard sensor near the
   VRM. Confidence: **Medium**
   ([Aquacomputer forum discussion](https://forum.aquacomputer.de/weitere-foren/english-forum/p1390945-maximum-fan-output-selection-for-controller/)).

**R15. Vendor software conflicts.** Only one program may talk to a USB AIO. iCUE with
CorsairLink, CAM with the NZXT plugin, or L-Connect with any USB plugin gives flapping
readings, ignored commands or firmware fallback to defaults. If the vendor software is
closed and the plugin is absent, a CAM-powered Kraken drops to fixed default speeds (pump
fixed, about 80% on Kraken Plus).
[NZXT: do you need CAM running](https://nzxt.com/blogs/news/do-you-need-cam-running).
The wizard must ask which program stays in charge. Confidence: **High**.

### D. Radiator fans, orientation and mounting

**R16. The pump must not be the highest point of the loop.** The radiator's top must sit
above the pump head. AIOs contain a small air pocket by design, and it should collect in
the radiator, not the pump.
- NZXT: "the highest point of the radiator should always sit above the pump cap". Bottom
  mounting is not recommended in any configuration (noise, higher temperatures, shorter
  life).
  [NZXT: radiator mounting guide](https://nzxt.com/blogs/news/aio-radiator-mounting-guide).
- GamersNexus' orientation video: top mount = good; front mount with tubes down = good;
  bottom mount with tubes up = bad. GN later clarified that the key point is that the pump
  must not be the highest point.
  [GN video chapters](https://www.letsplayindex.com/video/stop-doing-it-wrong-how-to-kill-your-cpu-cooler-aio-mounting-BbGomv195sk),
  [OCUK discussion](https://forums.overclockers.co.uk/threads/gamers-nexus-video-on-improper-aio-installation.18896548/page-2).
- Front mount with tubes up is acceptable when the radiator top is above the pump. Tubes
  at the bottom tend to be quieter. Orientation barely changes temperatures.
  [NZXT](https://nzxt.com/blogs/news/aio-radiator-mounting-guide).
- Confidence: **High**.

**R17. Placement priorities (NZXT).** Top exhaust is the first choice. Front (intake or
exhaust) is fine; front intake helps the CPU slightly but warms the GPU and case air.
Rear only suits 120/140 mm radiators. Radiator size and fans matter more than top vs
front. [NZXT](https://nzxt.com/blogs/news/aio-radiator-mounting-guide). Confidence:
**High**.
- Analyzer impact: **front-intake radiator** means case air temperature tracks liquid
  temperature, so case and GPU fans may follow liquid temperature as well. **Top-exhaust
  radiator** means radiator fans also act as case exhaust; their floor should not fall
  below about 25-30% while the GPU is loaded.

**R18. Fans and thickness.**
- Use static-pressure-optimised fans on radiators (dense fin stacks, e.g. 20+ FPI on
  AIOs). Airflow fans lose more performance on radiators than in open air. Confidence:
  **Medium** (widely reported by reviewers; no single primary source captured).
- Push/pull on standard 25-30 mm radiators usually gains only about 1-3 °C at equal RPM.
  It pays off mainly on thick (≥ 45-60 mm) or high-FPI radiators, or lets you run lower
  RPM for the same temperature. All fans on one radiator must share a single curve or
  control (same model, same speed); never mix one fast and one slow. Confidence: **Medium**.
- Arctic LF II/III (and others) run fans and pump on one daisy-chained cable. A single
  PWM signal then drives all of them, so a fan curve also drives the pump. **Separate them
  or keep the floor ≥ 60%**, or follow Arctic's recommendation of a high fixed speed
  ([LF III manual copy](https://manuals.plus/asin/B0DPHRVNGL)). Arctic LF II also has a
  small VRM fan on the pump head. Confidence: **Medium**.
- Radiator fans should not stop (0 RPM) while the pump runs: passive radiators work, but
  coolant keeps creeping up under any load. A zero-RPM floor is allowed only with liquid
  ≤ ambient + 5 °C, a start point well above idle liquid, and hysteresis ≥ 2 °C.
  Confidence: **Medium**.

**R19. Air in the loop.** Gurgling, clicking or grinding from the pump usually means air
in the pump cap. Fix: confirm the pump is below the radiator top. Then, with the pump head
held below the radiator, shake it for 30-60 s and run the pump at 100% for at least 4 h.
Repeat once. If the noise remains, replace the cooler. High-pitched electrical whine is
coil whine and not a fault.
[NZXT: is my AIO failing](https://nzxt.com/blogs/news/is-my-aio-cooler-failing).
Confidence: **High**. Analyzer action: when a user reports gurgling, suggest pump 100%
for 4 h and an orientation check. Never lower the pump to "fix" noise caused by air.

### E. Failure detection (analyzer alarms)

**R20. Alarm table:**

| Signal | Condition | Severity | Action |
|---|---|---|---|
| Pump RPM | 0 or N/A while the system is on (sensor was non-zero before) | **Critical** | Set pump to 100%, fans to 100%, notify. A 0 reading can also be a tach-only fault (Corsair report: pump worked, iCUE showed 0), so check temperatures before declaring failure. [NZXT](https://nzxt.com/blogs/news/is-my-aio-cooler-failing), [Corsair forum](https://forum.corsair.com/forums/topic/177049-h100i-elite-capellix-working-0rpm-pump-failure/) |
| Pump RPM | Below 70% of its own baseline at the same duty, or swinging > ±10% while duty is constant | Warning | NZXT: about ±10% variance is normal. More suggests air or a failing bearing. |
| Pump duty | Commanded below the per-type floor (R3), or curve reaches 0% / has Stop % > 0 | Error (config) | Fix the curve. |
| CPU idle temp | > 70 °C at idle on a liquid cooler (> 75 °C suggests a blockage); idle normal is about 30-40 °C | **Critical** | Suspect pump or mounting. [NZXT](https://nzxt.com/blogs/news/is-my-aio-cooler-failing) |
| CPU rise rate | CPU climbs to Tjmax within seconds of load while liquid temperature stays flat or falls | **Critical** | Classic dead-pump signature: heat is not reaching the radiator. **Low/Medium** (inferred). |
| Liquid temp | ≥ 50 °C warn; ≥ 55 °C critical; ≥ 60 °C emergency | Warning → Critical | Fans and pump to 100%. [Asetek spec](https://sliger-part-documents.s3.amazonaws.com/ASETEK_Standard_AIO_Product_Specification.pdf) |
| Liquid − ambient | > 15 °C sustained (custom loop) or > 20 °C (AIO) with fans at 100% | Warning | Radiator clogged, too small, or fans not spinning. |
| Liquid temp at idle | > ambient + 10 °C after 10 min idle | Warning | Fans stopped, pump slow, or radiator choked. |
| Inlet vs outlet | Large difference between the two tubes (if two sensors exist) | Warning | NZXT: a large difference suggests a blockage. |
| Radiator fan RPM | 0 while commanded > 30% | Warning | Fan or header fault. |
| Gradual drift | CPU load temperature at same load and ambient rises > 5 °C over weeks | Info | Permeation or coolant loss (Asetek rates permeation life at 50,000 h), dust, or paste. |

**R21. Do not "learn" a dead pump as baseline.** Record pump RPM and liquid temperature
baselines only while the pump reads ≥ 50% of nameplate and liquid ≤ ambient + 10 °C at
idle. Confidence: **Low** (design guidance).

---

## 2. Anti-patterns (the analyzer should flag these)

1. **Pump on a zero-RPM curve**, i.e. curve reaches 0%, or Stop % > 0 on the pump control.
   Pumps must never stop. → Error.
2. **Pump on CPU temperature with a low floor** (< 60% AIO, < 40% D5, < 50% DDC). → Error.
   Even with a floor, a pump on raw CPU temperature makes pump noise come and go; use a
   fixed speed.
3. **Pump on a SYS_FAN header** (current rating often 1 A) or on an NZXT or other fan
   controller hub channel. → Warning.
4. **4-pin PWM pump with the header in DC mode at < 100%.** It can drop below the 7.5 V
   start voltage. → Error.
5. **3-pin DC pump (e.g. Silent Loop 2) driven below 9 V / 75%.** → Error.
6. **Radiator fans on raw CPU temperature** (no Time Average, hysteresis < 3 °C). Causes
   audible bursts with no cooling benefit. → Warning; suggest a liquid sensor or a Time
   Average of 30-60 s.
7. **Liquid curve that never reaches 100% before 50 °C**, or whose start point is below
   idle liquid temperature (fans never settle). → Warning.
8. **Large hysteresis on liquid temperature (≥ 3 °C) or long response time (> 10 s)**.
   Double smoothing of an already slow signal. → Info.
9. **Mixed controls on one radiator** (push and pull fans on different curves or models).
   → Warning.
10. **Pump and fans on one cable (Arctic LF) controlled by an aggressive fan curve with a
    low floor**, which also slows the pump. → Warning.
11. **Bottom-mounted radiator or pump at the highest point** (from wizard answers). →
    Warning, with an orientation explanation.
12. **Two programs controlling the same USB AIO** (iCUE/CAM/L-Connect plus a plugin). →
    Error.
13. **Lowering pump speed to fix gurgling.** Air needs high speed and re-orientation. →
    Advice.
14. **GPU or case fans following CPU temperature in a front-intake radiator build**,
    while the real heat driver for case air is the liquid. → Info.
15. **Using motherboard "System" temperature as ambient** for a delta-T curve. It sits
    near the VRM or chipset and reads high. → Info.
16. **Radiator fans with zero-RPM below the idle liquid temperature** (stop-start
    cycling). → Warning.
17. **Pump at 100% on a curve that drops at night or idle via a "quiet profile" switch**,
    when the profile does not keep the pump fixed. → Warning.

---

## 3. Wizard questions (ask in this order)

1. **Cooling type:** air / AIO / custom loop / hybrid (e.g. AIO plus GPU custom block).
2. **AIO model and size** (free text plus a picker): brand, model, radiator 120/240/280/
   360/420, thickness (standard 27-30 mm or thick 38+ mm). Use this to map to the pump
   type table (R3) and plugin availability (R14).
3. **USB or software-controlled AIO?** (iCUE LINK, Commander, Kraken 2023/Elite, Hydro
   Platinum, Lian Li L-Connect, …). If yes: **which program should stay in control**,
   FanControl via a plugin or the vendor app? (R15)
4. **Pump connection:** which header (AIO_PUMP, W_PUMP+, PUMP_FAN, CPU_FAN, CPU_OPT,
   SYS_FAN, USB/SATA only, fan hub)? 3-pin or 4-pin? Pump and fans on one cable (Arctic)?
5. **BIOS pump header mode:** PWM / DC / Auto / Full speed, and whether the BIOS already
   fixes it at 100%. Offer to leave the pump under BIOS control at 100% if FanControl
   cannot see it.
6. **Does FanControl show a liquid or coolant temperature?** Present detected candidates
   (names containing liquid, coolant, water, "T_Sensor", "EXT"). If none: offer plugin
   install guidance, then the Time Average fallback (R14).
7. **Radiator location and airflow:** top / front / side / rear / bottom; intake or
   exhaust; tubes up or down; is the pump head below the radiator's top edge? (R16, R17)
8. **Radiator fans:** model, count, push / pull / push-pull, connected to which header or
   hub; all fans on one control?
9. **Noise preference:** silent / balanced / performance. This sets pump fixed 70-80% vs
   100%, the curve floor and the curve slope.
10. **Ambient:** typical room temperature, and is there an intake or ambient probe? (for
    delta-T)
11. **Custom loop only:** pump type (D5/DDC/other) and how many; reservoir position;
    radiators (count, size, thickness); blocks (CPU, GPU, other); flow sensor present;
    coolant sensors (how many, where); tubing material (soft PVC / PETG / acrylic) for
    temperature limits; controller (Aquacomputer, EK Loop Connect, Corsair, motherboard).
12. **Symptoms now:** gurgling or clicking, pump RPM 0, high idle temperatures, fan
    pulsing. Route to the failure or air checklist before tuning.

---

## 4. Open questions and conflicts

- **100% vs reduced pump speed for lifespan.** NZXT says sustained low speed shortens
  pump life and recommends 100%. Corsair recommends a fixed 70-80%. A forum report says
  Arctic recommends CPU_FAN because constant full speed is bad for the pump
  ([ROG forum](https://rog-forum.asus.com/t5/z170/cpu-fan-vs-w-pump-for-aio-water-cooler/td-p/579210),
  unverified, and it contradicts the Arctic manual's "100% or high fixed"). No vendor
  publishes bearing-life vs RPM data. Resolution: default 100% for NZXT and Arctic, allow
  70-80% fixed elsewhere, never below 60%.
- **Microbubbles at high pump speed.** Digital Trends says high speed can create
  microbubbles in AIOs; JayzTwoCents (via Digital Trends) found a constant 80% best
  overall. Not confirmed by a vendor.
  [Digital Trends](https://qa2.www.digitaltrends.com/computing/you-might-be-cooling-your-pc-the-wrong-way-heres-how-to-fix-it).
- **Exact minimum safe duty for Asetek, CoolIT and Lian Li pumps** is not published. The
  60% floor is conservative, derived from Asetek's 7.5 V start voltage (62.5% of 12 V)
  and the CorsairLink 50% floor.
- **DDC minimum PWM.** Alphacool forum questions about the PWM range (0-100% vs about
  20-70%) were unanswered in the sources found. Datasheet names imply a 3300 rpm floor for
  DDCzero.
- **Tach scaling on Asetek pumps** (4 pulses per revolution) means absolute RPM thresholds
  are unreliable across boards and software; use relative baselines.
- **Whether LibreHardwareMonitor natively reads specific AIOs** (Kraken X3/Z3/2023,
  Corsair Commander Core) changes with LHM versions; the NzxtKraken plugin exists because
  an LHM PR was pending. The analyzer should detect at runtime, not assume.
- **AquacomputerDevices plugin coverage** of D5 NEXT, Aquaero and Leakshield is not
  confirmed in the sources found; check its README.
- **GamersNexus primary article** was not retrievable (404); the orientation rules rely on
  NZXT's guide plus GN video chapter titles and forum summaries of GN's clarification.
- **Push/pull gain numbers** (1-3 °C) are reviewer consensus without a single primary test
  captured here.
- **Max temperature of non-Asetek AIOs** (CoolIT, Arctic, Lian Li, be quiet!) is assumed
  at 60 °C by analogy; no vendor number was found.

---

## 5. Sources

Manufacturer and spec documents:
- Asetek 6-Series SI AIO product specification (liquid 2-60 °C, pump ambient 2-50 °C,
  7.5 V min start, full speed without PWM, tach 4 pulses/rev, 50,000 h permeation):
  https://sliger-part-documents.s3.amazonaws.com/ASETEK_Standard_AIO_Product_Specification.pdf
- Asetek 645LT data sheet (60 °C max liquid): https://www.asetek.com/wp-content/uploads/2021/07/645lt_data_sheet.pdf
- Asetek Threadripper (8-Series) spec: https://sliger-part-documents.s3.amazonaws.com/ASETEK_Threadripper_AIO_Product_Specification.pdf
- Corsair, pump speed vs fan speed: https://www.corsair.com/us/en/explorer/diy-builder/cpu-coolers/pump-speed-vs-fan-speed-which-makes-the-biggest-difference-for-aio-performance/
- NZXT support, Kraken fan and pump control: https://support.nzxt.com/hc/en-us/articles/360057023314-How-do-I-control-the-fan-speed-on-my-Kraken-cooler
- NZXT, CAM fan curve: https://nzxt.com/blogs/news/nzxt-cam-fan-curve
- NZXT, is my AIO failing: https://nzxt.com/blogs/news/is-my-aio-cooler-failing
- NZXT, AIO radiator mounting guide: https://nzxt.com/blogs/news/aio-radiator-mounting-guide
- NZXT, do you need CAM running: https://nzxt.com/blogs/news/do-you-need-cam-running
- Arctic Liquid Freezer III Pro manual (copy): https://manuals.plus/asin/B0DPHRVNGL
- be quiet! Pure Loop 2: https://bequiet.com/en/watercooler/4397
- KitGuru, be quiet! Silent Loop 2 review (9-12 V DC pump): https://www.kitguru.net/components/cooling/luke-hill/be-quiet-silent-loop-2-240mm-aio-cooler-review/all/1
- EK-Loop D5 G3 PWM specs (retailer copy): https://www.scan.co.uk/products/ek-loop-d5-g3-pwm-pump-motor-1500l-h-ceramic-bearing-12v-sata-powered
- Alphacool Core DDCzero datasheet: https://download.alphacool.com/datasheet/ENG_13867_Alphacool_Core_DDCzero_3300-4800rpm_PWM_Pump_datasheet.pdf
- Aquacomputer forum, D5 NEXT lowest rpm (about 2000 rpm floor): https://forum.aquacomputer.de/weitere-foren/english-forum/112864-d5-next-lowest-rpm/
- Lian Li Galahad II Trinity manual (copy): https://manuals.plus/m/6b13b0995f8afe89a689e841428fee7b5b2d239e69d9f175a3af952aabc665ff

FanControl and plugins:
- FanControl docs (Hysteresis, Response time, Step up/down, Start/Stop/Minimum %, Mix
  Sensor, Time Average, File sensor): https://getfancontrol.com/docs/
- FanControl plugins wiki: https://github.com/Rem0o/FanControl.Releases/wiki/Plugins
- FanControl.CorsairLink: https://github.com/EvanMulawski/FanControl.CorsairLink
- CorsairLink discussion (probe as liquid proxy): https://github.com/EvanMulawski/FanControl.CorsairLink/discussions/128
- FanControl.NzxtKraken: https://github.com/brokenmass/FanControl.NzxtKraken
- FanControl.Liquidctl: https://github.com/sp00n/FanControl.Liquidctl/releases
- HWiNFO forum, Kraken X53 readings with CAM: https://hwinfo.com/forum/threads/nzxt-kraken-x53-not-showing-up.6285/

Testing, reviews and community:
- GamersNexus orientation video (chapter index): https://www.letsplayindex.com/video/stop-doing-it-wrong-how-to-kill-your-cpu-cooler-aio-mounting-BbGomv195sk
- OCUK thread on GN clarification: https://forums.overclockers.co.uk/threads/gamers-nexus-video-on-improper-aio-installation.18896548/page-2
- Tom's Hardware, fluid temperature sensor: https://www.tomshardware.com/news/installing-a-fluid-temperature-sensor-the-best-dollar10-upgrade-for-a-custom-loop
- HardForum, target coolant temperature: https://hardforum.com/threads/what-max-coolant-temp-should-i-be-targeting.1994676/post-1044543111
- Aquacomputer forum, delta-T fan control: https://forum.aquacomputer.de/weitere-foren/english-forum/p1390945-maximum-fan-output-selection-for-controller/
- Digital Trends (JayzTwoCents pump test summary): https://qa2.www.digitaltrends.com/computing/you-might-be-cooling-your-pc-the-wrong-way-heres-how-to-fix-it
- TechPowerUp EKWB Fluid Gaming 240G (50% vs 100%): https://techpowerup.com/review/ekwb-fluid-gaming-240g-kit/7.html
- LTT, ML240L pump 50% vs 100%: https://linustechtips.com/topic/1349894-unable-to-set-full-pump-speed-on-my-aio/
- ASRock forum, 3-pin pump on PWM header: https://forum.asrock.com/water-pump-header-x370_topic4576.html
- Microcenter community, pump headers and CPU_FAN Ignore: https://community.microcenter.com/discussion/2774/powerspec-011d-build-best-header-for-pump
- darkFlash build guide (pump header DC/PWM): https://www.darkflash.com/article/000000000000593
- Corsair forum, 0 rpm reported while pump works: https://forum.corsair.com/forums/topic/177049-h100i-elite-capellix-working-0rpm-pump-failure/
