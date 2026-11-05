# Case Airflow and Fan Layout: Research Notes for the Setup Wizard and Analyzer

Scope: desktop PC case airflow, fan layout, AIO radiator placement, which sensor should drive case fans, dust filters, anti-patterns, and the inputs a wizard needs.
Audience: whoever writes the FanControl MCP setup wizard and the automatic setup analyzer.
Compiled: 2026-11. Every rule is marked with a confidence level:

- **high**: a primary measured source, or several independent sources that agree.
- **medium**: one measured source, or a vendor guide with a sound physical rationale.
- **low**: an anecdote, a secondary write-up, or an engineering heuristic we derived ourselves (labelled **[heuristic]**).

Notation: "dT" means a temperature delta over ambient. Most reviewers report dT so that results can be compared across rooms.

> Caveat that applies to everything below: airflow results are **case-specific**. GamersNexus says outright that radiator and fan placement has to be tested per case, and that no single position is universally better ([GN H500P radiator guide][gn-h500p]). The analyzer should therefore phrase its output as "likely better/worse, verify with a fixed-RPM A/B test", not as absolute verdicts.

---

## 1. Key rules

### A. Pressure (positive / negative / neutral)

**R1. Thermals barely depend on pressure balance in a normal airflow case. Do not optimise pressure for temperature.**
- KitGuru tested a be quiet! Pure Base 500DX with every fan at 1200 RPM, running AIDA64 CPU+GPU for 60 minutes. Positive (2 front in / 1 rear out), negative (1 in / 2 out) and balanced (2 in / 2 out) gave "very similar" CPU and GPU dT. Negative was only marginally better for the CPU ([KitGuru 500DX pressure test][kg-pressure]).
- Hardware Secrets measured a small CPU advantage for negative pressure (exhaust fans at the rear/top) in a ventilated mid-tower ([Hardware Secrets][hws]).
- An SFF forum test found that more positive pressure lowered CPU and system temperatures but raised GPU temperatures, and negative pressure did the reverse. Low confidence, one user, one case ([SFF forum via search][sff-lz7]).
- Confidence: **high** that the effect is small, in the low single-digit °C range. **Medium** on its direction.

**R2. Choose pressure by dust exposure: target slight positive pressure, with every intake filtered.**
- Noctua's guide: positive pressure pushes air out through unfiltered gaps, so dust can only enter through the filtered intakes. Negative pressure pulls dust in through PCIe slot covers and panel seams. Neutral avoids both but is hard to hit exactly. Noctua's main lever is the ratio of intake to exhaust fans ([Noctua airflow guide, fundamentals][noctua-fund]).
- KitGuru reaches the same recommendation (slightly positive, with filtered intakes). Note that this is reasoning, not a measurement ([KitGuru][kg-pressure]).
- Confidence: **medium**. The physics is sound and vendors agree, but there is no published controlled dust measurement (see Open questions).

**R3. How to get slight positive pressure: count fans, then bias the RPM.** **[heuristic]**
- Fans with equal count and size at the same RPM do not move equal air. Filters and front panels choke the intakes, while exhausts usually sit behind open grilles. LTT's pressure experiment gave its intakes extra speed for exactly this reason ([LTT air pressure investigation pt. 1][ltt-pressure]).
- Analyzer rule:
  - Estimate intake airflow as Σ(intake fan CFM × panel factor). Use a panel factor of about 0.5–0.6 for a solid/closed front with side slots, about 0.75–0.85 for mesh plus a fine filter, and about 0.9 for open mesh.
  - Estimate exhaust airflow as Σ(exhaust fan CFM × 0.9).
  - Target an intake/exhaust ratio of about **1.1–1.3**.
  - Below 1.0, flag "likely negative pressure → more dust through gaps".
  - Above about 2.0, or with no exhaust at all, flag "no exhaust path" (see Anti-patterns).
  - Confidence: **low**. The factors are our own estimates, roughly calibrated against the filter and panel deltas in R14–R16.
- Fan control rule when counts are equal (e.g. 3 in / 3 out of the same model): run intakes about **5–15 % PWM higher** than exhausts on the same curve. With more intake fans than exhaust fans, one shared curve is fine. Confidence: **low** **[heuristic]**.
- Never make pressure balance the goal by starving exhaust. KitGuru found all-intake (no exhaust) was the **worst** result for both CPU and GPU ([KitGuru][kg-pressure]). Confidence: **high**.

