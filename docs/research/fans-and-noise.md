# Fans and Noise Perception: Rules for the Setup Wizard and Analyzer

Scope: the fans themselves (type, control method, limits, sizes, hubs, GPU fans) and how people perceive their noise. These notes feed the FanControl MCP setup wizard and the automatic setup analyzer.

Confidence levels:
- **High**: a manufacturer spec, an official doc, or the FanControl docs say it directly.
- **Medium**: several independent reviewers or labs agree, or it follows from established acoustics.
- **Low**: forum consensus, a single source, or engineering judgement written for this project. Treat these as defaults to tune, not facts.

FanControl terms used below (from [FanControl docs][fc-docs]):
- **Start %**: the value used to kick a stopped fan back into motion.
- **Stop %**: any command below this value snaps to 0%.
- **Minimum %**: a hard floor that applies even in manual mode.
- **Step up % / Step down %**: the maximum change per update, in each direction.
- **Response time**: the minimum time between changes.
- **Hysteresis**: the minimum temperature change before the curve reacts.
- **Ignore hysteresis at min/max**: skips hysteresis and response time at the ends of the curve.
- **Mix**: combines curves (Max, Min, Average, Sum, Subtract).
- **Sync**: copies another control's output, with an absolute or proportional offset.
- **Calibration**: measures where a fan starts, where it stops, and its min and max RPM, then builds a %-to-RPM map. Pairing with a speed sensor is required. RPM-mode curves need a valid calibration.
- **Avoid points**: % values the fan should never sit at, for example where it rattles ([FanControl README][fc-readme]).

---

## 1. Key rules

### A. Static pressure vs airflow

**R1. Choose the fan profile by the resistance the air meets.**
- Resistance means radiators, dense heatsink fin stacks, fine-mesh or foam dust filters, and solid or vented front panels. Fans there need high static pressure (pressure-optimised blades, often a tight frame gap). Examples: Noctua NF-A12x25 or NF-F12, Arctic P12/P14, Phanteks T30.
- Unrestricted spots mean open exhaust grilles and open-mesh fronts with no radiator. Airflow-optimised fans are fine there.
- How much it matters: on a high-fin-density radiator, a weak-pressure fan can lose a large share of its rated CFM. On an open grille the difference mostly disappears.
- Confidence: Medium. This is a long-standing consensus, and hwcooling measured NF-A12x25 static pressure as matching Noctua's spec. Retailer figures such as "3 to 6 °C on a 360 mm radiator" are unverified.
- Sources: [hwcooling NF-P12][hwc-p12], [Noctua "which fan is right"][noctua-which]

**R2. The analyzer should not over-weight this.**
- Fan type is a hardware recommendation, not something FanControl can fix.
- Flag only a clear mismatch, such as an airflow-type fan (for example, a fan sold as "AF" or "airflow") on a radiator or behind a dust filter or solid front. Phrase it as advice.
- In practice, a fan on a restrictive spot needs more RPM, and so makes more noise, to move the same air. The fix is "more RPM, or a better fan."
- Confidence: Medium.

**R3. Rank fans by airflow at equal noise.**
- Spec-sheet CFM and mmH2O are measured at maximum RPM with zero impedance, so they mislead.
- Noise-normalised testing (Hardware Busters runs each fan at 20, 25, 30, 35, 40 and 45 dBA; GamersNexus uses noise-normalised case thermals) is the right frame. The question is "how much cooling at the dBA the user tolerates."
- Confidence: High for the method.
- Sources: [Hardware Busters normalised tests][hwb-norm], [GN noise-normalised testing][gn-norm]

### B. PWM (4-pin) vs DC (3-pin) control

**R4. PWM basics (Intel 4-wire spec).**
- The PWM signal targets 25 kHz (21 to 28 kHz is acceptable).
- The vendor-specified minimum RPM must be 30% of max RPM or less. The fan must be able to start and run at that minimum.
- A startup boost is allowed if it stays at or below 30% of max RPM and lasts no more than 2 s.
- Below 20% duty is outside what the spec officially supports.
- Confidence: High.
- Sources: [Intel 4-Wire PWM spec rev 1.3][intel-pwm], [Noctua PWM white paper][noctua-wp]

**R5. Fans behave in one of three ways below their minimum duty. Each fan needs its own type, found by calibration.**
- **Type A** keeps running at minimum RPM for any duty below the minimum, including 0%. It never stops.
- **Type B** runs at minimum RPM for any non-zero duty below the minimum and stops only at exactly 0%. Most modern Noctua PWM fans work this way.
- **Type C** slows further and stops wherever the motor current is too low to keep it turning. Its stop point depends on the fan and drifts over time.
- Noctua exceptions that never stop below 20%: NF-A20 PWM, NF-S12B redux 1200 PWM, NF-B9 redux 1600 PWM.
- Some fans stop at a non-zero threshold. Arctic P12 PWM PST enters passive mode below 5% duty.
- The analyzer must not assume "0% equals stopped." After calibration it should show "fan never stops" or "stops below X%."
- Confidence: High.
- Sources: [Intel spec][intel-pwm], [Noctua white paper][noctua-wp], [Arctic P12 PWM PST datasheet][arctic-p12]

**R6. Typical numbers for modern 120/140 mm PWM fans.**

| Value | Typical | Notes |
|---|---|---|
| Spec minimum duty | 20% | Noctua and the Intel example |
| Minimum RPM (premium 120/140 mm) | 200 to 450 RPM | Noctua many models about 300 RPM, Arctic P12 PST 200 RPM, be quiet! Silent Wings 4 Pro 140 starts at about 365 RPM under PWM |
| Linear range | 20% to 100% duty | Speed scales roughly linearly between min and max RPM |
| Cheap fans | Often 30 to 40% duty to start | Some start above 700 to 1,200 RPM |

