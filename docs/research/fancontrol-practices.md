# FanControl (Rem0o) — Best Practices, Pitfalls, and Wizard Guidance

Research notes for the fancontrol-mcp plugin. Their purpose is to keep an AI assistant from making the usual
FanControl mistakes and to give a setup wizard a script to follow. Researched 2026-11-05. The latest release
at that date was **V285** (2026-11-02).

**Confidence key**
- **H (high):** official docs/README/wiki, release notes, or a direct statement by the author (Rem0o) in a GitHub issue.
- **M (medium):** the behaviour is clearly implied by official material, or the community agrees on it consistently.
- **L (low):** a third-party guide, my own inference, or general fan-control lore that FanControl docs don't confirm.

Short source tags such as [I#2946] refer to the Sources section at the end.

---

## 0. Mental model (read first)

- FanControl is mainly a **UI and curve engine on top of LibreHardwareMonitor (LHM)**. NvAPIWrapper handles NVIDIA
  GPUs, ADLXWrapper handles AMD GPUs, and plugins add other devices. When hardware isn't supported, the cause is
  usually LHM or a plugin, not FanControl itself [README].
- Each control card shows a % that is **read back from the SuperIO control register**. It is not a guaranteed fan
  state. FanControl "asks politely" and does not fight for a header. If something else (the BIOS smart mode, a vendor
  tool, the GPU driver) overrides the value, the card shows a **"?" ("being overridden by external")** indicator
  [I#988, I#571, I#1408].
- Every fan has two separate objects. A **Control** (the PWM/DC output, which has the curve, start/stop %, steps,
  etc.) and a **Speed sensor** (RPM). They have to be **paired** before calibration, RPM mode, and the start-failure
  failsafe can work [Docs].
- The building blocks are Curves (Graph, Linear, Mix, Sync, Trigger, Flat, Auto) and Custom sensors (Time Average,
  Mix, File, Offset). A Control points to one curve. Curves can feed Mix and Sync. Sensors feed curves.
- When FanControl exits cleanly it calls LHM `SetDefault()`/reset, which **hands the header back to the BIOS/EC**.
  GPUs get handed back to driver auto mode. A crash, kill, or BSOD skips that hand-off, so the header keeps its
  last written value until the firmware takes it back. When that happens depends on the board [I#1751, I#4244].

---

## 1. Setup checklist (correct order)

### 1.1 Before installing (hardware/firmware)

1. **Confirm it's a desktop.** Laptops are "mostly no". The app installs, but it usually finds no fans or throws
   errors. The exceptions are a few vendor plugins: Dell, newer Dell WMI, Lenovo ACPI, GPD, Acer PH315 [README FAQ]. (H)
2. **BIOS: turn off "smart" fan control on every header FanControl will drive.** On ASUS this is Q-Fan/Fan Xpert
   auto curves; on MSI, Smart Fan Mode; on Gigabyte, Smart Fan 5/6 auto curves; on ASRock, the smart curves. Set a
   **fixed default speed** instead. The author suggests something like **50%** [README FAQ; I#571 "untick smart mode on
   all your headers"]. (H)
   - Many boards have no "fixed %" option. A *flat-ish manual curve* (for example 40–60% across the whole range)
     plus a steep rise near critical temps is a reasonable compromise. It stays a safe fallback if FanControl isn't
     running. (M, inference)
   - Keep the **CPU_FAN / AIO_PUMP fallback safe**. If FanControl dies, the BIOS value is all that protects you. The
     pump should usually run at a fixed high speed (60–100%); check the AIO vendor's spec [L: Arctic/evetech
     guides].
3. **BIOS: set each header's mode explicitly to PWM (4-pin fans) or DC (3-pin fans).** Don't leave it on Auto. Auto
   sometimes detects the wrong mode, and a wrong mode often looks like "fan doesn't respond" or "stuck at
   full/fixed speed". The README says to *test* which mode works better on your board [README FAQ; Noctua FAQ]. (H/M)
4. **BIOS: if you plan to make fans stop at 0%, check the minimum-duty settings.** Some headers refuse to go below
   17% or 30% [I#571]. (H)
5. **Note fan hubs and splitters.** A hub reports only one RPM (often from fan 1) or none (some Lian Li/ARGB hubs).
   Without RPM, auto-pairing and calibration fail [I#3872]. (H)

### 1.2 Remove or neutralise competing software (see §5)

- Remove or disable ASUS Armoury Crate / Fan Xpert / "Asus Fan service", MSI Center / Dragon Center, Gigabyte
  Control Center / SIV, ASRock Phantom Gaming Tuning, NZXT CAM, Corsair iCUE (only if you use the CorsairLink
  plugin), L-Connect (only if you use the LianLi plugin), and **fan curves in MSI Afterburner, EVGA Precision, or
  AMD Adrenalin**. Afterburner can stay for OC as long as its fan control is off. (H for the specific tools cited in
  issues and plugin READMEs, M for the full list.)

### 1.3 Install and driver

1. Install the **current release**. Since V238/V239, FanControl uses a **PawnIO** build of LHM. WinRing0 is gone.
   The first launch prompts you to install the **PawnIO driver**. **If you decline, you get no motherboard
   sensors or controls**, while GPU and plugin sources keep working [Release V239]. (H)
2. **V237 and older** ship WinRing0 (FanControl.sys). Windows Defender flags it (as `HackTool:Win32/Winring0` or
   `Trojan:Win32/Vigorf.A`), and Microsoft's vulnerable-driver blocklist (Memory Integrity/HVCI) blocks it. Either
   one makes sensors disappear. **The fix is to update to V238 or later, not to turn off Defender or Memory
   Integrity** [README; I#3016; I#3410; KernelSight CVE-2023-1048]. (H)
3. Known PawnIO issues: FACEIT anti-cheat blocked it until PawnIO 2.1.0 (handled in V259). A BSOD on PawnIO install
   was fixed by shipping PawnIO 2.2.0 (V265) [Releases; I#3662]. (H)
4. Pick the build. Some plugins need a particular .NET flavour (for example LiquidCtl needs .NET 10). Plugins built
   for .NET 4.8 and .NET 8/10 can't be mixed [LiquidCtl README; I#1408]. (H)
5. Admin rights: FanControl needs elevation for driver access. The "Start with Windows" scheduled task runs elevated.
   V283 fixed its default elevation and V284 made it use the full exe path [Releases]. (H)

### 1.4 First launch (assisted setup)

1. Let the **assisted setup** run: sensor-source selection, then **automatic pairing**, then **calibration**. Since
   ~V200 it handles slow fans much more reliably [Releases V215+]. (H)
2. Close everything that might move fans while it runs, and keep the system idle.
3. Afterwards, review every Control card:
   - paired to the right RPM sensor? Use **Identify**/the 100% buttons, or the manual pairing dialog;
   - calibration status label OK?
   - rename it (for example "Front intake x3", "Rad top", "Pump").
4. Hide unused/unconnected controls and speed sensors. Since V197, speed cards are hidden by default.
5. **Save the config.** Configs are JSON files in the `Configurations` folder next to the exe. Since V261 they can
   live in any folder. Since V235, configs are trimmed, and FanControl prompts once to re-save [Releases]. (H)

### 1.5 Startup mode

- **Desktop app + "Start with Windows":** a scheduled task named "Fan Control" starts the app at logon. This is
  enough for most single-user PCs [README]. (H)
- **Service mode (V268+, "start service on boot"):** runs as SYSTEM in session 0, **before or without any user
  logon**. When you open the UI, the app takes over from the service (the service stops). When you exit from the
  tray or the menu, it asks whether to restart the service. **Closing the window with X does not exit; the app keeps
  running in the background.** The author is explicit that the service is *not* a resilience or watchdog feature.
  Its only purpose is running without a user session, or on multi-user machines [I#4035; Releases V268–V271]. (H)
- **AMD GPU (ADLX) works unreliably or not at all in service mode**, especially with no user session. AMD's own
  sample has the same problem. If curves depend on AMD GPU temps, prefer the app at logon, or design curves that
  tolerate a missing GPU sensor [I#4082; I#4244]. (H)
- Don't run two FanControl instances, or the service and the app, against the same hardware. V269+ allows only one
  service client [Releases V269]. (H)

---

## 2. Key rules (numbered)

Format: **Rule.** Why / details. — *Source* — **Confidence**

### BIOS and fallback

1. **Disable BIOS "smart" fan modes on headers FanControl controls, and set a fixed or safe default.** Otherwise the
   BIOS fights FanControl, causing pulsing, "?" overrides, or fans that won't stop. — [README FAQ], [I#571], [I#988]
   — **H**
2. **The BIOS setting is the safety net.** When FanControl exits, crashes, or isn't running yet (boot, logon, sleep
   transitions), the BIOS/EC curve or fixed value is what runs. Never leave a near-silent or 0% BIOS setting on
   CPU/pump headers. — [I#1751], [I#231], inference — **H/M**
3. **Set the header mode explicitly (PWM for 4-pin, DC for 3-pin) and test.** Wrong or Auto mode is a top cause of
   "control does nothing" and fans "stuck at one speed". — [README FAQ], [Noctua FAQ] — **H/M**
4. **Gigabyte boards: fans may stay at 100% after exit or reboot.** The board's IT87 chip needs an explicit reset.
   This has improved in newer LHM, so keep FanControl updated. — [I#1751] — **H**

### Pairing, identification, calibration

5. **Pair every Control with its RPM sensor before tuning anything.** Calibration, RPM mode, and the start-failure
   failsafe all depend on it. — [Docs: Speed sensor pairing] — **H**
6. **How auto-pairing works:** all fans go to 70% for about 5 s, then one control drops to 35%, and any RPM sensor
   that falls ≥20% is paired with it. It fails, or pairs wrongly, with hubs that report no RPM, with fans sharing a
   header, or when other software moves fans during the test. — [I#3872] — **H**
7. **Control and speed indexes normally match** (Fan Control #N ↔ Fan #N). A mismatch in a saved config is a red
   flag. — [I#1408] — **M**
8. **Use real calibration, never guessed start/stop %.** FanControl has a failsafe: if a fan commanded above 0 shows
   no RPM after ~5 s, it kicks to 100%. Since V259, it ramps up from start% step by step. Wrong values (for example
   start 5%, stop 1%) make fans **ramp to 100% repeatedly**. If a fan can't be calibrated (no RPM), set start and
   stop to 0. — [I#3872], [Release V259] — **H**
9. **Start %** kicks a stopped fan; **Stop %** snaps commands below it to 0%; **Minimum %** is a hard floor, even
   in manual mode; **Offset %** shifts the curve or manual value. Manual calibration ignores the offset (V284). —
   [Docs], [Release V284] — **H**
10. **Use calibration "avoid regions" (V267+)** for % bands that rattle, resonate, or behave oddly. This is better
    than hacking the curve. — [README], [Release V267] — **H**
11. **RPM mode** (a curve outputs target RPM instead of %) needs a valid calibration. Recalibrate after changing a
    fan, hub, or header mode. — [Docs], [Release V187 "detect control discrepancy"] — **H/M**
12. **Hide unconnected headers and junk sensors** (0 RPM, -128 °C, 255 °C, static values). Never put them in a
    curve or Mix. They cause fans to max out, or curves that never move. Use "allow missing sensor" in a Mix only on
    purpose (V229). — [Releases V197, V229, V284], inference — **M**

### Curves

13. **Graph** is the default choice. Left-click adds a point, right-click deletes one, and you can type exact
    values. The temperature range is configurable. — [Docs] — **H**
14. **Linear** gives min% below the min temp, max% above the max temp, and interpolates in between. Use it for
    simple case fans. — [Docs] — **H**
15. **Mix fan curve (Max/Min/Average/Sum/Subtract)** combines curves. **Max** is the standard pattern for a case fan
    that follows both CPU and GPU temps. Average dilutes the hotter source and can under-cool. Sum and Subtract are
    niche and can exceed 100% or go below 0%. — [Docs], inference — **H/M**
16. **Sync** copies another control's output with an absolute or proportional offset. Use it to keep a fan group
    together without duplicate curves. Since a sync follows its source, it inherits the source's hysteresis and
    step behaviour. — [Docs] — **H/M**
17. **Trigger** works like a Schmitt trigger: idle speed until temp ≥ load temp, then load speed until temp ≤ idle
    temp. Response time has separate up and down values (V235+). Use it for "stay quiet, then go loud and stay loud
    until really cool". The author recommends it over big hysteresis values for this pattern. — [Docs], [I#2946],
    [I#1203] — **H**
18. **Flat** gives a fixed % and is handy for setting many fans at once (pumps, test configs). — [Docs] — **H**
19. **Auto** hunts for the lowest speed that holds a target load temperature. Inputs: idle temp, load temp, min/max
    speed, step, deadband, and response time. Step is halved and response time doubled while temps fall. It **works
    best under constant load** and will hunt under bursty gaming loads. It is not a good default. — [Docs] — **H**
20. **Don't stack many curves needlessly.** The engine is cheap (30 curves is fine), but complex chains are hard to
    reason about. The author suggests consolidating to about 6–10 curves. — [I#1203] — **H**

### Custom sensors

21. **Time Average** smooths a spiky sensor over a set period. It's the right fix for CPU-package temp spikes, which
    are common on Ryzen and modern Intel. — [Docs], community consensus — **H/M**
22. **Mix sensor** applies Max/Min/Avg/Sum/Subtract to sensors. In Min/Max, null values are ignored (V284). "Allow
    missing sensor" is optional (V229). — [Docs], [Releases] — **H**
23. **Offset sensor** shifts a sensor by degrees or %. Use it to normalise sources (for example CPU reads hotter than
    the liquid). — [Docs] — **H**
24. **File sensor** reads °C as plain text from the **first line** of a `.sensor` file. Good for injecting values
    from scripts, an MCP server, or a test harness. The author uses it to test hysteresis by editing temperatures by
    hand. A file sensor fix landed in V285. — [Docs], [I#2946], [Release V285] — **H**
25. **Choose the right input per fan.** Case fans next to an AIO radiator often do better on **liquid/coolant temp**
    (from the AIO plugin) than on CPU temp. GPU-exhaust case fans do better on GPU temp. — community consensus — **L/M**

### Hysteresis, response time, steps

26. **Hysteresis is measured from the temperature of the last change, not from a curve point.** No change happens
    inside ±hysteresis around that last-change temperature, so the effective dead window is 2× the value. Output
    still follows the curve, so curve points are eventually reached. — [I#2946], [I#1203] — **H**
27. **Hysteresis up and down are separate settings (V226+), and decimals are allowed (V227).** "Only on the way
    down" means up = 0. — [Releases], [I#2946] — **H**
28. **"Ignore hysteresis at min/max temps" (on by default)** skips hysteresis and response time when crossing toward
    the first or last graph point, so idle and full speed are always reachable. Hysteresis applies again when temps
    re-enter the range. To make a hysteresis apply at a limit, add duplicate limit points outside the real
    temperature range. — [Docs], [I#1203], [Release V173] — **H**
29. **Recommended values from the author:** hysteresis around **3–5 °C** for a typical ±25 °C curve range (15 °C is
    "huge"); response time **at least ~3 s** (1 s is basically none); max response time is 300 s (V181). —
    [I#1203], [Release V181] — **H**
30. **Response time = the minimum time between changes.** It filters brief threshold crossings. If temps dip briefly
    past the threshold and the fans react, raise the response time rather than the hysteresis. — [Docs], [I#2946] —
    **H**
31. **Step up % / step down % = the maximum change per update cycle in each direction.** They smooth audible ramps
    and tame fans with nonlinear % to RPM responses. The author suggested **step up ~4–5%** for an erratic GPU. Steps
    also set the ramp when a control is first enabled. — [Docs], [I#1203], [I#1068] — **H**
32. **Avoid big jumps right at the idle threshold** (for example 0→50% at 55 °C). The fan cools the part below the
    threshold at once and oscillates. Use a gradual slope, a Trigger, or a gap between the on and off temps. —
    [I#1203] — **H**
33. **Temperatures react fast to load changes but slowly to fan-speed changes**, so stepped hysteresis rarely causes
    cycling by itself. Pulsing usually comes from a spiky input sensor, a too-steep curve near idle, or an external
    override. — [I#2946], inference — **H/M**

### GPU

34. **NVIDIA (RTX and most recent cards): the minimum command is 30%, and 0 RPM can't be set manually.** Turn on
    "**0% hardware curve override**" (NVIDIA 0 RPM override). When the curve outputs exactly 0%, FanControl releases
    the card to its own auto/0-RPM logic. Set **start and stop % to 30**. Make the graph a **sharp step from 0% to
    ≥30%** at or below the card's own 0-RPM temperature (often 35–50 °C). **Avoid 1–29%**: those values snap to 30%,
    or to 0 if stop is 0. — [Wiki: Nvidia 30% and 0 RPM], [Docs], [I#2262], [I#1198] — **H**
35. **Don't use "Force apply" on NVIDIA controls**, because it interferes with the 0% hand-off. — [I#2262], [I#1198]
    — **H**
36. **The card's own conditions decide 0 RPM.** Multiple monitors (higher idle power), high-performance power mode,
    or OC can keep the fans spinning. Test first: disable the control. If the card won't stop on its own,
    FanControl can't make it stop. — [Docs], [I#2262], [I#3585] — **H**
37. **2 control cards for a 3-fan GPU is normal**, because fans share channels. — [README FAQ] — **H**
38. **If the NVIDIA control misbehaves, try the LHM GPU controls instead of NvAPIWrapper**, in sensor settings. —
    [I#1198] — **M**
39. **AMD (ADLX): applying any fan setting resets Adrenalin overclock/tuning to defaults.** The minimum is about 30%
    and lower commands are refused. There's a Zero-RPM temp threshold, shown as a dotted line in Adrenalin. Build the
    graph the same way as for NVIDIA (0% at or below the Zero RPM temp, then jump to the minimum). **To avoid all of
    this, read AMD temps without controlling the AMD fans.** — [Docs: AMD] — **H**
40. **AMD + service mode/sleep/hibernate/Fast Startup is fragile.** The AMDADLXServ process can die and crash the
    caller. V285 has mitigations. Prefer the app at logon for AMD GPU control. — [I#4244], [I#4082] — **H**
41. **Only one program may control GPU fans.** Turn off the fan curve or "user-defined fan speed" in Afterburner,
    Precision X1, Adrenalin, or vendor GPU tools (ASUS GPU Tweak, MSI Center). Bouncing RPM or a "?" mean there's a
    conflict. — [I#3805], [I#3585], [I#988] — **H**

### Operation and robustness

42. **"?" next to a control % = an external override.** Find the other controller (BIOS smart mode, vendor app, GPU
    tool). **Force apply** (3-dot menu) rewrites the value every cycle as a last resort. Don't use it on NVIDIA. —
    [I#988], [I#571] — **H**
43. **Sleep is the hardest case.** The BIOS resets fan registers during sleep. FanControl closes the driver before
    sleep and reopens it on resume. If the fans are wrong after wake, **Ctrl+R (refresh sensors)** or `-r` usually
    fixes it. Hardware- or software-specific failures exist (Armoury Crate is a known cause of 100% after wake). —
    [I#231], [I#3349], [Releases V190, V246, V247] — **H**
44. **At boot, sensors can show up late.** FanControl saves the expected sensor count and re-detects until the count
    matches. After hardware changes, **re-save the config** so the count is right. Some vendor services (Asus Fan
    service) have to start before FanControl. — [Release V122], [I#899] — **H**
45. **Test a control in Manual mode first.** If the slider doesn't move the RPM, no curve tweak will fix it. The
    cause is outside the curve: BIOS mode, a hub, LHM support, or another program. — [I#571], [I#3585] — **H**
46. **Stuck UI/sensors on ASUS: try unticking Embedded EC, then Controllers, then InpOut** in sensor settings to
    isolate the faulty source. Gigabyte second chips need InpOut, which is on by default. — [I#1867], [Release V153] —
    **H**
47. **Configs, CLI, and switching:** `FanControl.exe -c <config.json or full path>` starts with that config, or
    switches to it in the running instance (full paths since V262). `-r` refreshes sensors, `-e` exits, `-m` starts
    minimized, `-w` forces the window, `-sc` runs as a service client. Hotkeys can switch configs (V136+). Tray icons
    can switch configs too. — [Docs: CLI], [Wiki: Hotkeys], [Releases] — **H**
48. **FanControl blocks session end (logoff/shutdown) when there are unsaved changes.** An assistant that edits the
    live config should save it explicitly. — [Release V115] — **H**
49. **Keep FanControl and LHM up to date when debugging hardware**, but note the author's advice that a working
    setup doesn't *have* to update (for example at the PawnIO switch). — [Release V239] — **H**
50. **Report hardware-support problems to LHM or the plugin repo, not FanControl.** The FanControl repo closes
    hardware requests. — [README] — **H**

---

## 3. Common problems and fixes

| Symptom | Likely cause(s) | Fix | Src / Conf |
|---|---|---|---|
| No motherboard sensors/controls at all | PawnIO not installed (V238+); old WinRing0 blocked by Defender or Memory Integrity (≤V237); AV (Kaspersky/Avast) blocking; unsupported SuperIO | Install PawnIO when prompted, or update to latest; add an AV exclusion only for the FanControl folder; check LHM nightly support; on ≤V237, update instead of disabling HVCI | [Rel V239], [I#3016], [I#2563] H |
| Only GPU fans detected, CPU/chassis missing | SuperIO not detected; regression in an LHM version; ASUS EC issues | Refresh (Ctrl+R) a few times; update; try the LHM nightly DLL; report to LHM | [I#3823], [I#899] H |
| Sensors missing after boot or wake, need several refreshes | LPC port not answering yet; vendor services racing | Re-save the config so the sensor count is stored; delay startup; let vendor services start first, or remove them | [Rel V122], [I#899] H |
| Control % changes but RPM doesn't | Header in the wrong PWM/DC mode; BIOS smart mode; hub without control passthrough; wrong pairing; external program | Set the BIOS mode explicitly; disable smart fan; test in Manual mode; re-pair; remove the conflicting tool | [README], [I#571], [I#1408] H |
| "?" next to the % | Something else is setting the header (BIOS, Armoury Crate, Afterburner, GPU driver limit) | Remove or disable the other controller; Force apply as a last resort (not on NVIDIA) | [I#988] H |
| Fans pulse / ramp up and down every few seconds | Spiky sensor (CPU package); steep curve near idle; 0↔x% jumps at a threshold; BIOS smart mode fighting; wrong start/stop causing 100% kicks | Time Average sensor; gentler slope; Trigger curve; hysteresis 3–5 °C + response time ≥3 s; step up/down limits; disable BIOS smart mode; run calibration | [I#1203], [I#2946], [I#3872] H |
| Fan repeatedly jumps to 100% then back | Start-failure failsafe: start% too low, or a fan with no RPM sensor paired wrongly | Run the assisted pairing + calibration; for RPM-less fans set start/stop 0 | [I#3872], [Rel V259] H |
| All fans 100% briefly when FanControl starts or a control is enabled | Control starts from the current value and steps toward the target; old bug when the chip has no readable value (fixed V126/127) | Update; set reasonable step values; a short ramp is normal | [I#1068] H |
| Fans stuck at 100% after exiting FanControl or rebooting (Gigabyte) | IT87 chip needs an explicit reset; BIOS default restore not triggered | Update (LHM reset improved); use Exit rather than killing; set a BIOS fixed default | [I#1751] H |
| Fans 100% (or wrong) after sleep/resume | BIOS resets registers during sleep; Armoury Crate; driver not reopened | Ctrl+R / `-r`; update (many sleep fixes); remove Armoury Crate; scheduled task with an unlock/resume trigger if FanControl dies on sleep | [I#3349], [I#231], [D#1717] H |
| FanControl process gone after sleep | Crash in a GPU plugin (ADLX) or NZXT handle leak | Update; disable the offending source/plugin; restart via a task trigger | [D#1717], [I#231], [I#4244] M/H |
| NVIDIA stuck at 30% / won't go 0 RPM | API minimum is 30%; curve outputs 1–29%; Force apply on; card conditions (multi-monitor, power) | 0% hardware override, start/stop 30, step graph, remove Force apply, respect the card's 0-RPM temp | [Wiki], [I#2262] H |
| NVIDIA GPU fans bounce 0↔hundreds of RPM | Another program (Afterburner/Precision) with its own curve | Disable the other fan control | [I#3805] H |
| AMD overclock reset after FanControl starts | ADLX fan apply resets Adrenalin tuning | Expected; reapply OC or don't control AMD fans from FanControl | [Docs AMD] H |
| AMD GPU sensors missing in service mode | ADLX unreliable in session 0 / no user | Run the app at logon; avoid AMD-temp-dependent curves in service mode | [I#4035], [I#4082] H |
| NZXT CAM "communication failed" / Kraken conflicts | FanControl's LHM controller source and CAM both talk to the device | Untick "Controller" in sensor settings, **or** close CAM; two programs can't share NZXT devices | [I#1668] H |
| Corsair devices erratic with the CorsairLink plugin | iCUE / "Corsair Service" running | Stop iCUE and Corsair Service; SignalRGB is compatible at listed versions | [CorsairLink README] H |
| Lian Li fans not controllable | L-Connect running alongside the plugin | Configure L-Connect, exit it, and disable its scheduled task | [LianLi plugin README] H |
| HWInfo plugin shows no sensors | HWiNFO not running; "Report value in Gadget" not enabled; DLL blocked | Run HWiNFO (sensors-only OK), enable gadget reporting per sensor, unblock the DLL, refresh | [HWInfo plugin README] H |
| Plugin not loading | DLL blocked by Windows (MOTW); wrong .NET flavour; duplicate plugin DLL | Properties → Unblock; match the .NET build; remove duplicates (V260 fix) | [plugin READMEs], [Rel V260] H |
| Defender "Trojan:Win32/Vigorf.A" / "HackTool Winring0" | WinRing0 in ≤V237 | Update to ≥V238 (PawnIO) | [README], [I#3410] H |
| FACEIT or anti-cheat blocks the driver | Old PawnIO | PawnIO ≥2.1.0 (FanControl ≥V259) | [README] H |
| Service stops when the app opens and doesn't come back | By design: the app takes over; X ≠ exit | Use tray → Exit and accept "restart service"; or just close the window | [I#4035] H |
| Settings lost / config not applied after reboot | Unsaved changes; old start-with-Windows task path or elevation | Save; re-toggle Start with Windows (V283/V284 task fixes) | [Rel V283/V284] H |
| Laptop: no fans | Unsupported EC | Check for a vendor plugin; otherwise use the vendor tool | [README FAQ] H |

---

## 4. Anti-patterns (things the assistant must NOT do or recommend)

1. **Leaving BIOS smart fan or Q-Fan curves active** on headers FanControl drives, which makes the two fight.
2. **Setting the BIOS fallback to 0%, or to "silent", on CPU or pump headers** "because FanControl handles it". If
   FanControl crashes, sleeps badly, or starts late, the CPU or pump goes unprotected.
3. **Assigning a curve to an unpaired or uncalibrated control** and then tuning start/stop by guessing (5%/1%).
   That triggers the 100% failsafe loops.
4. **Using an unconnected header or a junk sensor** (0 RPM, -128 °C, constant values) as a curve input or in a Max
   mix.
5. **Driving case fans directly from raw CPU package temp without smoothing** (no Time Average, no hysteresis,
   response time 1 s). That's the classic pulsing setup.
6. **Huge hysteresis (10–15 °C) to get "stay loud until cool" behaviour.** Use a Trigger curve, or a Graph + Trigger
   Mix, instead.
7. **Expecting hysteresis to act at the first or last graph point** while "ignore hysteresis at min/max" is on.
8. **Steep 0→50% jumps exactly at idle temperature**, which make the fan oscillate around the threshold.
9. **NVIDIA curves with values in 1–29%**, Force apply on an NVIDIA control, or forcing 0% manually. **AMD curves
   with values below the card's minimum.**
10. **Two programs controlling the same fan**: Afterburner/Adrenalin curve + FanControl, Armoury Crate + FanControl,
    CAM + LHM controller source, iCUE + CorsairLink plugin, L-Connect + LianLi plugin.
11. **Disabling Defender or Memory Integrity, or adding broad AV exclusions, to make an old WinRing0 build work**
    instead of updating to the PawnIO build.
12. **Using Mix → Average when you need Max** (cooling one hot component), or Sum/Subtract without clamping in mind.
13. **Auto curve as a default for gaming PCs.** It is designed for constant loads.
14. **Running the service and the app, or two configs/instances, at once** and expecting both to work; or treating
    service mode as a crash watchdog.
15. **Relying on AMD GPU temperatures in service mode or across hibernate/Fast Startup** without a fallback.
16. **Killing FanControl (Task Manager/taskkill) instead of a clean exit (`-e` or tray → Exit).** That skips the
    hand-off reset (critical on Gigabyte).
17. **Editing the JSON config on disk while FanControl is running** and expecting it to apply, or forgetting to save
    UI changes. Load the edited file with `-c` instead. (M, inference from config-switch behaviour)
18. **Tying pump speed to CPU temperature with low minimums.** Most AIO pumps want a fixed high speed (60–100%) or
    only a gentle curve tied to liquid temp. (L/M)
19. **Assuming laptop support.**
20. **Chasing hardware bugs in FanControl curves** before testing Manual mode. If manual doesn't work, curves never
    will.
21. **Dozens of near-duplicate curves.** Use Sync, or one curve shared by several controls.
22. **Assuming 1:1 fan:control on GPUs or hubs** (3 fans but 2 channels; a hub reports one RPM).

---

## 5. Software conflicts quick reference

| Software | Conflict | Recommended action | Conf |
|---|---|---|---|
| ASUS Armoury Crate / Fan Xpert / Asus Fan service | Overrides headers, 100% after wake, sensor races | Uninstall with the ASUS uninstall tool, or at least disable Fan Xpert; set fans in BIOS | H ([I#3349], [I#899]) / M |
| MSI Center / Dragon Center, Gigabyte Control Center/SIV, ASRock tuning | Board-level fan control fights the SuperIO | Remove the fan module or uninstall | M |
| MSI Afterburner / EVGA Precision / GPU Tweak | GPU fan curve conflicts | Disable their fan control (OC can stay) | H ([I#3805]) |
| AMD Adrenalin | Shares ADLX; FanControl apply resets the OC | Choose one fan controller; reapply the OC | H |
| NZXT CAM | Device can't be shared | Close CAM, or untick the "Controller" source | H ([I#1668]) |
| Corsair iCUE + Corsair Service | Incompatible with the CorsairLink plugin | Stop both | H |
| SignalRGB | Compatible with CorsairLink at specific versions; may grab other USB devices | Check per device; disable its fan features | M |
| Lian Li L-Connect | Can't run with the LianLi plugin | Exit it and disable its task | H |
| HWiNFO | Fine alongside (used by the HWInfo plugin); the author sees no systematic conflict | OK | H ([I#988]) |
| liquidctl plugin | Claims exclusive ownership of devices | Use `liquidctl_filter.txt` to exclude devices other tools need | H |
| Kaspersky / Avast / other AV | Partially blocks the driver/sensors | Exclusion for the FanControl folder, or update | H ([I#2563], [I#1521]) |
| Citrix Workspace (USB redirection) | Breaks NZXT/Corsair USB devices | Uninstall if those devices are used | H |

---

## 6. Wizard questions (FanControl-specific)

Ask or check these in order. Wherever the plugin can detect something itself (running processes, installed apps,
config JSON), it should detect it and ask the user to confirm.

### A. Platform
1. **Desktop or laptop?** For a laptop, check for a vendor plugin (Dell, Lenovo, GPD, Acer PH315, Dell WMI).
   Otherwise stop and set expectations.
2. **FanControl version?** If ≤V237, recommend an update (WinRing0/Defender). If ≥V238, **is PawnIO installed?**
   (The About page shows the PawnIO version, and V237+ shows a PawnIO/WinRing0 indicator.) No PawnIO means no
   motherboard sensors.
3. **.NET build** (4.8 / 8 / 10)? This matters for plugin compatibility.
4. **Motherboard vendor/model?** It flags known quirks: Gigabyte (exit/reset, InpOut second chip), ASUS (EC,
   Armoury Crate), MSI (smart mode on every header).

### B. BIOS (per header)
5. For **each header** (CPU_FAN, CPU_OPT, AIO_PUMP/W_PUMP, SYS/CHA_FAN n): **is it in PWM or DC mode (not Auto)?
   What is connected (fan model, 3- or 4-pin, splitter/hub, how many fans)?**
6. **Is BIOS smart fan / Q-Fan / Smart Fan 5/6 disabled** on these headers? **What fallback is set** (fixed % or a
   safe curve)? Warn if CPU or pump fallback is very low.
7. **Is the pump on its own header with a fixed high speed?** Does the user want FanControl to control the pump at
   all? (Default: no, or a Flat curve.)

### C. Competing software
8. Detect and ask about: Armoury Crate / Fan Xpert, MSI Center, Gigabyte Control Center, ASRock tools, NZXT CAM,
   iCUE (+ Corsair Service), L-Connect, SignalRGB, MSI Afterburner / Precision X1 / GPU Tweak (fan curve enabled?),
   AMD Adrenalin fan tuning, Citrix Workspace, a third-party AV.
9. **Which program should own GPU fans?** (FanControl, or the vendor tool with FanControl only reading temps.)
10. **AIO/RGB ecosystem?** (Corsair, NZXT, Lian Li, Thermaltake, Aquacomputer.) Offer the matching plugin and the
    "close vendor app" steps.

### D. Startup and runtime
11. **Single user, auto logon?** → app + Start with Windows. **Must run before logon / multi-user / headless?** →
    service mode. **AMD GPU control needed?** → warn about the service.
12. **Sleep/hibernate/Fast Startup in use?** If yes, test wake behaviour. Consider disabling Fast Startup when AMD
    GPU control is in use, and keep Ctrl+R/`-r` as a remedy.
13. **Multiple profiles wanted** (silent/gaming/render)? Plan configs plus `-c` switching, hotkeys, or tray
    switching.

### E. Fans and sensors (after the assisted setup)
14. **Did pairing and calibration succeed for every control?** List any control without an RPM pair, failed
    calibration, or a "?" indicator.
15. **Run Identify** (the 100% buttons) for each control and have the user name the physical fans. Confirm the
    intake/exhaust/radiator role.
16. **Which headers are unused?** → hide them. **Which sensors look unrealistic?** → hide or exclude them.
17. **Any rattle/resonance ranges?** → avoid regions.
18. **Noise priority vs temperature priority?** And **should fans stop at idle (0 RPM)?** If yes, check that the
    header and fan support stopping, set start% from calibration, and use a Trigger or a gap to avoid start/stop
    cycling.
19. **Primary heat source for case fans:** CPU, GPU, liquid temp, or Max of several?
20. **GPU vendor:** NVIDIA → 0% override + 30% rules; AMD → OC reset and minimum warnings; Intel Arc → IntelCtlLibrary
    plugin.

### F. Verification steps the wizard should run
- Manual-mode sweep per control (0 → start% → 50 → 100): does RPM follow? Flag anything that doesn't.
- Watch for a "?" after 30–60 s under a curve.
- Load test (CPU stress, then GPU stress): check for no pulsing, and that the fans reach their planned max.
- Idle period: check for no oscillation around the idle threshold.
- Save the config, restart FanControl, then reboot: settings persist and sensor count is stable.
- Sleep → wake: fans correct within ~10–30 s.
- Exit FanControl: fans return to the BIOS fallback (none stuck at 100% or 0%).

---

## 7. Recommended starting templates (assistant defaults)

These are opinionated starting points, not author-mandated values (confidence **M/L** unless noted).

- **Smoothing:** Time Average of CPU package over **5–10 s** as the CPU-driven curve input.
- **Hysteresis:** **3 °C down / 1–2 °C up**, response time **3–5 s** (author guidance, see [I#1203], **H** for the
  ranges). Leave "ignore hysteresis at min/max" ON.
- **Steps:** step up 5–10%/cycle and step down 2–5%/cycle for quiet ramps. Use smaller steps for fans with steep %
  to RPM curves.
- **Case fans (air cooler):** Mix **Max**(CPU-avg graph, GPU graph). Graph: 30–35% up to ~50 °C, rising to 60–70% at
  ~75 °C, and 100% at ~85–90 °C (CPU-specific; adjust Tjmax for X3D, Intel, etc.).
- **Radiator fans:** graph on **liquid temp** (for example 30–35 °C → minimum, 40–45 °C → 100%) where an AIO plugin
  provides it. Otherwise smoothed CPU.
- **Pump:** Flat 70–100% (vendor-dependent) or leave it on the BIOS/vendor default.
- **NVIDIA GPU:** 0% to ~(card's 0-RPM temp − 2 °C), a step to 30%+, then a slope; start/stop = 30; 0% override ON;
  no Force apply.
- **AMD GPU:** the same shape with the card's minimum %. Or **read-only** if the user overclocks in Adrenalin.
- **Fan stop for case fans:** only if calibration shows a clean start; use Trigger or a ≥5 °C gap between the stop
  and start temps.

---

## 8. Notes for the MCP implementation

- **Configs are JSON** in `Configurations\` (or any path, V261+). To apply a programmatically generated config,
  write a new file and call `FanControl.exe -c "<full path>"` (V262+ supports full paths). The running instance
  switches. Don't hot-edit the active file. (H for `-c`, M for "don't hot-edit")
- After hardware/driver changes, call `FanControl.exe -r` (refresh) and re-save to update the stored sensor count.
- To stop cleanly, use `FanControl.exe -e`. Don't kill the process, so the BIOS hand-off runs.
- **File sensors** (`.sensor`, °C on the first line) let the MCP inject synthetic temps for wizard tests (for
  example ramp a fake temperature to check curves, hysteresis, and Trigger behaviour without stressing hardware).
  This is the author's own testing approach [I#2946].
- In service mode the UI app replaces the service while open. An MCP that drives the UI or CLI must handle the
  service/app hand-off and the exit prompt [I#4035].
- Respect the hardware ownership rule: if a "?"/override is detected, tell the user. Don't escalate to Force apply
  automatically.

---

## Sources

Official / author
- [README] FanControl.Releases README (FAQ, PawnIO/WinRing0 warning, plugins, compatibility): https://github.com/Rem0o/FanControl.Releases
- [Docs] Official docs (curves, parameters, custom sensors, GPU, CLI): https://getfancontrol.com/docs/
- [Wiki] Nvidia 30% and 0 RPM: https://github.com/Rem0o/FanControl.Releases/wiki/Nvidia-30%25-and-0-RPM
- [Wiki: Hotkeys] https://github.com/Rem0o/FanControl.Releases/wiki/Hotkeys---Automate-config-changes-(V136-and-up)
- [Wiki: Plugins] https://github.com/Rem0o/FanControl.Releases/wiki/Plugins
- [Releases] Release notes V100–V285: https://github.com/Rem0o/FanControl.Releases/releases (specifically V115, V122,
  V136, V153, V173, V181, V187, V190, V197, V200, V215, V226, V227, V229, V235, V238, V239, V246, V259, V260–V262,
  V265, V267–V272, V283–V285)

GitHub issues/discussions (author comments)
- [I#2946] Hysteresis semantics: https://github.com/Rem0o/FanControl.Releases/issues/2946
- [I#1203] GPU hysteresis, recommended values, limits: https://github.com/Rem0o/FanControl.Releases/issues/1203
- [I#988] "Being overridden by external", Force apply: https://github.com/Rem0o/FanControl.Releases/issues/988
- [I#571] BIOS smart mode, manual-mode testing: https://github.com/Rem0o/FanControl.Releases/issues/571
- [I#1408] Readback %, pairing indexes: https://github.com/Rem0o/FanControl.Releases/issues/1408
- [I#1068] 100% on start, step logic on enable: https://github.com/Rem0o/FanControl.Releases/issues/1068
- [I#3872] Pairing algorithm, 100% start failsafe, start/stop: https://github.com/Rem0o/FanControl.Releases/issues/3872
- [I#1751] Gigabyte fans 100% after exit (reset hand-off): https://github.com/Rem0o/FanControl.Releases/issues/1751
- [I#231] Sleep behaviour, driver close/reopen: https://github.com/Rem0o/FanControl.Releases/issues/231
- [I#3349] 100% after wake (Armoury Crate): https://github.com/Rem0o/FanControl.Releases/issues/3349
- [I#899] Sensors need multiple refreshes; Asus Fan service: https://github.com/Rem0o/FanControl.Releases/issues/899
- [I#2262] NVIDIA 30% floor: https://github.com/Rem0o/FanControl.Releases/issues/2262
- [I#1198] NVIDIA 0 RPM, Force apply, NvAPI vs LHM: https://github.com/Rem0o/FanControl.Releases/issues/1198
- [I#3585] Control doesn't match curve (GPU): https://github.com/Rem0o/FanControl.Releases/issues/3585
- [I#3805] GPU bouncing to 0 RPM (conflict): https://github.com/Rem0o/FanControl.Releases/issues/3805
- [I#4035] Service vs app semantics: https://github.com/Rem0o/FanControl.Releases/issues/4035
- [I#4082] Service vs app behaviour; AMD in service: https://github.com/Rem0o/FanControl.Releases/issues/4082
- [I#4244] ADLX service crash, Fast Startup: https://github.com/Rem0o/FanControl.Releases/issues/4244
- [I#1668] NZXT CAM conflict: https://github.com/Rem0o/FanControl.Releases/issues/1668
- [I#1867] UI stuck; EC/Controller/InpOut toggles: https://github.com/Rem0o/FanControl.Releases/issues/1867
- [I#2563] Kaspersky blocking; HWiNFO notes: https://github.com/Rem0o/FanControl.Releases/issues/2563
- [I#3016] / [I#3410] Defender WinRing0 flags: https://github.com/Rem0o/FanControl.Releases/issues/3016 , https://github.com/Rem0o/FanControl.Releases/issues/3410
- [I#3662] PawnIO BSOD: https://github.com/Rem0o/FanControl.Releases/issues/3662
- [I#3823] Only GPU fans detected: https://github.com/Rem0o/FanControl.Releases/issues/3823
- [D#1717] FanControl stops on sleep (ADLX plugin, task trigger): https://github.com/Rem0o/FanControl.Releases/discussions/1717

Plugins
- [HWInfo plugin] https://github.com/Rem0o/FanControl.HWInfo
- [CorsairLink README] https://github.com/EvanMulawski/FanControl.CorsairLink
- [LianLi plugin README] https://github.com/lewisgibson/FanControl.LianLi
- [LiquidCtl README] https://github.com/antoine-bouteiller/FanControl.LiquidCtl

Third-party / background (lower confidence)
- [Noctua FAQ] PWM vs Auto mode: https://noctua.at/en/support/faqs/how-do-i-optimise-my-fan-settings
- [Arctic] PWM settings / example curve: https://support.arctic.de/pwm-settings
- [KernelSight CVE-2023-1048] WinRing0 on the HVCI vulnerable-driver blocklist: https://splintersfury.github.io/KernelSight/case-studies/CVE-2023-1048
- JayzTwoCents feature video (linked from README): https://www.youtube.com/watch?v=uDPKVKBMQU8
- jrmora.com FanControl guide (hysteresis/response example): https://jrmora.com/en/fan-control-take-total-control-fans/2

Gaps: I couldn't fetch Reddit threads (r/FanControl, r/buildapc) or the GamersNexus/Optimum videos directly. Where
community consensus is mentioned, it isn't backed by a specific citation. The full text of the getfancontrol.com
docs subpages was read only through a summarising fetch.