**R4. All-exhaust and all-intake are both bad.** In KitGuru's test, all-exhaust gave "considerably higher" CPU temperatures than balanced, and all-intake was worst overall ([KitGuru][kg-pressure]). Hardware Canucks found that flipping the top fans to intake on top of front intake raised GPU temperatures, because the air had nowhere to leave ([Hardware Canucks][hwc-fans]). Confidence: **high**.

### B. Layouts

**R5. Default layout: front intake, rear exhaust, plus a top exhaust toward the rear.**
- Noctua's baseline is 5 fans: 2 front intake, 1 rear exhaust, and 1 top exhaust in the rear-most top slot. Its guide then adds fans step by step up to 9 ([Noctua setup ideas][noctua-setup]).
- Noctua also says to keep all fans flowing in one coherent direction and to use the largest fan that fits each slot ([Noctua fundamentals][noctua-fund]).
- Confidence: **high**.

**R6. The first two fans (one intake, one exhaust) give most of the benefit. Returns diminish quickly after about 4 fans.**
- Tom's Hardware, as reported secondhand (i9-12900K at over 250 W): going from 1 fan to 2 cut CPU temperature by about 18.5 °C, while going from 4 fans to 6 gained only about 2 °C ([summary via ExpertBeacon][eb-fans]). Confidence: **medium**, because the original chart was not verified.
- Toasty Bros tested 0 to 12 fans. Gains flattened after the first 2. Across the whole range the CPU dropped about 10 °C and the GPU about 4–5 °C ([summary][toasty]). Confidence: **medium**.
- bit-tech: going from 4 to 5 fans was negligible, and some 3-fan layouts were worse than the best 2-fan layout. Position matters more than count ([bit-tech big cooling investigation][bittech]). Confidence: **medium**.
- Hardware Canucks: populating every slot cut CPU temperature by 4 °C. Simply moving the existing top fan to the front gained 3 °C with no added fans ([Hardware Canucks][hwc-fans]). Confidence: **medium**.
- Analyzer rule: with fewer than 2 fans, or with no exhaust, recommend adding fans (high value). With 2–3 fans, adding a 3rd front intake is moderate value. With 5 or more fans, more fans are low value and more noise; recommend fixing placement or speed instead.

**R7. With an air cooler, a top-front fan set to exhaust short-circuits the fresh front intake. Make it an intake, or leave the slot empty.**
- Noctua tested 11 configurations in the Fractal North (glass side, high-end air-cooled build). The best was 3 front intake, rear exhaust, the **front-most top fan as intake** with an NA-IS1 spacer, and the rear-most top fan as exhaust. Noctua's reasoning: with both top fans exhausting, the CPU cooler gets too little fresh air. Noctua calls the gains "massive" for that build, but says they are setup-specific. Results with the mesh side panel were less clear-cut ([Noctua North blog][noctua-north]; [igor'sLAB coverage][igor-noctua]).
- igor'sLAB: a top intake mounted without spacing from the lid gets loud, and the noise can wipe out the thermal gain ([igor'sLAB][igor-noctua]).
- PCWorld/PC-WELT, top-front exhaust changed to intake: expect about 1–2 °C on the CPU, sometimes more. They add that with bottom intakes fitted, a top intake can raise GPU temperature, so top exhaust is safer in that case ([PCWorld][pcw-top]).
- Hardware Canucks: top intake beat top exhaust for the CPU, but GPU temperature rose when there was no top exhaust ([Hardware Canucks][hwc-fans]).
- Confidence: **high** that a front-top exhaust next to a front intake is wasteful. **Medium** that top-front intake beats simply leaving that slot empty.
- Analyzer rule: if the top-front fan is exhaust, the front has intakes, and the CPU uses an air cooler, flag it as a "short-circuit". Suggest flipping it to intake, but only if a rear and/or rear-top exhaust remains, or turning it off. Do **not** apply this rule when a top radiator is fitted.

**R8. Bottom intake under the GPU clearly helps GPU temperature and slightly hurts CPU temperature.**
- Tom's Hardware reviews of the Montech HS01 Pro, HS02 Pro and King 45 Pro: bottom-intake layouts gave chart-topping GPU temperatures in noise-normalized tests, at the cost of airflow to the CPU cooler ([HS01 Pro][th-hs01], [HS02 Pro][th-hs02], [King 45 Pro][th-king45]).
- OC3D on the NZXT H5 Flow: the bottom fan helps most with blower GPUs and at low case-fan RPM. Its effect shrinks when the case fans run at maximum ([OC3D H5 Flow][oc3d-h5]).
- Confidence: **high** for the GPU benefit. **Medium** for the CPU penalty (single-digit °C).
- Analyzer rule: when the GPU is the dominant heat source (GPU TGP more than about 1.5× CPU PPT), a bottom intake below the card is a high-value suggestion. Bottom fans must be intake, and they need about **≥ 10–20 mm** of clearance to the floor or the filter beneath them **[heuristic]**.