- Confidence: High for the vendor figures, Medium for the generalisation.
- Sources: [Noctua white paper][noctua-wp], [Noctua NA-FC1 manual][noctua-fc1], [Noctua "CPU fan error" FAQ][noctua-cpufan], [Igor's Lab SW4 Pro][igor-sw4]

**R7. DC (3-pin, voltage) control has a narrower range and less predictable start and stop points.**
- Measured examples:
  - be quiet! fan: starts at about 3.2 V.
  - Another 120 mm fan: starts at about 4.7 V, stops at about 4.4 V.
  - A third fan: needed 1,292 RPM to start and stopped below 774 RPM, which makes it nearly useless on DC control.
- Rules:
  - On a 3-pin fan, set Minimum % at or above the calibrated start % plus about 5 percentage points.
  - Assume no zero-RPM unless calibration proves the fan restarts reliably.
  - In BIOS, the header must be in DC mode for a 3-pin fan. A PWM-mode header drives a 3-pin fan at full speed.
- Confidence: Medium. The per-fan values come from Igor's Lab measurements, and the rules are derived from them.
- Sources: [Igor's Lab start-up/shut-down voltage tests][igor-volt], [FanControl README: check BIOS PWM vs DC][fc-readme]

**R8. Start % sits above Stop %, and the analyzer should enforce that.**
- Why it happens:
  - Getting a stopped rotor moving takes more torque than keeping it turning, because of static friction, cogging and the motor IC's commutation threshold.
  - So the duty needed to start (start %) is higher than the duty below which a spinning fan stalls (stop %). The gap is a built-in hysteresis.
- Typical gap: 3 to 15 percentage points on PWM fans and more on DC. Igor's 4.7 V start vs 4.4 V stop is about 3 points of 12 V.
- Rules:
  - Start % must be greater than Stop %, and Start % must be at or above the calibrated start value.
  - If a curve's lowest non-zero point is between the calibrated stall point and the start point, a stopped fan may never restart. Flag that.
- Confidence: High for the mechanism, Medium for the size of the gap.
- Sources: [Intel spec startup clause][intel-pwm], [Igor's Lab][igor-volt], [FanControl docs][fc-docs]

**R9. Running below the minimum, and what can go wrong.**
1. **Stall without restart.** The fan stops and stays stopped until duty rises past start %. BIOS may then raise a "CPU fan error" and force 100%. Noctua documents boards reading its 300 RPM minimum as a fault and spiking to 100%.
2. **Erratic rotation or cogging.** The fan wobbles in speed, sometimes pulsing audibly.
3. **PWM clicking or ticking.** Switching pulses flex the frame. Noctua says this is most audible at low speed on many PWM fans, and Corsair says PWM switching can become audible below roughly 20 to 30% duty.
4. **Motor hum or whine.** The 3-phase motor tone dominates at low RPM.
5. **RPM reads 0 or jumps.** Tach signals become unreliable near the stall point.
- Rule: never let a non-zero curve value fall below the calibrated minimum stable % plus a 2 to 5 point margin, unless that value is meant to stop the fan.
- Confidence: High for the effects, Low for the margin.
- Sources: [Noctua "CPU fan error"][noctua-cpufan], [Noctua new PWM fans (clicking)][noctua-click], [Corsair "what is PWM"][corsair-pwm], [TechPowerUp NF-P12 redux][tpu-p12]

**R10. What FanControl calibration gives the analyzer.**
- It runs the paired fan across its range and records the start point, stop point, min and max RPM, and the full %-to-RPM map.
- How the analyzer should use it:
  - Below the start point, the curve is effectively flat or zero.
  - Above about 80 to 90%, many fans barely gain RPM.
  - "Avoid points" handle resonance or rattle spots.
  - Re-run calibration after changing fans, hubs or BIOS mode.
- An uncalibrated control means the analyzer cannot verify the stall margin. Ask the user to calibrate first.
- Confidence: High.
- Sources: [FanControl docs][fc-docs], [FanControl README][fc-readme]

### C. Zero-RPM and semi-passive operation

**R11. Where zero-RPM is OK and where it is not.**

| Location | Zero-RPM allowed? | Reason |
|---|---|---|
| Case intake and exhaust | Yes, with care | The case can pass air by convection at idle. Keep at least one exhaust spinning if the GPU also stops, or tie the stop to both CPU and GPU temperatures |
| Air CPU cooler fan | Only for coolers designed for it | Noctua says its coolers were not designed for passive use and should be ramped down gradually. ASRock does not advertise CPU fan stop because it can overheat the CPU. Default: never 0%, floor at the calibrated minimum |
| AIO or custom-loop radiator fans | Only when keyed to coolant temperature with large hysteresis | A pump without airflow lets coolant heat-soak. Default: never 0% when keyed to CPU temperature |
| Pump | Never | Run it at a fixed 70 to 100%, or as the vendor recommends. Corsair measured only about 1 to 2 °C between 100% and about 80% pump speed. Let the radiator fans, not the pump, follow temperature |
| GPU | Leave it to the card's own fan-stop logic | See R25 to R27 |

- Confidence: Medium for case fans and pumps, High for the CPU cooler default.
- Sources: [Noctua passive FAQ][noctua-passive], [Noctua NH-P1 / LS-PWM][noctua-p1], [ASRock forum][asrock-semi], [Corsair pump vs fan][corsair-pump], [Dark Flash pump guide][darkflash-pump]

**R12. Zero-RPM needs hysteresis wide enough that the fan does not cycle.**
- How cycling happens: if idle temperature sits right at the start threshold, the fan starts, cools the part, stops, and repeats. The EVGA RTX 3060 report shows fans starting at about 56.5 °C, cooling, stopping and looping.
- Rules for a case or radiator fan:
  - Turn-on temperature at least 5 °C above turn-off temperature. On coolant, 2 to 3 °C is enough.
  - Use a response time of at least 10 to 30 s, plus stop % or start % in FanControl.
  - Restart at a value at or above start %, then let the fan settle down to the curve.
- Rule for the analyzer: flag a curve whose 0% to non-zero step sits within ±3 °C of the observed idle temperature.
- Confidence: Medium for the mechanism, Low for the numbers.
- Sources: [EVGA 3060 cycling][evga-cycle], [HardwareCanucks pulsing][hwc-pulse], [FanControl docs][fc-docs]

**R13. Cycling costs more than it saves.**
- Each start-stop cycle produces an audible start transient and loads the bearing and motor. Bearing-wear data is scarce, so treat that part as unproven.
- Users consistently report that a fan held at a steady 600 to 700 RPM is less noticeable than one pulsing between 0 and a few hundred RPM.
- Rule: if the user cannot get stable zero-RPM, recommend the lowest steady speed instead.
- Confidence: Low for wear, Medium for perception.
- Sources: [HardwareCanucks][hwc-pulse], [Moorhouse et al. via AoA 2016][aoa-fluct]

### D. Noise perception

**R14. How noise scales with speed (fan laws).**
- Airflow scales with RPM, pressure with RPM², and shaft power with RPM³.
- Sound power usually follows about 50·log10(RPM₂/RPM₁) dB. Published values range from 50 to 60.
- Rules of thumb:
  - 10% less RPM gives about 2.3 dB less noise.
  - 20% less RPM gives about 5 dB less.
  - Halving RPM gives about 15 dB less.
  - Doubling RPM gives about 15 dB more, which is roughly 2.8 times as loud.
- So a small cut from the top of the curve buys a lot of quiet. The cost is in airflow, which falls only linearly.
- Below about 600 to 800 RPM, motor and electrical noise often dominate, and real fans stop following the law. SPCR measured one 120 mm fan quieter at 900 RPM than at 700.
- Confidence: Medium. This is classic fan acoustics; check exact coefficients against ISO 13347 or an AMCA guide.
- Sources: [Techniques de l'Ingénieur, fan noise similarity laws][ti-fan], [SPCR 120 mm roundup][spcr-120]

**R15. How dB changes feel.**

| Change | How it feels |
|---|---|
| About 1 dB | Just noticeable in a lab |
| About 3 dB | The smallest change most people notice in everyday listening |
| About 10 dB | Roughly twice as loud |

- Applied to the wizard and analyzer:
  - Offer quiet-level steps 3 dB or more apart.
  - Treat a 1 to 2 dB gain as not worth extra temperature.
  - In a quiet room (about 20 to 25 dBA ambient), fans are heard much earlier than in a 35 dBA office.
- Confidence: High for the general psychoacoustics, though the sources found were secondary.
- Sources: [Cutnell & Johnson Physics, decibels][webassign-db], [Saint-Gobain "human hearing"][sg-hearing]

**R16. Speed changes are more annoying than constant noise of the same level.**
- In a lab, fluctuating sound was rated as annoying as steady sound about 5 dB louder (Moorhouse et al., 2007, amplitude modulation).
- Listening tests on multiple fans found that rotational-speed fluctuations as small as ±1% change perceived annoyance (Schade and Merino-Martinez, TU Delft, 2024/25).
- Implications:
  - Prefer a slightly higher steady speed over hunting.
  - Ramps should be slow enough to be inaudible as events, so the user hears no "whoosh" when a game loads.
- Confidence: Medium. The direction is robust, and transfer to PC fans is inferred.
- Sources: [AoA 2016 (citing Moorhouse 2007, Dittrich 2009)][aoa-fluct], [TU Delft rotor speed fluctuations][tud-fluct], [Forum Acusticum 2025][fa2025], [SAE 980587 cooling-fan sound quality][sae-fan]

**R17. Beat frequency between similar fans.**
- Two fans at nearly the same RPM produce a slow, periodic "wow-wow" hum. Its rate equals the difference between their blade-pass frequencies.
- Identical fans on one PWM signal often run within a few RPM of each other, which gives a slow, very noticeable beat.
- Fix: offset adjacent or push-pull fans by about 50 RPM (Noctua's figure for its fans). This speeds the beat until most listeners stop noticing it.
- In FanControl, use Sync with an offset, or separate curves with a small % offset (often 2 to 5%, depending on the RPM span). Fans on one header or hub cannot be offset.
- Confidence: High (Noctua).
- Sources: [Noctua fan speed offset explained][noctua-beat], [Noctua FAQ: periodic humming][noctua-hum]

**R18. Resonance and rattle spots.**
- Some RPM bands excite the case, radiator or cooler, producing a hum or buzz at specific speeds only.
- Fixes:
  - Use FanControl's avoid points during calibration.
  - Make sure no curve plateau sits inside a resonance band. Pass through it quickly, or stay above or below it.
  - Use anti-vibration mounts.
- Wizard rule: ask "is there a speed where it hums or rattles?" and store it as an avoid band of ±50 to 100 RPM.
- Confidence: Medium.
- Sources: [FanControl README avoid points][fc-readme], [OCUK Arctic P14 hum][ocuk-p14]

**R19. Tonal noise matters more than its dBA number suggests.**
- Motor whine, PWM ticking and blade-pass tones are judged harsher than broadband "whoosh" at the same dBA.
- At low RPM the motor or electrical share of the sound rises.
- Rules:
  - If the user complains about whine or clicking at low speed, raise the floor a little, try a different duty, or try DC mode on a PWM fan. Reviewers note voltage control can sometimes go below PWM minimums quietly.
  - Do not keep lowering speed when the complaint is whine.
- Confidence: Medium.
- Sources: [Noctua new PWM fans][noctua-click], [Igor's Lab][igor-volt], [Corsair PWM][corsair-pwm]

**R20. Practical "quiet" thresholds. These are defaults to tune per fan.**

| Size / class | Near-silent at about 50 cm | Quiet | Clearly audible |
|---|---|---|---|
| 120 mm premium (A12x25, P12, SW4) | ≤ 600 to 700 RPM | 700 to 1,000 RPM | > 1,200 RPM |
| 140 mm premium | ≤ 500 to 600 RPM | 600 to 850 RPM | > 1,000 RPM |
| 200 mm | ≤ 400 RPM | 400 to 600 RPM | > 700 RPM |
| GPU axial fans | ≤ 1,000 RPM | 1,000 to 1,500 RPM | > 1,800 RPM |
| AIO pump | Model-specific; tone matters more than RPM | | |

- Reference point: SPCR measured one 120 mm fan at 13 dBA@1m at 550 RPM and 27 dBA@1m at 1,670 RPM.
- For a 120 mm fan, about 20 dBA@1m corresponds to roughly 900 to 1,100 RPM.
- Confidence: Low to Medium. These are a synthesis of SPCR and Hardware Busters data and vary by model and case.
- Sources: [SPCR 120 mm roundup][spcr-120], [Hardware Busters normalised tests][hwb-norm]

### E. Ramp rates and response times

**R21. Ramp up faster than you ramp down, and never instantly.**
- Rising temperatures need a timely response, but falling temperatures can be followed slowly. FanControl's Auto curve does exactly this: it halves the step and doubles the response time while temperature falls.
- Suggested defaults:

| Setting | Default | Notes |
|---|---|---|
| Step up | 2 to 4% per update | At FanControl's usual 1 s update, a full 30% to 70% ramp then takes about 10 to 20 s, a gradual swell rather than a jump |
| Step down | 1 to 2% per update | Slow decay after a load ends |
| Response time | 2 to 5 s for case and radiator fans on CPU temperature, 1 to 2 s on GPU temperature | Use 5 to 30 s on coolant or water temperature, which changes slowly anyway |
| Hysteresis on CPU temperature | 2 to 4 °C | Coolant 0.5 to 1 °C, GPU 2 to 3 °C |
| Ignore hysteresis at max | Keep on | So a hot system gets full speed without waiting |

- Confidence: Low (project defaults, derived from R16 and the FanControl docs). Mechanism from the docs: High.
- Sources: [FanControl docs][fc-docs], [FanControl changelog (response time max 300 s, ignore-hysteresis toggle)][fc-changelog]

**R22. Do not drive case fans from raw CPU package temperature with no smoothing.**
- Modern CPUs spike 10 to 20 °C in under a second on short bursts, such as opening an app.
- Use one of these:
  - Hysteresis plus response time.
  - A slow-moving source such as coolant temperature, GPU temperature, or a Mix of CPU and GPU (Max).
  - A FanControl plugin or custom sensor that averages.
- CPU-cooler fans may follow CPU temperature, but still need 2 to 4 °C hysteresis.
- Confidence: Medium.
- Sources: [FanControl docs][fc-docs], [Corsair pump vs fan (coolant-based control)][corsair-pump]

### F. Fan sizes, splitters and hubs

**R23. Size trade-offs.**
- At the same airflow, a larger fan needs lower RPM and is usually quieter.
- The advantage is largest at low to moderate speeds. hwcooling found good 140 mm fans matching or beating top 120 mm fans below about 950 RPM, and beating them in efficiency around 600 RPM.
- Compared at equal noise, the 140 mm advantage shrinks, and it varies a lot by model.
- 200 mm fans are very quiet but have low static pressure, so they suit open or mesh intakes, not radiators or filters.
- RPM thresholds are size-specific, so the analyzer must not use a 120 mm rule on 140 or 200 mm fans.
- Confidence: Medium.
- Sources: [hwcooling Fractal Aspect 14][hwc-aspect14], [hwcooling Endorfy Stratus 140][hwc-stratus]

**R24. Splitters, daisy-chains and hubs.**
1. **One control, one curve.** Every fan on a header or hub gets the same PWM signal. They cannot be offset (R17) or zero-RPM'd individually. Mixing fan models on one header means the slowest-starting fan sets the floor.
2. **Only one RPM signal is reported.** A Y-splitter passes tach from one fan only. Hubs (Phanteks Universal PWM Hub, Arctic 10-port, Noctua NA-FH1) report port 1 only. A stall on the other ports is invisible, so put a representative fan on port 1.
3. **Header current budget.** Typically 1 A (12 W) per header, and some pump or high-current headers allow 2 A or more. Check the manual and add up each fan's max input current; an NF-A12x25 draws 0.14 A. Use a SATA-powered hub for more than about 3 to 4 fans per header.
4. **Mode pitfall.** A SATA-powered PWM hub on a header in DC or voltage mode runs every fan at 100%. Set the header to PWM.
5. **Calibration through a hub** reflects the port 1 fan only. Calibrate with the most demanding fan, the one with the highest start %, on port 1.
6. **Mixed models on one header** have different start and stop % values. Set Minimum % from the worst fan, which needs a manual check because the tach is blind to the others.
- Confidence: High for items 1, 2, 4 and 5, Medium for the current numbers.
- Sources: [Noctua FAQ: multiple fans on one header][noctua-multi], [Phanteks PWM hub][phanteks-hub], [Arctic Case Fan Hub][arctic-hub], [Noctua NA-FH1][noctua-fh1]

### G. GPU fans

**R25. NVIDIA GPU fans (RTX 30 series and newer).**
- **Minimum is 30%.** The driver accepts only 30 to 100% for manual control on Ampere and newer. Older GPUs accept 1 to 100%.
- **No manual 0%.** Setting 0% in FanControl releases control back to the card's automatic fan-stop logic.
- **Fan stop** usually ends around 45 to 60 °C. Multiple monitors or high refresh rates can raise idle power and block 0 RPM.
- **Curve rule:** put the 0% point at or below the card's 0 RPM temperature, then step straight to 30% or more. Never put points between 1% and 29%.
- **Channels:** a 3-fan card may show only 2 control channels because fans share a channel.
- Confidence: High.
- Sources: [FanControl docs (GPU)][fc-docs], [NVIDIA dev forum: 30 to 100% range][nv-30], [FanControl README][fc-readme]

**R26. AMD GPU fans (via ADLX).**
- Minimum is usually 30%. Lower commands are refused and the card holds its minimum.
- Zero RPM has a fixed minimum temperature, shown as a dotted line in Adrenalin.
- **Applying any fan setting from FanControl resets Adrenalin overclock and undervolt settings to default.** The wizard must warn about this.
- Reading temperatures without controlling the GPU fan avoids all of these caveats. Offer "monitor only" as the safe default.
- Confidence: High.
- Sources: [FanControl docs (GPU)][fc-docs]

**R27. Only one program may control a given GPU fan.**
- The same applies to motherboard headers and AIO pumps.
- When MSI Afterburner (user-defined fan curve), EVGA Precision, ASUS GPU Tweak, Armoury Crate, Fan Xpert, iCUE, NZXT CAM or Adrenalin tuning also control fans, users report:
  - Fans fighting.
  - Curves not applied at boot.
  - Speed spikes when the other tool loads.
  - Wildly fluctuating RPM readings.
- Rules:
  - The wizard should detect or ask about these tools.
  - In Afterburner, disable "user defined software automatic fan control." Afterburner can stay installed for overclocking and monitoring.
  - Set BIOS headers to a fixed value (FanControl suggests about 50%) or plain PWM, not "smart" mode, so BIOS and FanControl do not fight.
- Confidence: Medium for conflict symptoms (forum and blog sources), High for the BIOS advice.
- Sources: [FanControl README FAQ][fc-readme], [Tecnobits: FanControl vs Afterburner][tecnobits-ab], [EVGA Afterburner spike][evga-ab]

---

## 2. Anti-patterns (rules for the analyzer)

| # | Anti-pattern | Why it is bad | Suggested fix |
|---|---|---|---|
| AP1 | Non-zero curve points below calibrated stop % or minimum stable % | The fan stalls or wobbles, may not restart, and BIOS may raise a fan error and force 100% | Set Minimum % to calibrated minimum plus 2 to 5 points, or make it an explicit 0% |
| AP2 | Start % at or below Stop %, or Start % below calibrated start | A stopped fan never restarts | Start % at or above calibrated start; Start % greater than Stop % |
| AP3 | Zero-RPM on CPU cooler fan, pump, or CPU-temperature-keyed radiator fans | Overheating, coolant heat-soak, stall alarms | Floor at calibrated minimum; pump fixed at 70 to 100% |
| AP4 | Zero-RPM threshold within ±3 °C of idle temperature, or no hysteresis | Start/stop cycling, audible pulsing, wear | At least 5 °C on/off gap and 10 to 30 s response time, or a steady low speed instead |
| AP5 | Step up/down at 100% (instant) with 0 hysteresis on raw CPU temperature | Fans surge on every short load spike; fluctuating noise is perceived as worse (R16) | Step up 2 to 4%, step down 1 to 2%, hysteresis 2 to 4 °C, response time 2 to 5 s, or a smoother sensor |
| AP6 | Step down faster than step up | Fans drop and immediately re-surge | Step down at or below step up |
| AP7 | Identical fans side-by-side or push-pull at exactly the same % | Beat frequency hum | Offset about 50 RPM (Sync with offset), unless they share a header |
| AP8 | Curve plateau inside a known resonance or rattle band | Persistent hum at a common operating point | Avoid points; shift the plateau |
| AP9 | GPU curve with points between 1% and 29% (NVIDIA or AMD) | The driver clamps them, the curve behaves unexpectedly, or the fan sits at 30% when 0 was expected | 0% at or below the zero-RPM temperature, then 30% or more |
| AP10 | FanControl plus Afterburner, Precision, GPU Tweak, Armoury, iCUE or CAM curves on the same device | The tools fight, spikes occur, curves are lost at boot | Pick one controller per device and disable fan control in the others |
| AP11 | BIOS "smart" fan mode active on headers FanControl controls | BIOS and software fight, or BIOS overrides at boot | Set BIOS to a fixed value (about 50%) or plain PWM or DC mode |
| AP12 | 3-pin fan on a PWM-mode header, or SATA PWM hub on a DC-mode header | Fan stuck at 100% | Match the header mode to the device |
| AP13 | Treating hub or splitter RPM as representative of all fans | Stalls on other ports go unnoticed | Port 1 gets the most demanding fan; do not alarm-trust the others |
| AP14 | Header overload (sum of fan max current above the header rating, often 1 A) | Header damage, brown-out at spin-up | Use a SATA-powered hub |
| AP15 | Applying 120 mm RPM thresholds to 140, 200 mm or GPU fans | Wrong "too loud" or "too slow" verdicts | Use size-specific tables (R20) |
| AP16 | Curves that only rise at 100% near the top (for example 40% until 80 °C, then 100%) | An abrupt jump of 15 dB or more is very noticeable | A smooth slope; cap the noise-optimal max at 70 to 85% unless critical |
| AP17 | Running all fans at 100% "to be safe" | Huge noise cost (R14) for 1 to 3 °C | Show the user the dB vs temperature trade-off |
| AP18 | Pump on a CPU-temperature curve that swings | Audible pump-tone changes; little thermal benefit | Fixed speed |
| AP19 | Airflow-type fan on a radiator or dense filter (advisory) | Needs much more RPM for the same cooling | Hardware advice only |
| AP20 | Using an uncalibrated control for RPM-mode curves or stall checks | No data to validate | Calibrate first |
| AP21 | Applying an AMD GPU fan setting without warning | Adrenalin overclock or undervolt is reset | Warn, or use monitor-only |
| AP22 | Lowering speed further when the complaint is whine or clicking at low RPM | Tonal motor or PWM noise, not airflow noise | Raise slightly, try DC mode, or avoid points |

---

## 3. Wizard questions (fans)

Ask once per fan, header or hub channel. The analyzer can auto-fill some answers from sensors and calibration; confirm them with the user.

**Hardware identification**
1. **Fan make and model** (for example Noctua NF-A12x25 PWM, Arctic P14 PWM PST). This looks up min and max RPM, 0% behaviour (Type A/B/C) and pressure vs airflow type.
2. **Size**: 92, 120, 140, 200 mm, or GPU-integrated. This selects the R20 thresholds.
3. **Connector**: 4-pin PWM or 3-pin DC. Also the BIOS header mode if known (PWM, DC or Auto).
4. **Position and role**: front intake, top or rear exhaust, bottom intake, CPU air cooler, AIO or radiator (push, pull or push-pull), GPU, pump. Also whether there is a dust filter or solid front.
5. **How many fans on this header**, and how they are connected (direct, Y-splitter, daisy-chain such as Lian Li or Corsair chaining, SATA-powered hub).
   - For a hub: which fan is on port 1?
   - Are the fans all the same model?
6. **Header current rating** if more than 2 fans share it (or let the analyzer warn generically at 1 A).
7. **Other software controlling fans**: Afterburner, Precision, GPU Tweak, Armoury Crate, Fan Xpert, iCUE, CAM, Adrenalin tuning, vendor AIO apps. Also the BIOS fan mode (smart, fixed, full).
8. **GPU**: vendor (NVIDIA, AMD, Intel) and whether to control its fans or only read its temperature. Warn about AMD Adrenalin resets.

**Noise preferences**
9. **Noise sensitivity and environment**:
   - Room: quiet bedroom or studio, normal room, or noisy office or living room.
   - Distance to the PC: on the desk or under it.
   - Headphones or speakers.
10. **Which noise bothers you most?** (multi-select)
    - (a) loud whoosh under load
    - (b) fans speeding up and slowing down, surging
    - (c) humming or wow-wow beating
    - (d) whine, clicking or ticking at low speed
    - (e) fans starting and stopping
    - (f) rattle or buzz at a specific speed
    - (g) pump noise
    These map to R14, R16/R21, R17, R19, R12, R18 and R11 respectively.
11. **Zero-RPM wanted?** Never, at idle only, or as much as possible. Explain the trade-offs (R11 to R13).
12. **Priority**: silence first, balanced, or temperatures first. Also any hard temperature caps.
13. **Known problem speeds**: is there a speed where a fan rattles or hums? This becomes avoid points.
14. **Permission to run calibration**, which briefly runs fans from 0 to 100%, and to briefly test zero-RPM restart.

---

## 4. Open questions and conflicts

1. **Exponent of the noise-RPM law.** 50·log10 is the textbook value for fan sound power. PC fans at low RPM deviate because of motor and electrical noise and bearing tone. No PC-fan-specific regression was found. The analyzer could fit its own from user mic data or published normalised curves.
2. **Ramp-rate numbers (R21) are project defaults, not sourced.** No manufacturer or psychoacoustic study gives a PC-fan "inaudible ramp rate." The Schade and Merino-Martinez ±1% result is about steady fluctuation, not deliberate ramps. FanControl's default step values and update interval were not confirmed in the docs; check the app.
3. **Zero-RPM and bearing wear.** No vendor data on start-stop cycle life was found. Fluid-dynamic and magnetic bearings probably tolerate it well, and sleeve bearings may not. Mark as unproven.
4. **NVIDIA fan-stop temperature.** It depends on the card's BIOS (about 45 to 60 °C). There is no NVIDIA constant. The FanControl docs say "usually about 45 °C"; third parties say 50 to 60 °C.
5. **Pump speed.** Corsair suggests a fixed 70 to 80%, many forums say 100%, and some Arctic Liquid Freezer pumps are tied to their own PWM logic. Defer to the vendor per model.
6. **Arctic P12 PWM PST max RPM.** Arctic says 1,800 RPM; some retailers list 2,000 RPM. Lookup tables should use the vendor datasheet.
7. **"Quiet" RPM thresholds (R20)** are synthesised. Validate them against Hardware Busters or SPCR normalised data per model if the plugin keeps a fan database.
8. **The 140 mm advantage** is real at equal RPM or airflow, but small and model-dependent at equal noise. The evetech figure of "3 to 5 dB quieter" is unverified.
9. **The size of the fluctuation-annoyance penalty** (about 5 dB-equivalent) comes from amplitude-modulation studies on other sources, not PC fans.
10. **The current limit of daisy-chain systems** (Lian Li UNI, Corsair iCUE Link) is vendor-specific and usually controlled through vendor software, not FanControl. This needs separate research.

---

## 5. Sources

[fc-docs]: https://www.getfancontrol.com/docs
[fc-readme]: https://github.com/Rem0o/FanControl.Releases
[fc-changelog]: https://www.softpedia.com/progChangelog/FanControl-Rem0o-Changelog-263328.html
[intel-pwm]: https://www.mikrocontroller.net/attachment/414199/4-Wire_PWM_Controlled_Fans_Specification.pdf
[noctua-wp]: https://www.mikrocontroller.net/attachment/686242/Noctua_PWM_specifications_white_paper.pdf
[noctua-fc1]: https://noctua.at/pub/media/blfa_files/infosheet/noctua_na_fc1_datasheet_en.pdf
[noctua-cpufan]: https://noctua.at/de/i-get-a-cpu-fan-error-using-my-noctua-pwm-fan-is-it-faulty
[noctua-click]: https://www.noctua.at/en/news/noctua-announces-new-pwm-fans
[noctua-beat]: https://www.noctua.at/cn/expertise/tech/fan-speed-offset-explained
[noctua-hum]: https://www.noctua.at/en/support/faqs/how-do-i-avoid-periodic-humming-noises-when-running-fans-side-by-side-or-in-push-pull
[noctua-multi]: https://noctua.at/en/support/faqs/can-i-connect-multiple-fans-to-one-fan-header
[noctua-fh1]: https://www.noctua.at/en/products/na-fh1
[noctua-passive]: https://noctua.at/fr/is-it-possible-to-cool-my-cpu-without-a-fan-passive-using-noctua-coolers
[noctua-p1]: https://www.noctua.at/en/news/noctua-introduces-nh-p1-passive-cpu-cooler-and-ls-pwm-fan-for-semi-fanless-systems
[noctua-which]: https://noctua.at/en/which_fan_is_right_for_me_old
[arctic-p12]: https://cdn.iceshop.nl/objects/mmo_62515078_1542031193_9319_18523.pdf
[arctic-hub]: https://shop.teqex.co.uk/arctic-case-fan-hub-10-port-pwm-fan-hub-with-sata-power-acfan00175a.html
[phanteks-hub]: https://www.watercoolinguk.co.uk/p/PHANTEKS-Universal-PWM-Fan-Hub-retail-black_69940.html
[corsair-pwm]: https://www.corsair.com/eu/ar/explorer/diy-builder/fans/what-is-pwm-and-how-does-it-relate-to-pc-fans/
[corsair-pump]: https://www.corsair.com/tw/ar/explorer/diy-builder/cpu-coolers/pump-speed-vs-fan-speed-which-makes-the-biggest-difference-for-aio-performance/
[darkflash-pump]: https://www.darkflash.com/article/000000000000593
[igor-volt]: https://igorslab.de/en/it-is-always-better-scythe-kaze-flex-120-mm-fan-with-much-pressure-and-little-noise/3
[igor-sw4]: https://www.igorslab.de/en/be-quiet-silent-wings-4-pro-140-mm-fan-test/3
[tpu-p12]: https://techpowerup.com/review/noctua-nf-p12-redux-1700-pwm/3.html
[hwc-p12]: https://hwcooling.net/en/noctua-nf-p12-redux-1700-pwm-not-every-low-end-is-the-same/42
[hwc-aspect14]: https://www.hwcooling.net/en/fractal-design-aspect-14-rgb-pwm-larger-rotor-higher-airflow-per-dba-review/42/
[hwc-stratus]: https://www.hwcooling.net/en/endorfy-stratus-140-pwm-low-price-high-efficiency-review/2/
[hwb-norm]: https://hwbusters.com/cooling/be-quiet-silent-wings-pro-4-140mm-fan-review/10/
[gn-norm]: https://www.letsplayindex.com/video/test-same-fans-in-every-case-and-noise-normalized-case-thermals-eVkAXSJeKKM
[spcr-120]: https://silentpcreview.com/120mm-fan-roundup-scythe-1stplayer-reeven-phanteks/
[ti-fan]: https://techniques-ingenieur.fr/en/resources/article/ti680/fan-noise-bm4178/v2/similarity-laws-and-empirical-forecasting-methods-4
[aoa-fluct]: https://www.czasopisma.pan.pl//Content/101343/PDF/aoa-2016-0022.pdf
[tud-fluct]: https://research.tudelft.nl/en/publications/psychoacoustic-analysis-of-the-perceptual-influence-of-rotational/
[fa2025]: https://dael.euracoustics.org/confs/fa2025/data/articles/000808.pdf
[sae-fan]: https://saemobilus.sae.org/papers/sound-quality-cooling-fan-systems-980587
[webassign-db]: https://demo.webassign.net/ebooks/cj6demo/xlinks/c16-sec1-0023.htm
[sg-hearing]: https://saint-gobain-glass.co.uk/document/human-hearing
[nv-30]: https://forums.developer.nvidia.com/t/fan-speed-regression-with-nvidia-beta-470-42-01-and-rtx-3080-fans-dont-stop-on-idle/183604?page=2
[evga-cycle]: https://forums.evga.com/3060-XC-Gaming-Fans-Constantly-StartStop-amp-Noise-m3463765.aspx
[hwc-pulse]: https://hardwarecanucks.com/forum/threads/msi-afterburner-fan-profile-not-applying-at-windows-startup.84697/latest
[evga-ab]: https://forums.evga.com/GTX-980-Userdefined-fan-control-High-RPM-at-program-startup-m2372709.aspx
[tecnobits-ab]: https://tecnobits.com/fan-control-vs-msi-afterburner/
[asrock-semi]: https://forum.asrock.com/forum_posts.asp?TID=3228
[ocuk-p14]: https://forums.overclockers.co.uk/threads/arctic-p14-pwm-strange-hum-drone-at-certain-rpms.18922832

**Official and vendor documents**
- FanControl docs: <https://www.getfancontrol.com/docs>
- FanControl README and FAQ: <https://github.com/Rem0o/FanControl.Releases>
- FanControl changelog (Softpedia mirror): <https://www.softpedia.com/progChangelog/FanControl-Rem0o-Changelog-263328.html>
- Intel 4-Wire PWM Controlled Fans Specification rev 1.3 (mirror): <https://www.mikrocontroller.net/attachment/414199/4-Wire_PWM_Controlled_Fans_Specification.pdf>
- Noctua PWM specifications white paper (mirror): <https://www.mikrocontroller.net/attachment/686242/Noctua_PWM_specifications_white_paper.pdf>
- Noctua NA-FC1 datasheet: <https://noctua.at/pub/media/blfa_files/infosheet/noctua_na_fc1_datasheet_en.pdf>
- Noctua FAQ, CPU fan error (300 RPM minimum): <https://noctua.at/de/i-get-a-cpu-fan-error-using-my-noctua-pwm-fan-is-it-faulty>
- Noctua, new PWM fans (switching or clicking noise): <https://www.noctua.at/en/news/noctua-announces-new-pwm-fans>
- Noctua, fan speed offset and beat frequency: <https://www.noctua.at/cn/expertise/tech/fan-speed-offset-explained>
- Noctua FAQ, periodic humming in push-pull: <https://www.noctua.at/en/support/faqs/how-do-i-avoid-periodic-humming-noises-when-running-fans-side-by-side-or-in-push-pull>
- Noctua FAQ, multiple fans on one header: <https://noctua.at/en/support/faqs/can-i-connect-multiple-fans-to-one-fan-header>
- Noctua NA-FH1 hub: <https://www.noctua.at/en/products/na-fh1>
- Noctua FAQ, passive CPU cooling: <https://noctua.at/fr/is-it-possible-to-cool-my-cpu-without-a-fan-passive-using-noctua-coolers>
- Noctua NH-P1 and NF-A12x25 LS-PWM: <https://www.noctua.at/en/news/noctua-introduces-nh-p1-passive-cpu-cooler-and-ls-pwm-fan-for-semi-fanless-systems>
- Arctic P12 PWM PST datasheet: <https://cdn.iceshop.nl/objects/mmo_62515078_1542031193_9319_18523.pdf>
- Arctic Case Fan Hub (retailer listing): <https://shop.teqex.co.uk/arctic-case-fan-hub-10-port-pwm-fan-hub-with-sata-power-acfan00175a.html>
- Phanteks Universal PWM Hub (retailer listing): <https://www.watercoolinguk.co.uk/p/PHANTEKS-Universal-PWM-Fan-Hub-retail-black_69940.html>
- Corsair, what PWM is: <https://www.corsair.com/eu/ar/explorer/diy-builder/fans/what-is-pwm-and-how-does-it-relate-to-pc-fans/>
- Corsair, pump speed vs fan speed: <https://www.corsair.com/tw/ar/explorer/diy-builder/cpu-coolers/pump-speed-vs-fan-speed-which-makes-the-biggest-difference-for-aio-performance/>
- NVIDIA developer forum, 30 to 100% fan range on Ampere and newer: <https://forums.developer.nvidia.com/t/fan-speed-regression-with-nvidia-beta-470-42-01-and-rtx-3080-fans-dont-stop-on-idle/183604?page=2>

**Reviews and labs**
- Igor's Lab, start-up and shut-down voltage (example): <https://igorslab.de/en/it-is-always-better-scythe-kaze-flex-120-mm-fan-with-much-pressure-and-little-noise/3>
- Igor's Lab, be quiet! Silent Wings 4 Pro 140: <https://www.igorslab.de/en/be-quiet-silent-wings-4-pro-140-mm-fan-test/3>
- TechPowerUp, NF-P12 redux: <https://techpowerup.com/review/noctua-nf-p12-redux-1700-pwm/3.html>
- hwcooling, NF-P12 redux (static pressure): <https://hwcooling.net/en/noctua-nf-p12-redux-1700-pwm-not-every-low-end-is-the-same/42>
- hwcooling, Fractal Aspect 14 (140 vs 120 mm): <https://www.hwcooling.net/en/fractal-design-aspect-14-rgb-pwm-larger-rotor-higher-airflow-per-dba-review/42/>
- hwcooling, Endorfy Stratus 140: <https://www.hwcooling.net/en/endorfy-stratus-140-pwm-low-price-high-efficiency-review/2/>
- Hardware Busters, normalised noise tests (example): <https://hwbusters.com/cooling/be-quiet-silent-wings-pro-4-140mm-fan-review/10/>
- GamersNexus, noise-normalised case testing (video index): <https://www.letsplayindex.com/video/test-same-fans-in-every-case-and-noise-normalized-case-thermals-eVkAXSJeKKM>
- SilentPCReview, 120 mm fan roundup: <https://silentpcreview.com/120mm-fan-roundup-scythe-1stplayer-reeven-phanteks/>

**Acoustics and psychoacoustics**
- Techniques de l'Ingénieur, fan noise similarity laws: <https://techniques-ingenieur.fr/en/resources/article/ti680/fan-noise-bm4178/v2/similarity-laws-and-empirical-forecasting-methods-4>
- Archives of Acoustics 2016 (fluctuating vs steady annoyance; cites Moorhouse 2007, Dittrich 2009): <https://www.czasopisma.pan.pl//Content/101343/PDF/aoa-2016-0022.pdf>
- TU Delft, perceptual influence of rotational speed fluctuations: <https://research.tudelft.nl/en/publications/psychoacoustic-analysis-of-the-perceptual-influence-of-rotational/>
- Forum Acusticum 2025, distributed fans with speed deviations: <https://dael.euracoustics.org/confs/fa2025/data/articles/000808.pdf>
- SAE 980587, Sound Quality of Cooling Fan Systems: <https://saemobilus.sae.org/papers/sound-quality-cooling-fan-systems-980587>
- Decibels and perceived loudness (Cutnell & Johnson Physics, textbook excerpt): <https://demo.webassign.net/ebooks/cj6demo/xlinks/c16-sec1-0023.htm>
- Saint-Gobain, human hearing: <https://saint-gobain-glass.co.uk/document/human-hearing>

**Forums and secondary sources (low weight)**
- EVGA, RTX 3060 start/stop cycling: <https://forums.evga.com/3060-XC-Gaming-Fans-Constantly-StartStop-amp-Noise-m3463765.aspx>
- HardwareCanucks, GPU fan pulsing at idle: <https://hardwarecanucks.com/forum/threads/msi-afterburner-fan-profile-not-applying-at-windows-startup.84697/latest>
- EVGA, Afterburner/Precision spike at startup: <https://forums.evga.com/GTX-980-Userdefined-fan-control-High-RPM-at-program-startup-m2372709.aspx>
- Tecnobits, FanControl vs Afterburner: <https://tecnobits.com/fan-control-vs-msi-afterburner/>
- ASRock forum, semi-passive CPU fan: <https://forum.asrock.com/forum_posts.asp?TID=3228>
- OCUK, Arctic P14 resonance hum: <https://forums.overclockers.co.uk/threads/arctic-p14-pwm-strange-hum-drone-at-certain-rpms.18922832>
- Dark Flash, pump speed guide: <https://www.darkflash.com/article/000000000000593>