**R9. Side intake (dual-chamber "fishtank" cases such as the Lian Li O11 or Hyte Y60): side or bottom intake feeds the GPU, and top/rear should exhaust.**
- No controlled primary study was found. Confidence: **low**. Apply the generic rules: every fan must point along one coherent path (intakes on the side or bottom, exhausts on the top or rear), and side intakes must not face a radiator exhaust directly.

**R10. A vertical GPU mount with an air-cooled card needs a real gap to the glass.**
- GamersNexus, H500M at a fixed 55 % fan speed: horizontal mounting gave 44 °C dT and the CableMod vertical mount, which sits farther from the glass, also gave 44 °C. The stock vertical mount, close to the glass, gave **50 °C dT, about 6 °C worse**. GN's general point is that many vertical mounts suffocate air-cooled cards by leaving less than about 1–2 inches to the glass ([GN vertical GPU fact check][gn-vert]). Confidence: **high**.
- Analyzer rule: if the GPU is vertical, air-cooled, and has **< 30 mm** from the fan blades to a solid panel, warn the user to expect about +5–10 °C on the GPU and recommend a mesh or vented side panel, or horizontal mounting. The 30 mm threshold is from secondary sources ([summary][vert-secondary]) and is **[heuristic]**, confidence **medium**.

**R11. Mesh or high-open-area fronts beat solid fronts by roughly 5–13 °C.**
- KitGuru, NZXT H510 Flow (mesh) against a solid front: about **−13 °C CPU and −5 °C GPU** ([KitGuru H510 Flow build][kg-h510f]).
- OC3D: GPU up to **7 °C cooler** than the H510i ([OC3D H510 Flow][oc3d-h510f]).
- NZXT's own data: −8 °C CPU on a 280 mm AIO with an 11900K ([PCWorld][pcw-h510f]).
- GamersNexus modded the H500P (removed the front panel) and saw about **−12–13 °C CPU** ([GN HAF-ify mod][gn-hafify]).
- Hardware Canucks, in an already-mesh case: removing the mesh gained only about 1–2 °C ([Hardware Canucks][hwc-fans]). This shows the big gain comes from replacing a *restrictive* panel; it does not come from removing any panel at all.
- Confidence: **high**.
- Analyzer rule: a "solid front with side slots" plus an air-cooled high-TDP build (CPU > 150 W or GPU > 250 W) should get a "case-limited" warning. For such a case, recommend higher intake RPM, and say that more fans will help less than they would in a mesh case.

**R12. A GPU's heat ends up where its cooler sends it. Exhaust capacity must sit above and behind the GPU.**
- An open-air GPU cooler dumps nearly all of its TGP (often 200–450 W) into the case. The CPU cooler then breathes that air. In gaming, the GPU heat load usually exceeds the CPU's by 1.5–4× **[heuristic, from vendor TDP figures]**. The GN radiator data shows coupling in both directions: front-intake CPU radiator heat raised the GPU to 57 °C dT, compared with 50.5 °C when the radiator was on top ([GN H500P][gn-h500p]). Confidence: **high** that the coupling exists.

### C. AIO radiator placement

**R13. Top exhaust is the balanced default. A front intake radiator gives the coolest CPU but heats the GPU.**
- GamersNexus, H500P with a 240 mm CLC, dT values:

  | Configuration | CPU dT | GPU dT |
  |---|---|---|
  | Top radiator, exhaust, 2×200 mm front intake | **51.8 °C** | **50.5 °C** (best balance) |
  | Front radiator intake, push/pull | **45.8 °C** | **57 °C** (GPU close to throttling) |
  | Front radiator, top 200 mm exhaust | 47 °C | 59 °C |
  | Front radiator with fans removed | about 58.8 °C | – |
  | Front radiator, fans removed, with a gap | about 67 °C (worst) | – |

  GN favours the GPU because GPU boost clocks are more sensitive to temperature than CPU clocks ([GN H500P radiator guide][gn-h500p]). Confidence: **high** for this case, **medium** for generalising it.
- NZXT: top-mount exhaust is the first choice. Front intake improves the CPU but warms the rest of the case slightly. Radiator *size* matters more than position: a 360 mm in front beats a 240 mm on top ([NZXT radiator guide][nzxt-rad]). Confidence: **medium** (vendor).
- Noctua: put the radiator in front or on the side for the lowest CPU temperature, and on top if GPU temperature is the priority ([Noctua radiator guide][noctua-rad]). Confidence: **high** (consensus).
- Rear position: only 120 or 140 mm radiators, as exhaust. Bottom: NZXT advises against it in any configuration ([NZXT][nzxt-rad]).
- Analyzer rules:
  - A front intake radiator plus a hot air-cooled GPU (TGP ≥ 250 W) should get an info note: "expect GPU about +5–7 °C. If the GPU throttles or is loud, consider a top exhaust mount."
  - A top radiator mounted as **intake** feeds pre-heated air into the case. Flag it as low value unless the case has no other exhaust, which would be worse.
  - The radiator fans must be present, and flush with the radiator (no gap). Missing fans cost 13–21 °C in GN's test.

**R14. The pump must never be the highest point of the loop.**
- Air collects at the highest point. A pump at the top traps it, which causes gurgling and noise, worse cooling, and wear on the pump bearings.
- Rules of thumb:
  - Top mount: fine.
  - Front mount with the tubes at the **bottom**: preferred.
  - Front mount with the tubes at the top: OK as long as the radiator top is still above the pump.
  - Bottom mount: never.
  The highest point of the radiator must sit above the pump cap ([GN "Stop doing it wrong" video][gn-aio-orient] via [summaries][gn-aio-sum]; [NZXT][nzxt-rad]).
- Tube orientation does not meaningfully change temperature. It changes noise, and in the front-mount case it decides whether trapped air reaches the pump ([NZXT][nzxt-rad]).
- Confidence: **high**.
- Analyzer rule: if the radiator is bottom-mounted, or the radiator top sits below the pump block, raise an **error**-level warning. If it is front-mounted with tubes at the top, give an info note: "OK if the radiator top is higher than the pump; tubes-down is preferred."

### D. Which temperature should drive the case fans

**R15. Case fans should follow the hottest heat source that dumps heat into the case. Use max(CPU-derived demand, GPU-derived demand), not CPU temperature alone.**
- The case fans' job is to remove the heat the GPU and CPU coolers release. In gaming the GPU is usually the larger source (see R12), so a curve that follows the CPU alone under-ventilates during GPU-bound loads.
- FanControl's *Mix* curve (Max function) is built for this: one curve per source, combined with Max. Community and press guides recommend exactly that for case fans ([FanControl guide on Yahoo/How-To Geek][yahoo-fc]; [igor'sLAB FanControl tutorial][igor-fc]).
- Confidence: **high** (consensus and physics).
- Concrete starting point **[heuristic]**:
  - GPU curve: GPU core 40 °C → minimum (about 30–40 %), 75 °C → 100 %.
  - CPU curve: CPU 50 °C → minimum, 85 °C → 100 % on AMD Tctl or Intel package.
  - Case fans: Mix = Max(GPU curve, CPU curve).

**R16. Do not drive case fans from raw CPU package or Tctl without damping.** CPU package readings jump by 10–20 °C in under a second whenever a core boosts. Air in the case changes over tens of seconds. Use FanControl's step-up/step-down limits and response time (for example a 3–5 s up / 10–20 s down response) and 2–4 °C of hysteresis on case-fan curves **[heuristic]**. Alternatively, use a slower signal: GPU temperature, liquid temperature, or a motherboard/system sensor. Confidence: **medium**.

**R17. AIO radiator fans should follow liquid (coolant) temperature, not CPU temperature.**
- Coolant has a large thermal mass and rises slowly. Short CPU spikes barely move it, so a coolant-based curve avoids fan "revving". Corsair and Aqua Computer forum staff and users widely recommend this ([Corsair forum][corsair-liquid]; [Aqua Computer forum][aqua-dt]).
- Typical AIO coolant: 28–32 °C at idle, 35–40 °C under sustained load. **[heuristic, typical observed range]**
- Starting curve: liquid 30 °C → minimum, 40 °C → 100 %. The radiator fans also act as case exhaust or intake, so in a top-exhaust radiator layout consider Max(liquid curve, GPU curve) **[heuristic]**.
- If an ambient or case-air sensor exists, liquid minus ambient (dT) gives seasonally stable behaviour. The downside is that it does not compensate for a hot room ([Aqua Computer forum][aqua-dt]).
- Confidence: **medium-high**.

**R18. A case-air/ambient delta is the ideal signal for pure case ventilation when a probe exists** (for example a motherboard T_SENSOR header or an Aquacomputer probe). Case air minus room air at about 5–10 °C under load is the usual target in enthusiast practice. Confidence: **low** **[heuristic]**. Most users have no such probe, so fall back to R15.

### E. Dust filters

**R19. Fine dust filters cost real airflow. The effect ranges from about 0 °C to about 9 °C depending on the filter.**
- GamersNexus, Lian Li O11 Air: removing the front filter cut CPU dT from about 50 to about **41.5 °C (−9 °C)**. GN describes the filter as a major restriction. It also raised the pitch of the fan noise ([GN O11 Air review transcript][gn-o11air]; mirror transcript, medium confidence).
- KitGuru, Cooler Master Qube 500: removing the filter gave about **−9 °C CPU** with a 240 mm AIO. Cooler Master then revised the filter ([KitGuru Qube 500][kg-qube]).
- Hardware Canucks: in a case with a coarse filter, removing it changed nothing measurable ([Hardware Canucks][hwc-fans]).
- Noctua: filters reduce airflow, so weigh how dusty the room is against that loss ([Noctua fundamentals][noctua-fund]).
- Confidence: **high** that the effect is filter-specific and can be large.
- Rules: keep intake filters, because they make positive pressure work, and clean them regularly. A clogged filter turns intended positive pressure into negative pressure (critique of a filtered-intake dust test, [overclockers forum][oc-dust]). As a heuristic, the analyzer can suggest cleaning when, at the same load and same RPM, GPU or CPU dT has drifted **≥ 3 °C** above the baseline from setup **[heuristic]**.

---

## 2. Anti-patterns (measurably or physically worse; each one is an analyzer check)

| # | Anti-pattern | Evidence / effect | Severity |
|---|---|---|---|
| AP1 | **No exhaust at all** (all intake) | Worst CPU and GPU in KitGuru's test ([kg-pressure]) | error |
| AP2 | **No intake at all** (all exhaust) | CPU "considerably higher" than balanced ([kg-pressure]); dust pulled in through every gap ([noctua-fund]) | warn |
| AP3 | **Front-top fan exhausting right next to front intakes** (air cooler) | Short-circuits fresh air; Noctua's North tests; about 1–2 °C or more on the CPU ([noctua-north], [pcw-top]) | warn |
| AP4 | **Top intake with no top or rear exhaust** | GPU temperature rises and hot air stalls ([hwc-fans]) | warn |
| AP5 | **Bottom fan set as exhaust** | Fights the hot air that rises; removes fresh air the GPU needs (physics; reverse of R8) | warn |
| AP6 | **Opposing fans facing each other** (e.g. front intake vs. front-top exhaust, or a side intake blowing into a radiator exhaust) | Turbulence and recirculation; Noctua says keep one coherent flow direction ([noctua-fund]) | warn |
| AP7 | **Pump is the highest point** (bottom radiator, or radiator top below the pump) | Trapped air: noise, worse cooling, pump wear ([gn-aio-sum], [nzxt-rad]) | error |
| AP8 | **Radiator without fans, or a gap between fans and radiator** | +13 °C (no fans) and +21 °C (gap) on the CPU against push/pull ([gn-h500p]) | error |
| AP9 | **Air-cooled GPU vertically mounted close to the glass** (< about 30 mm) | +6 °C GPU dT in GN's test ([gn-vert]) | warn |
| AP10 | **Solid or closed front panel on a high-TDP air-cooled build with low intake RPM** | 5–13 °C worse than mesh ([kg-h510f], [gn-hafify]) | info |
| AP11 | **Top intake fan flush against a restrictive lid or strut, with no spacer** | Noise outweighs the gain ([igor-noctua], [noctua-north]) | info |
| AP12 | **Case fans driven only by raw CPU package temperature, undamped** | Fan revving and under-ventilation in GPU-bound loads (R15/R16) | warn |
| AP13 | **AIO fans on CPU temperature instead of liquid temperature** | Constant ramping with no thermal benefit ([corsair-liquid]) | info |
| AP14 | **Clogged or very fine filter left uncleaned** | Up to about 9 °C ([gn-o11air], [kg-qube]); flips pressure to negative ([oc-dust]) | info |
| AP15 | **Top radiator as intake while the front is also intake** | All-intake effect (AP1) plus pre-heated air from the CPU radiator over the board **[heuristic]** | warn |
| AP16 | **Mixed fan sizes or models sharing one PWM curve** for pressure tuning | Equal % does not mean equal airflow; this breaks the R3 pressure estimate **[heuristic]** | info |
| AP17 | **Adding fans to a case that already has 5 or more, instead of fixing placement** | About 2 °C gain from 4→6 fans ([eb-fans]); position beats count ([bittech]) | info |
| AP18 | **Fans running below their start/stall RPM** (curve minimum under stop speed) | Fan stops, airflow path breaks, pressure balance flips (general FanControl practice) | warn |

---

## 3. Wizard questions (inputs required to judge airflow)

Ask these in this order. Tag each answer **auto** (FanControl/LHM can detect it), **ask** (only the user knows), or **infer** (derived from other answers).

### 3.1 Hardware and heat sources
1. **CPU model** (auto via LHM). Infer PPT/TDP and choose the CPU sensor: Tctl/Tdie for AMD, Package for Intel.
2. **CPU cooler type** (ask): stock or low-profile air / tower air (single or dual) / AIO with radiator size 120–420 / custom loop. Choose from a picker, with "I don't know" allowed.
3. **GPU model** (auto). Infer TGP. **GPU cooler type** (ask, default inferred): open-air axial, blower, flow-through or "pass-through" backplate (e.g. NVIDIA FE), or water-blocked.
4. **GPU mounting** (ask): horizontal or vertical. If vertical, ask the gap to the side panel (< 2 cm / 2–4 cm / > 4 cm) and the panel type (glass or mesh).
5. **GPU length and slot thickness** (infer from model, or ask). This tells us whether it blocks bottom-intake flow and whether a front radiator fits.

### 3.2 Case
6. **Case model** (ask, free text with autocomplete if we ship a case DB). This drives defaults for slots and front panel.
7. **Front panel type** (ask if the case is unknown): full mesh / mesh with fine filter / solid with side or bottom slots / glass front (dual-chamber) / wood slats or other.
8. **Side intake?** (dual-chamber cases): yes or no, and which slots.
9. **Dust filters present** on the front, bottom and top (ask). **Room dustiness or pets** (ask: low / normal / high). This sets the pressure target (R2).
10. **Case orientation and placement** (ask): on a desk, on carpet (blocks the bottom intake), in an enclosed desk cabinet, or against a wall (rear exhaust clearance under 10 cm).

### 3.3 Fans (per fan header or channel; FanControl gives the list of controls and speeds)
11. For **every detected fan control**, ask:
    - **position**: front-top, front-middle, front-bottom, top-front, top-rear, rear, bottom, side, radiator, CPU cooler, or PSU-shroud.
    - **direction**: intake or exhaust (offer an "identify" step that spins one fan to 100 % while the others go to minimum).
    - **size**: 120, 140 or 200 mm.
    - **model**, if known: lets us look up CFM and static pressure for R3.
    - **fans per header**: hubs and splitters.
12. **Radiator** (if AIO or loop): position (front / top / side / rear / bottom); intake or exhaust; push, pull or push/pull; **tube location** (top, bottom or side of radiator); and whether the **radiator top sits above the pump** (yes / no / unsure, shown with an illustration).
13. **Is a liquid temperature sensor exposed?** (auto). Also check for any case or ambient probe, such as a motherboard T_SENSOR or a USB probe.

### 3.4 Preferences and usage
14. **Main workload**: gaming (GPU-bound), CPU rendering or compile, mixed, or mostly idle/office. This decides how sources are weighted in R15.
15. **Noise vs. temperature priority** (slider). Also ask the **maximum acceptable noise** at load, for example "audible but not distracting" versus "silent".
16. **Room ambient range**, summer and winter (optional). This is relevant for liquid-temperature curves that do not use dT (R17).

### 3.5 Derived checks the analyzer runs on these answers
- Intake/exhaust airflow ratio (R3), with flags for AP1, AP2 and AP16.
- Short-circuit check (R7, AP3), opposing flow (AP6), bottom-exhaust check (AP5).
- Pump-height / radiator checks (R14, AP7, AP8).
- Vertical GPU gap (R10, AP9).
- Radiator vs. GPU coupling note (R13).
- Restrictive-front warning (R11, AP10).
- Sensor mapping: GPU+CPU Mix for case fans, liquid for radiator fans, damping on (R15–R17, AP12, AP13).
- Optional **verification test** the wizard can offer:
  - Fix all fans at one RPM.
  - Run a 10–15 min combined CPU+GPU load (KitGuru and GN both use sustained combined loads).
  - Record CPU, GPU and liquid dT.
  - Change one variable and repeat.
  - Treat differences below ±1.5 °C as noise **[heuristic, typical run-to-run variance]**.

---

## 4. Open questions / conflicting evidence

1. **Dust and pressure: no published measured data was found.** LTT's year-long "PC Case Air Pressure Investigation" set up all-exhaust, all-intake and positive-leaning builds in Fractal R5 cases ([pt. 1][ltt-pressure]). We could not retrieve the follow-up results, and a TechPowerUp critique notes part 1 had no data. The "positive is cleaner" recommendation rests on physics and vendor guidance. Confidence for its *magnitude* is **low**.
2. **Which way does pressure shift temperatures?** KitGuru and Hardware Secrets saw negative pressure slightly better for the CPU. An SFF user saw positive better for the CPU and worse for the GPU. In every case the differences were small. Conclusion: treat pressure as a dust decision, not a thermal one.
3. **Top-front intake vs. exhaust.** Noctua (North, glass, air cooler) and Hardware Canucks say intake helps the CPU. PCWorld and Hardware Canucks say it can hurt the GPU when no exhaust or bottom path remains. Noctua's mesh-side-panel results were inconclusive. The analyzer should recommend it conditionally (R7).
4. **Front vs. top radiator.** GN (one case, 240 mm) favours top for balance. NZXT says size matters more than position. Forum reports range from "front intake 8–12 °C better on the CPU" to "top better". Present this as a CPU-vs-GPU trade-off, not a single answer.
5. **Fan-count numbers** come from secondary summaries (Tom's Hardware via ExpertBeacon, Toasty Bros via a digest site). The direction (strongly diminishing returns) is consistent, but the exact values are unverified.
6. **Noctua primary pages** (airflow guide, radiator guide, North blog) returned HTTP 429 during this research. Their content was taken from search-indexed excerpts and third-party coverage, and should be re-verified before quoting numbers.
7. **Panel factors and pressure ratio (R3)** are our own heuristics, not measured. If a case database is built later, the factors could come from GN or Tom's noise-normalized case data.
8. **Ideal case-air dT** (R18) has no authoritative source.
9. **Flow-through GPUs** (NVIDIA FE "pass-through" designs) exhaust partly upward into the CPU cooler's intake. No controlled layout study was found. As a heuristic, a top or rear exhaust right above the GPU becomes more important.
10. **Dual-chamber or side-intake cases** (O11, Y60, etc.) lack controlled layout comparisons in high-trust sources. Treat any rules for them as low confidence.

---

## 5. Sources

[gn-h500p]: https://gamersnexus.net/guides/3084-cooler-master-h500p-radiator-placement-guide
[gn-vert]: https://www.gamersnexus.net/guides/3324-fact-check-cablemod-vertical-gpu-thermals-vs-stock-h500m
[gn-hafify]: https://gamersnexus.net/guides/3085-project-haf-ify-mesh-cooler-master-h500p-case-mod
[gn-o11air]: https://gadgetory.github.io/Gamers-Nexus/Lian-Li-O11-Air-Case-Review-Dust-Filters-Revenge-ctKKTeaNpb8.html
[gn-aio-orient]: https://www.letsplayindex.com/video/stop-doing-it-wrong-how-to-kill-your-cpu-cooler-aio-mounting-BbGomv195sk
[gn-aio-sum]: https://appuals.com/how-wrong-aio-mounting-orientation-could-be-damaging-your-cpu
[gn-case-std]: https://gamersnexus.net/guides/3477-case-fan-standardization-tests-noise-normalized-thermals
[noctua-fund]: https://www.noctua.at/en/support/faqs/airflow-guide-fundamentals
[noctua-setup]: https://www.noctua.at/en/support/faqs/airflow-guide-setup-ideas
[noctua-next]: https://www.noctua.at/en/support/faqs/airflow-guide-next-steps
[noctua-rad]: https://www.noctua.at/en/expertise/guides/where-should-i-install-my-aio-radiator-top-vs-front-side
[noctua-north]: https://noctua.at/en/best-fan-setup-fractal-design-north
[igor-noctua]: https://www.igorslab.de/en/against-the-current-noctua-turns-the-fan-dogma-on-its-head-and-it-works/
[igor-fc]: https://igorslab.de/en/fan-control-correct-use-and-set-basics-curves-and-tools-tutorial/3
[nzxt-rad]: https://nzxt.com/en-intl/blogs/news/aio-radiator-mounting-guide
[kg-pressure]: https://www.kitguru.net/components/cases/james-dawson/positive-negative-airflow-testing-with-be-quiet-pure-base-500dx/
[kg-h510f]: https://www.kitguru.net/desktop-pc/james-dawson/nzxt-white-build-featuring-h510-flow/
[kg-qube]: https://www.kitguru.net/components/cases/matthew-wilson/cooler-master-to-improve-dust-filter-design-following-kitgurus-qube-500-review/
[oc3d-h510f]: https://overclock3d.net/reviews/cases_cooling/nzxt-h510-flow-case-review/3/
[oc3d-h5]: https://overclock3d.net/reviews/cases_cooling/we_built_an_nzxt_h5_flow_elite_revisiting_nzxts_h5_flow/3
[pcw-h510f]: https://www.pcworld.com/article/395126/whoa-nzxt-finally-put-a-front-mesh-panel-on-the-h510.html
[pcw-top]: https://www.pcworld.com/article/3198083/your-top-pc-fan-might-be-making-your-cpu-run-hotter.html
[th-hs01]: https://www.tomshardware.com/pc-components/pc-cases/montech-hs01-pro-case-review/2
[th-hs02]: https://www.tomshardware.com/pc-components/pc-cases/montech-hs02-pro-case-review/2
[th-king45]: https://www.tomshardware.com/pc-components/pc-cases/montech-king-45-pro-case-review/2
[th-pressure]: https://www.tomshardware.com/desktops/pc-building/how-to-optimize-your-pcs-airflow-using-positive-vs-negative-pressure
[hwc-fans]: https://hardwarecanucks.com/?p=76072
[hws]: https://hardwaresecrets.com/which-is-the-best-place-to-install-a-case-fan-part-1/4
[bittech]: https://www.bit-tech.net/reviews/tech/the-big-cooling-investigation/8/
[eb-fans]: https://expertbeacon.com/is-6-case-fans-overkill/
[toasty]: https://recapio.com/digest/how-many-fans-does-a-gaming-pc-need-by-toasty-bros
[ltt-pressure]: https://rosetta.to/u/linustechtips/one-year-benchmark-pc-case-air-pressure-investigation-pt-1
[oc-dust]: https://www.overclockers.com/forums/threads/797741/
[sff-lz7]: https://smallformfactor.net/forum/threads/lazer3d-lz7-quiet-gaming-cube-pc-case.596/page-4
[vert-secondary]: https://deltiasgaming.com/?p=278644
[yahoo-fc]: https://tech.yahoo.com/computing/articles/control-pcs-fans-open-source-220018325.html
[corsair-liquid]: https://forum.corsair.com/forums/topic/146020-set-custom-fan-curve-to-h110i-or-cpu-package/
[aqua-dt]: https://forum.aquacomputer.de/weitere-foren/english-forum/108693-dithering-fan-smoothing/

**Primary or high-trust measured:**
- GamersNexus: H500P radiator placement; vertical GPU fact check; H500P HAF-ify mod; O11 Air review (filter); AIO orientation video; standardized case testing methodology.
- KitGuru: pressure test; H510 Flow; Qube 500 filter.
- Tom's Hardware: Montech HS01/HS02/King 45 Pro reviews.
- OC3D: H510 Flow; H5 Flow.

**Vendor guides:**
- Noctua airflow guide: fundamentals, setup ideas, next steps.
- Noctua: radiator guide and Fractal North blog.
- NZXT: radiator mounting guide.

**Secondary or anecdotal (low/medium):**
- Hardware Canucks; Hardware Secrets; bit-tech; ExpertBeacon (summarising Tom's Hardware).
- Toasty Bros digest; PCWorld/PC-WELT; igor'sLAB.
- LTT pt. 1; Corsair, Aqua Computer, overclockers and SFF forums; Deltia's Gaming.

Not retrievable during research (HTTP 429): Noctua pages. Not retrievable (truncated): Tom's Hardware pressure article.
