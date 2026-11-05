# Thermal limits and temperature-dependent boost (desktop CPUs, GPUs, storage, board)

Purpose: give the FanControl MCP assistant hard numbers for where fan-curve
end-points belong. Two goals drive the numbers:

- **Quiet without performance loss**: keep the component just below the point where
  the boost algorithm starts to take away clock (not below the point where it "looks hot").
- **Max performance**: keep the component as cold as noise allows, because modern boost
  algorithms turn every degree of headroom into clock, with diminishing returns.

Confidence legend: **H** = vendor statement or multiple independent measurements agree;
**M** = one good measurement or consistent community/vendor-forum data; **L** = inferred,
sparse, or conflicting data.

Researched 2026-11. Numbers are for stock/default firmware unless stated. Always prefer the
part's own reported limit (e.g. HWiNFO "CPU TjMax", NVML "slowdown temp") over this table
when it is available at runtime.

---

## 1. Summary table

"Perf loss" means a measurable clock/fps reduction caused by temperature (not by power).
"100 % fan by" is the recommended end-point of the fan curve for that sensor. "Alarm" is
the point where the assistant should warn the user that cooling is insufficient or broken.

| Component class (sensor) | No measurable perf loss below | Small, gradual loss band | 100 % fan by | Alarm at | Conf. |
|---|---|---|---|---|---|
| Ryzen 7000/9000 non-X3D (Tctl/Tdie) | ~70 °C (loss <1-2 %) | 70-95 °C, ~0.5-1 %/5 °C | 90 °C (quiet) / 80 °C (perf) | sustained 95 °C **and** clocks well below rated, or >95 °C | M |
| Ryzen 9000 X3D (9800X3D, 9950X3D) | ~70 °C | 70-95 °C | 85-90 °C | 95 °C sustained | M |
| Ryzen 7000 X3D (7800X3D, 7950X3D) | ~70 °C | 70-89 °C | 80-85 °C | 89 °C (limit) | H (limit) / M (curve) |
| Ryzen 5000 (5800X/5900X/5950X 90 °C; 5600X 95 °C) | ~65-70 °C | 70-90 °C | 85 °C | 90 °C (95 for 5600X) | H (limit) / M |
| Ryzen 5800X3D/5700X3D | ~65-70 °C | 70-90 °C | 80-85 °C | 90 °C | H (limit) |
| Intel 12th-14th gen (Package / max core) | 70 °C on TVB parts (i9-13/14), else ~90 °C | >70 °C: one 100 MHz TVB bin; 100 °C hard throttle | 85-90 °C | 100 °C (TjMax) | H |
| Intel Core Ultra 200S (Package / max P-core) | 70 °C on TVB parts (285K), else ~95 °C | 105 °C hard throttle | 90-95 °C | 105 °C (TjMax) | M |
| NVIDIA RTX 20/30/40/50 (GPU core temp) | ~50-60 °C on V/F curve; practically ~65 °C | ~1 bin (15 MHz, <1 %) per ~5-10 °C; target 83 °C | 80 °C | 85-88 °C (near slowdown) | M |
| NVIDIA hot spot (RTX 20-40; hidden on RTX 50) | — (not used by boost as primary) | — | do not drive fans from it | >105 °C or core-to-hotspot delta >25 °C | M |
| NVIDIA GDDR6X memory junction (RTX 30) | <95 °C | throttle near 110 °C | 95-100 °C if exposed | 105 °C | M |
| AMD Radeon RX 6000/7000/9000 (edge temp) | edge <~80 °C | — | edge 80 °C | edge >90 °C | M |
| AMD Radeon junction/hotspot | <~100 °C | throttle at 110 °C | (vendor fan curve targets ~85-95 °C junction) | 105 °C sustained; 110 °C = throttling | H (110 °C spec) |
| AMD Radeon memory (GDDR6) | <90 °C | — | 95 °C | 100 °C | L |
| NVMe PCIe 3/4 (Composite) | <70 °C | throttle ~80-85 °C (WCTEMP) | 70 °C (case fan input only if logged) | 80 °C | M |
| NVMe PCIe 5 (e.g. Crucial T700) | <70 °C | throttle 81 °C, shutdown 90 °C | 70-75 °C | 80 °C | M |
| Chipset (X670E/B650/X870E, Z790) | <70 °C | no documented throttle | not a primary input | 85 °C (90+ = airflow problem) | L |
| VRM (MOS sensor) | <90 °C | power stages rated ~125 °C junction | not a primary input | 100 °C | L |

Practical reading of the table for curve design:

- "Quiet" profile: flat/low fan until ~60-65 °C component temp, ramp so the steady-state
  gaming temp lands in the "no measurable loss" column, reach 100 % at the "100 % fan by"
  value, never above the alarm value.
- "Performance" profile: shift the whole curve 10-15 °C lower; reach 100 % ~10 °C below
  the quiet profile's 100 % point.

---

## 2. Key rules

### 2.1 AMD Ryzen

1. **Ryzen 7000/9000 non-X3D TjMax is 95 °C and running there is by design.** AMD states
   Zen 4 is designed to sit at TjMax 24/7 under all-core load without damage; the boost
   algorithm deliberately consumes all thermal headroom. 95 °C in Cinebench is not a fault.
   Source: AMD statements reported by [Tom's Hardware 7950X review](https://www.tomshardware.com/reviews/amd-ryzen-9-7950x-ryzen-5-7600x-cpu-review/5),
   [Tom's forum summary](https://forums.tomshardware.com/threads/amd-says-ryzen-7000-is-designed-to-hit-the-thermal-limit-as-much-as-possible.3779253). **H**
2. **Precision Boost 2 is continuous, not stepped: clocks drop gradually as temperature rises,
   long before TjMax.** GamersNexus swept a Ryzen 9 3900X (stock, PBO off) across Tdie:
   ~4225 MHz at 36 °C, ~4200 at 55 °C, ~4150 at 61 °C, ~4075 at 71 °C, ~4050 at 78 °C,
   ~3975 at 84 °C. That is ~1-2 MHz/°C below 55 °C and ~8 MHz/°C between 55 and 84 °C;
   Cinebench gained ~4 % going from 84 °C to 55 °C.
   Source: [GamersNexus, Ryzen thermals matter](https://www.gamersnexus.net/news-pc/3492-ryzen-cpu-thermals-matter-coolers-and-cases). **M** (Zen 2 data; Zen 3/4/5 behave the same way qualitatively, magnitudes not measured per-degree)
3. **Rule of thumb for Ryzen: each 10 °C less under all-core load is worth ~1-2 % clock;
   below ~60 °C the gain is <0.5 %/10 °C.** Derived from rule 2. For gaming (light, bursty
   load) the effect is smaller still, because cores rarely approach TjMax. **M**
4. **Ryzen 7000 X3D limit is 89 °C; 5800X3D/5700X3D is 90 °C; Ryzen 9000 X3D (9800X3D,
   9950X3D) is back to 95 °C** because the V-cache die moved under the CCD, putting the CCD
   next to the IHS. Sources: [Digital Trends 9800X3D vs 7800X3D](https://digitaltrends.com/computing/amd-ryzen-7-9800x3d-vs-ryzen-7-7800x3d),
   [Profesional Review: Ryzen 7000X3D TjMax 89 °C](https://www.profesionalreview.com/2023/01/07/amd-ryzen-7000-x3d-tjmax-89/amp/),
   5800X3D 90 °C per AMD spec sheet copies (e.g. [vedantcomputers listing](https://www.vedantcomputers.com/amd-ryzen-7-5800x3d-processor)). **H** for 7000X3D/9800X3D, **M** for 9950X3D (verify on amd.com product page).
5. **Ryzen 5000 limits: 5800X/5900X/5950X 90 °C, 5600X 95 °C; AMD calls up to ~90-95 °C
   under load "typical and by design".** Source: [PC Gamer on AMD Zen 3 thermals](https://www.pcgamer.com/amd-views-ryzen-5000-cpu-temperatures-up-to-95c-as-typical-and-by-design). **H**
6. **Tctl vs Tdie: only Zen 1/Zen+ X parts have a fixed offset** (1700X/1800X/1600X +20 °C,
   2700X +10 °C). Zen 2 and later report Tctl = Tdie at stock; some boards add a custom
   offset via BIOS (MI skew). Fan control on Ryzen should use **CPU (Tctl/Tdie)** in HWiNFO /
   LibreHardwareMonitor "Core (Tctl/Tdie)". Sources: [HWiNFO forum on Tctl offset](https://www.hwinfo.com/forum/threads/is-cpu-tctl-value-is-still-a-tdie-value-on-ryzen.4977/),
   [Guru3D on 20 °C offset](https://www.guru3d.com/story/amd-ryzen-7-have-a-temperature-20-degree-c-reporting-offset/). **H**
7. **CCD temps (CCD1/CCD2 Tdie) spike faster than Tctl and are noisier.** Tctl is roughly the
   max of the CCDs, filtered. Use Tctl as the curve input with a 3-10 s hysteresis/averaging;
   CCD temps jump 10-20 °C within milliseconds when a single core boosts, which causes fan
   "revving" if used directly. **M** (community consensus; HWiNFO documentation of sensors)
8. **Typical Ryzen temps (good 240-360 mm AIO or large tower, 22-25 °C room):**
   idle 35-50 °C (Ryzen 7000/9000 idle higher than Intel because of IOD and thick IHS);
   gaming 60-80 °C (X3D: 55-75 °C); all-core rendering 85-95 °C (non-X3D 7000 frequently 95).
   Sources: reviews (TPU, Tom's) above; **M**.
9. **PBO and Curve Optimizer raise clocks by consuming thermal headroom; board "thermal
   limit" options (e.g. ASUS PBO Enhancement 90 °C, or manual 80-85 °C) cap Tctl and reduce
   temperatures at a small multithreaded cost.** If a user sets a lower thermal limit, the
   fan curve's 100 % point should move to that limit minus ~5 °C.
   Source: [Guru3D on ASUS PBO Enhancement levels](https://www.guru3d.com/story/asus-introduces-the-exclusive-pbo-enhancement-for-amd-x670-and-b650-motherboards). **M**

### 2.2 Intel

10. **TjMax: 12th-14th gen desktop = 100 °C; Core Ultra 200S (Arrow Lake) = 105 °C.**
    At TjMax PROCHOT throttles hard. i9-13900K/14900K reach 100 °C within seconds in
    all-core loads with most air coolers at unlocked power limits.
    Sources: [TechPowerUp i9-14900K review](https://techpowerup.com/review/intel-core-i9-14900k/24.html),
    [Lowyat: Arrow Lake max temp 105 °C](https://lowyat.net/2024/327021/intel-arrow-lake-max-temp),
    [Tom's Hardware 285K cooling test](https://www.tomshardware.com/pc-components/cooling/intel-core-ultra-9-285k-cooling-testing-how-much-does-it-take-to-keep-arrow-lake-cool-in-msis-mpg-gungnir-300r-airflow-pc-case). **H**
11. **Thermal Velocity Boost (i9 only, e.g. 13900K 5.8 GHz, 14900K 6.0 GHz, 285K 5.7 GHz)
    adds the top 100 MHz bin only while the package is below ~70 °C.** Above 70 °C the CPU
    loses exactly that bin (~1.5-2 % single-thread peak). Non-TVB parts (i5/i7) have no
    comparable cliff below TjMax. Intel's 0x125/0x129 microcode series fixed an eTVB bug
    where Raptor Lake did not drop the bin above 70 °C as specified.
    Sources: [Tom's Hardware TVB definition](https://www.tomshardware.com/features/intel-thermal-velocity-boost-glossary-definition-tvb),
    [WikiChip TVB](https://en.wikichip.org/wiki/intel/thermal_velocity_boost),
    [SkatterBencher TVB](https://skatterbencher.com/intel-thermal-velocity-boost/). **H** (threshold) / **M** (microcode detail)
12. **Intel turbo below TjMax is limited by power (PL1/PL2/ICCMax) far more than by
    temperature.** So on Intel, "no perf loss" is basically "never hit TjMax"; going from
    85 °C to 65 °C gains little except the TVB bin and slightly lower leakage power.
    Contrast: Adaptive Boost (i9-11900K) explicitly allows boost up to 100 °C.
    Source: [Tom's Hardware on Adaptive Boost](https://www.tomshardware.com/news/intel-adaptive-boost-technology-rocket-lake-amd). **M**
13. **Sensor choice on Intel: use "CPU Package" (≈ max of cores) or "Core Max".** P-cores
    run hottest; E-core clusters typically 5-15 °C cooler; per-core spread of 10+ °C on
    Raptor Lake is normal (hot cores 4/5 are common). Core Ultra 200S spreads heat better
    (P/E cores interleaved) and runs cooler in gaming. **M**
14. **Typical Intel temps:** 14900K gaming 60-85 °C depending on cooler and power limits;
    all-core at Intel default 253 W often 95-100 °C. 285K: TechSpot measured 84 °C peak
    with a 360 mm AIO; unlimited power can hit 105 °C even on strong coolers.
    Sources: [TechSpot 285K review](https://www.techspot.com/review/2911-intel-core-ultra-9-285k/),
    [Tom's 285K cooling test](https://www.tomshardware.com/pc-components/cooling/intel-core-ultra-9-285k-cooling-testing-how-much-does-it-take-to-keep-arrow-lake-cool-in-msis-mpg-gungnir-300r-airflow-pc-case). **M**
15. **Raptor Lake degradation (Vmin shift) is voltage-driven, not temperature-driven**, but
    heat accelerates it. Do not use a "loud" curve as a substitute for the 0x12B+ microcode
    and Intel Default Settings; but for 13th/14th gen i7/i9 keep sustained temps <90 °C where
    noise allows. **M**

### 2.3 NVIDIA GPUs (GPU Boost 3.0 / 4.0, RTX 20-50)

16. **GPU Boost lowers clock in fixed bins as temperature rises, well below the temperature
    target.** Bin = 15 MHz on Turing/Ampere/Ada/Blackwell (13 MHz on Pascal). TechPowerUp
    measured on Ampere about one 15 MHz bin per ~5 °C, with steps observed around
    70/75/80 °C; earlier bins can occur from ~40-60 °C on some cards.
    Sources: TechPowerUp RTX 3080 FE review, GPU Boost page (techpowerup.com/review/nvidia-geforce-rtx-3080-founders-edition/39.html),
    [TechPowerUp GTX 1080 review (13 MHz steps)](https://techpowerup.com/review/nvidia-geforce-gtx-1080/29.html),
    [EVGA forum (15 MHz bin)](https://forums.evga.com/Guide-How-to-force-max-voltage-curve-overclock-with-msi-afterburner-m2820280-p9.aspx). **M**
    (NVIDIA never published the step table; it varies per SKU/vBIOS.)
17. **Magnitude is tiny: going from 80 °C to 60 °C typically buys 2-4 bins = 30-60 MHz =
    1.5-3 % clock, <2 % fps.** Higher GPU temperature also raises leakage, so under a power
    limit a cooler card also gains a little through lower power per clock. **M**
18. **Default temperature target is 83 °C (Turing, Ampere, Ada; Blackwell similar);
    "GPU max operating temp" on nvidia.com is ~88-93 °C depending on SKU (e.g. RTX 4090
    listed 90 °C).** At the target, GPU Boost 4.0 settles on a clock plateau between boost
    and base instead of collapsing to base; near the slowdown temperature it throttles hard.
    Sources: [HotHardware RTX 4090 review (83 °C target)](https://hothardware.com/reviews/nvidia-geforce-rtx-4090-gpu-review?page=5),
    [SkatterBencher GPU Boost 4.0](https://skatterbencher.com/nvidia-gpu-boost-4-0/),
    [AnandTech on temperature-based boost (GPU Boost 2.0)](https://at-web1.www.anandtech.com/show/6760/nvidias-geforce-gtx-titan-part-1/5). **H** (83 °C) / **M** (max values per SKU)
19. **Our RTX 2070 measurement (1950 → 1935 MHz, one bin, at ~83 °C) is consistent**: 83 °C
    is exactly the Turing default temperature target, so a one-bin drop there is expected.
    It also suggests that card had already "spent" its lower-temperature bins or that its
    vBIOS steps are sparse below 80 °C. Expect further bin losses above 83 °C and fast
    collapse near ~88-89 °C (Turing slowdown). **M**
20. **Fan-curve end-point for NVIDIA: reach 100 % fan by ~80 °C GPU core temp.** That keeps
    the card below the 83 °C target, so the clock never hits the plateau. For "quiet", target
    a steady-state gaming temp of 70-75 °C (loses ~1-3 bins vs 60 °C, inaudible fps impact). **M**
21. **Hot spot is not the boost control input on NVIDIA and is hidden on RTX 50.** RTX 20-40
    expose "GPU Hot Spot" (typically core + 10-15 °C, up to +20 °C acceptable); RTX 50
    removed it from the public API (HWiNFO/GPU-Z no longer show it; HWMonitor 1.65 re-reads it
    unofficially). Use it only as a diagnostic: delta >25-30 °C indicates paste/pump-out/mount
    problems. Sources: [HWiNFO forum RTX 5080 hotspot](https://www.hwinfo.com/forum/threads/nvidia-rtx-5080-incorrect-gpu-hotspot-temp-missing-3rd-fan-speed-summary-info.10306/post-52969),
    [Club386 on hidden RTX 50 hotspot](https://www.club386.com/nvidia-geforce-rtx-50-hotspot-sensor-exposed-mods/). **H** (removal) / **M** (delta thresholds)
22. **GDDR6X memory junction (RTX 3080/3090/3070 Ti/3080 Ti, early 40-series) throttles at
    ~110 °C; Micron's operating spec is quoted as 95 °C (some cite 105 °C).** RTX 3080 FE hit
    ~102 °C in gaming, 110 °C mining. RTX 40 GDDR6X and RTX 50 GDDR7 run much cooler
    (typically 60-85 °C). Memory temp responds mostly to case airflow and backplate cooling,
    weakly to GPU fan speed.
    Source: [Tom's Hardware: GDDR6X hits 110 °C](https://www.tomshardware.com/news/hwinfo64-adds-gddr6x-temp-monitoring-rtx30series). **M**
23. **Power limit usually binds before thermal limit on NVIDIA.** At stock, most RTX 30/40/50
    cards in games are limited by power (Pwr) or voltage reliability (VRel), not temperature.
    A quieter fan curve that keeps the card under 80 °C costs nearly nothing.
24. **How to tell thermal vs power limiting:**
    `nvidia-smi --query-gpu=temperature.gpu,clocks.sm,power.draw,power.limit,clocks_event_reasons.active,clocks_event_reasons.sw_power_cap,clocks_event_reasons.sw_thermal_slowdown,clocks_event_reasons.hw_thermal_slowdown,clocks_event_reasons.hw_slowdown --format=csv -l 1`
    (older drivers: `clocks_throttle_reasons.*`). `sw_power_cap` = power limited (fans won't
    help much); `sw_thermal_slowdown` = above the GPU's thermal target, fans help;
    `hw_thermal_slowdown` = emergency, clocks cut ≥50 %. NVML: `nvmlDeviceGetCurrentClocksEventReasons`.
    **Caveat:** the small temperature-dependent V/F bin drops (rule 16) do **not** set any
    throttle flag; detect them by logging clock vs temperature at constant load/power.
    GPU-Z "PerfCap Reason" shows the same (Pwr/VRel/VOp/Thrm).
    Sources: [Netdata throttle reasons guide](https://www.netdata.cloud/guides/nvidia-gpu/nvidia-gpu-throttle-reasons/),
    [NVIDIA cuda-python ClocksEventReasons](https://nvidia.github.io/cuda-python/cuda-core/1.2.1/generated/cuda.core.system.typing.ClocksEventReasons.html). **H**

### 2.4 AMD Radeon (RDNA 2/3/4)

25. **AMD controls on junction (hotspot) temperature; 110 °C junction is the throttle point and
    "within spec".** AMD: RX 5700 series throttles only when the hottest point reaches 110 °C
    and that this is expected during gaming. Radeon VII: 110 °C throttle, 118 °C software
    shutdown, 120 °C hardware cutoff. The same 110 °C junction limit applies to RX 6000/7000/9000.
    Sources: [Tom's Hardware: AMD explains 110 °C](https://tomshardware.com/news/amd-rx-5700-graphics-card-thermal-management,40144.html),
    [PC Gamer](https://pcgamer.com/fretting-over-high-gpu-temps-amd-says-110c-is-expected-and-within-spec-on-navi),
    [Legit Reviews Radeon VII](https://legitreviews.com/amd-radeon-vii-16gb-video-card-review_210489/13). **H**
26. **Edge temp is the nearest equivalent to NVIDIA "GPU temperature"; typical edge-to-junction
    delta is 15-25 °C.** Delta >30 °C (e.g. reference RX 7900 XTX vapor-chamber defect, 2022-23)
    signals a cooler fault and caused 110 °C hotspot + throttling.
    Sources: [Tom's Hardware RX 7900 XTX 110 °C hotspot reports](https://www.tomshardware.com/news/amd-gpu-partner-collects-rx-7900-xtx-110c-hotspot-user-reports),
    [AMD community thread](https://community.amd.com:443/t5/graphics-cards/reference-7900-xtx-110c-junction-temp-and-maxed-fan-speed-on/m-p/571387). **H**
27. **Fan-curve input for Radeon: use edge temp for the case-fan curve; let the card's own
    firmware handle its fans (it already targets junction, typically 85-95 °C on partner
    cards).** If driving the card fans via software, 100 % by junction 100 °C / edge 80 °C. **M**
28. **Radeon memory (GDDR6) temperature: 9070 XT partner cards commonly show 85-90 °C; AMD's
    reported "memory" value is the hottest junction.** No official throttle point published;
    community quotes range 95-108 °C. Treat >100 °C as alarm. Memory temp barely responds to
    GPU fan speed, more to case airflow.
    Source: [TechPowerUp news: 90 °C VRAM on RX 9070 XT customs](https://techpowerup.com/333594/leakers-record-90-c-vram-temperatures-on-unnamed-radeon-rx-9070-xt-custom-cards),
    [TPU forum thread](https://techpowerup.com/forums/threads/is-rx-9070-vram-temperature-regular-value-or-hotspot.334188/page-7). **L**
29. **RDNA clocks also decline continuously with temperature (like Precision Boost), but the
    measurable loss below ~95 °C junction is small (<2-3 %);** at 110 °C junction clocks drop
    sharply. **L-M** (no per-degree measurement found)

### 2.5 Storage, chipset, VRM

30. **NVMe controllers expose two NVMe-spec thresholds: WCTEMP (warning, throttle starts) and
    CCTEMP (critical).** Typical PCIe 3/4 drives: WCTEMP ~80-85 °C, CCTEMP ~85-90 °C.
    PCIe 5 Crucial T700: throttle at 81 °C, shutdown at 90 °C; without heatsink it drops to
    ~0.1 GB/s in sustained tests.
    Sources: [LanOC T700 review (81/90 °C)](https://lanoc.org/review/storage/crucial-t700-pcie-gen5-nvme-m-2-ssd-2tb?showall=1),
    [HotHardware: T700 throttles without cooler](https://hothardware.com/news/crucial-t700-pcie-5-ssd-throttles-severely-without-a-cooler),
    [Tom's Hardware T700 4TB](https://www.tomshardware.com/pc-components/ssds/crucial-t700-4tb-ssd-review/2). **H** (T700) / **M** (generic thresholds)
31. **NVMe only throttles during sustained transfers (> tens of GB).** Gaming loads rarely
    push a drive past 60-65 °C. Case fans should take SSD temp into account only as a
    secondary input (max-of rule) when the drive sits under a GPU or has no heatsink; a
    gentle ramp from 60 to 75 °C is sufficient. NAND itself prefers warm (40-70 °C) for writes;
    over-cooling is not beneficial. **M**
32. **Chipset (AMD Promontory 21: X670E/X870E have two, B650/X870 one; Intel Z790 PCH):**
    50-75 °C is common; ~70 °C under drive/USB load is normal on X670E; >90-100 °C has been
    reported on poorly cooled boards (Gigabyte X870E Aorus Xtreme). No published throttle point.
    A vertical GPU or a fan over the chipset heatsink drops it ~10 °C. Linux reads the
    integrated USB controller temp as proxy.
    Sources: [Tom's Hardware X870E Aorus Xtreme chipset >100 °C](https://www.tomshardware.com/pc-components/motherboards/enthusiasts-report-gigabyte-x870e-aorus-xtreme-chipset-temps-exceeding-100-c),
    [TPU forum X670E Hero chipset temp](https://techpowerup.com/forums/threads/asus-rog-x670e-hero-chipset-temperature.328578/unread). **L**
33. **VRM (MOS) sensors:** modern smart power stages are rated ~125 °C junction; boards with
    decent heatsinks stay 40-80 °C even with 250 W CPUs (Guru3D/TPU board reviews measure
    VRM heatsink temps routinely). <90 °C fine, >100 °C warn. Relevant mainly for open-air
    AIO builds without airflow over the socket area. Source: [Guru3D X670E-E VRM thermals](https://www.guru3d.com/review/asus-strix-x670e-e-gaming-wifi-review/page-17/). **L-M**

### 2.6 Ambient and seasons

34. **Component temperature rises ~1:1 with intake/ambient temperature** as long as the
    component is not already sitting at its limit (cooler thermal resistance is roughly
    constant, so ΔT over ambient stays constant at constant power). A 22 °C → 32 °C room
    raises GPU and CPU temps by ~10 °C. **H** (basic heat-transfer physics; widely confirmed)
35. **For limit-seeking parts (Ryzen 7000/9000 all-core, Intel at unlimited PL), temperature
    stays at TjMax and clocks drop instead.** Using rule 2 (~8 MHz/°C near the limit), +10 °C
    ambient costs ~80 MHz (~1.5-2 %) all-core on a Ryzen, a few bins on NVIDIA. **M**
36. **Case internal temperature = ambient + 3-10 °C** depending on airflow; GPU exhaust in
    closed cases is the main source. If the plugin can read a motherboard/system sensor,
    `case delta = system temp - ambient` is the best signal for case fan speed. **M**
37. **Seasonal headroom:** tune curves in the warmest expected room temperature or add a
    margin. Rule: steady-state load temp measured at ambient T_tune must be ≤ the "no loss"
    value minus `(T_summer_max - T_tune)`. Practical default: keep 8-10 °C margin below the
    "100 % fan by" point when tuning in winter (20 °C) for a 30 °C summer room. **M**

### 2.7 Fan-curve construction rules derived from the above

38. **Always end the curve at 100 % fan at or below the "100 % fan by" value**; a curve that
    tops out at 60-70 % leaves cooling on the table exactly when the limit approaches. **H**
39. **Use hysteresis/step-up-down times** (FanControl: "Response time" / "Hysteresis"), e.g.
    2-3 °C hysteresis and 3-5 s up / 5-10 s down on CPU curves; CPU temperatures spike
    within milliseconds and are irrelevant to heatsink/radiator mass. For AIO/custom loops
    use liquid temperature as the case/radiator fan input (thermal mass, no spikes). **H**
40. **Mix sensors with "max" for case fans** (e.g. max(CPU curve, GPU curve)), because the
    GPU dumps most heat into the case in gaming. **M**

---

## 3. Anti-patterns

1. **Panicking at 95 °C on Ryzen 7000/9000 (or 89 °C on 7000X3D).** That is the designed
   operating point under all-core load; the check is whether clocks are near rated, not the
   temperature. Ramping fans to 100 % at 75 °C on these chips only buys ~1-3 % in rendering.
2. **Driving fans directly from Ryzen CCD/core temps or Intel per-core temps** without
   averaging → constant fan revving. Use Tctl/Package with hysteresis, or liquid temp.
3. **Using GPU hot spot as the primary curve source** on NVIDIA. It is 10-20 °C above core,
   noisier, not the boost control input, and unavailable on RTX 50. On Radeon, the card
   firmware already targets junction; for case fans use edge.
4. **Curves topping out below 100 %** (e.g. max 70 % "for silence"). Silence comes from the
   low end of the curve; the top end is the safety net.
5. **Treating 110 °C Radeon junction as "fine" without checking the delta.** 110 °C with a
   35 °C edge/junction delta and falling clocks is a cooler defect, not normal behaviour.
6. **Trying to fix power-limited GPU clocks with fans.** If `sw_power_cap` is active, more
   fan only lowers leakage marginally; raise the power limit or accept it.
7. **Aggressively cooling NVMe/chipset with all case fans.** They need airflow, not 100 % fans
   tied to their sensor; add a heatsink/fan locally instead.
8. **Expecting Intel temps to scale like Ryzen.** On Intel, power limits set clocks; below
   TjMax only the TVB 70 °C bin is temperature-bound. A curve that keeps an i7 at 65 °C
   instead of 85 °C is mostly wasted noise.
9. **Assuming GDDR6X temps follow GPU fan speed.** Memory on RTX 30 cards is cooled through
   pads/backplate; case airflow over the backplate matters more.
10. **Using HWiNFO "CPU (Tctl/Tdie)" on Zen 1/Zen+ X parts without subtracting the offset**
    (+20 °C on 1700X/1800X/1600X, +10 °C on 2700X) — curves become unnecessarily loud.
11. **Tuning in winter, deploying in summer** without margin (see rule 37).
12. **Using the GPU's "zero RPM" idle mode while also forcing case fans off** in a closed case
    — the GPU then heat-soaks at idle (50-60 °C) and ramps abruptly on load. Keep a low case
    fan baseline.

---

## 4. Open questions / conflicts

- **NVIDIA bin step table**: not published. Measurements range from first drops at ~37-40 °C
  (forum, RTX 2080) to steps at 70/75/80 °C (TPU, RTX 3080). The plugin should *measure*
  per card: log `clocks.sm` vs `temperature.gpu` at a fixed load and fixed power to find
  where bins fall (our 2070: a bin at ~83 °C).
- **GDDR6X rated limit**: 95 °C (Tom's citing Micron) vs 105 °C (forum citing Micron);
  observed throttle at ~110 °C. Treat 100-105 °C as alarm.
- **Radeon memory throttle point** (RDNA 3/4 GDDR6): community claims 95-108 °C, no AMD doc.
- **Ryzen 9950X3D/9900X3D TjMax**: reported 95 °C like the 9800X3D; verify on amd.com.
- **Per-degree clock scaling for Zen 4/Zen 5** has not been published with a clean sweep like
  GamersNexus' Zen 2 test; the ~8 MHz/°C near-limit figure is extrapolated.
- **Core Ultra 200S TVB threshold**: assumed 70 °C like Raptor Lake; confirm in Intel ARK /
  datasheet for 285K.
- **Hot spot on RTX 50**: hidden by NVIDIA; repair-shop reports of ~107 °C hotspot causing
  throttling on cards with normal core temps are single-source.
- **Chipset limits** (Promontory 21, Intel PCH): no vendor maximum found; values are anecdotal.
- **Board-specific Tctl offsets** (MI skew) on Zen 2+ exist but are rare; cannot be detected
  without comparing to known idle behaviour.

---

## 5. Sources

AMD CPU
- Tom's Hardware, Ryzen 9 7950X / 5 7600X review, boost & thermals: https://www.tomshardware.com/reviews/amd-ryzen-9-7950x-ryzen-5-7600x-cpu-review/5
- Tom's forum, "AMD says Ryzen 7000 is designed to hit the thermal limit": https://forums.tomshardware.com/threads/amd-says-ryzen-7000-is-designed-to-hit-the-thermal-limit-as-much-as-possible.3779253
- PC Gamer, AMD: Zen 3 up to 95 °C typical: https://www.pcgamer.com/amd-views-ryzen-5000-cpu-temperatures-up-to-95c-as-typical-and-by-design
- GamersNexus, Ryzen thermals matter (PB2 clock vs Tdie sweep): https://www.gamersnexus.net/news-pc/3492-ryzen-cpu-thermals-matter-coolers-and-cases
- Digital Trends, 9800X3D vs 7800X3D (95 vs 89 °C): https://digitaltrends.com/computing/amd-ryzen-7-9800x3d-vs-ryzen-7-7800x3d
- Profesional Review, Ryzen 7000X3D TjMax 89 °C: https://www.profesionalreview.com/2023/01/07/amd-ryzen-7000-x3d-tjmax-89/amp/
- 5800X3D spec sheet (90 °C): https://www.vedantcomputers.com/amd-ryzen-7-5800x3d-processor
- HWiNFO forum, Tctl vs Tdie offset: https://www.hwinfo.com/forum/threads/is-cpu-tctl-value-is-still-a-tdie-value-on-ryzen.4977/
- Guru3D, Ryzen 20 °C offset: https://www.guru3d.com/story/amd-ryzen-7-have-a-temperature-20-degree-c-reporting-offset/
- Guru3D, ASUS PBO Enhancement thermal levels: https://www.guru3d.com/story/asus-introduces-the-exclusive-pbo-enhancement-for-amd-x670-and-b650-motherboards

Intel CPU
- Tom's Hardware, TVB definition: https://www.tomshardware.com/features/intel-thermal-velocity-boost-glossary-definition-tvb
- WikiChip, Thermal Velocity Boost: https://en.wikichip.org/wiki/intel/thermal_velocity_boost
- SkatterBencher, Intel TVB: https://skatterbencher.com/intel-thermal-velocity-boost/
- Tom's Hardware, Adaptive Boost: https://www.tomshardware.com/news/intel-adaptive-boost-technology-rocket-lake-amd
- TechPowerUp, i9-14900K review (temps): https://techpowerup.com/review/intel-core-i9-14900k/24.html
- Lowyat, Arrow Lake TjMax 105 °C: https://lowyat.net/2024/327021/intel-arrow-lake-max-temp
- Tom's Hardware, Core Ultra 9 285K cooling test: https://www.tomshardware.com/pc-components/cooling/intel-core-ultra-9-285k-cooling-testing-how-much-does-it-take-to-keep-arrow-lake-cool-in-msis-mpg-gungnir-300r-airflow-pc-case
- TechSpot, Core Ultra 9 285K review: https://www.techspot.com/review/2911-intel-core-ultra-9-285k/

NVIDIA GPU
- TechPowerUp, RTX 3080 FE review, GPU Boost page: https://techpowerup.com/review/nvidia-geforce-rtx-3080-founders-edition/39.html
- TechPowerUp, GTX 1080 review (13 MHz steps): https://techpowerup.com/review/nvidia-geforce-gtx-1080/29.html
- AnandTech, GPU Boost 2.0 temperature-based boosting: https://at-web1.www.anandtech.com/show/6760/nvidias-geforce-gtx-titan-part-1/5
- SkatterBencher, GPU Boost 4.0: https://skatterbencher.com/nvidia-gpu-boost-4-0/
- HotHardware, RTX 4090 review (83 °C target): https://hothardware.com/reviews/nvidia-geforce-rtx-4090-gpu-review?page=5
- EVGA forum, 15 MHz bins: https://forums.evga.com/Guide-How-to-force-max-voltage-curve-overclock-with-msi-afterburner-m2820280-p9.aspx
- Tom's Hardware, GDDR6X 110 °C: https://www.tomshardware.com/news/hwinfo64-adds-gddr6x-temp-monitoring-rtx30series
- HWiNFO forum, RTX 50 hotspot removed: https://www.hwinfo.com/forum/threads/nvidia-rtx-5080-incorrect-gpu-hotspot-temp-missing-3rd-fan-speed-summary-info.10306/post-52969
- Club386, RTX 50 hidden hotspot: https://www.club386.com/nvidia-geforce-rtx-50-hotspot-sensor-exposed-mods/
- Netdata, NVIDIA throttle reasons: https://www.netdata.cloud/guides/nvidia-gpu/nvidia-gpu-throttle-reasons/
- NVIDIA cuda-python, ClocksEventReasons: https://nvidia.github.io/cuda-python/cuda-core/1.2.1/generated/cuda.core.system.typing.ClocksEventReasons.html

AMD GPU
- Tom's Hardware, AMD explains 110 °C in spec: https://tomshardware.com/news/amd-rx-5700-graphics-card-thermal-management,40144.html
- PC Gamer, 110 °C expected: https://pcgamer.com/fretting-over-high-gpu-temps-amd-says-110c-is-expected-and-within-spec-on-navi
- Legit Reviews, Radeon VII (110/118/120 °C): https://legitreviews.com/amd-radeon-vii-16gb-video-card-review_210489/13
- Tom's Hardware, RX 7900 XTX 110 °C hotspot reports: https://www.tomshardware.com/news/amd-gpu-partner-collects-rx-7900-xtx-110c-hotspot-user-reports
- AMD community, reference 7900 XTX 110 °C: https://community.amd.com:443/t5/graphics-cards/reference-7900-xtx-110c-junction-temp-and-maxed-fan-speed-on/m-p/571387
- TechPowerUp, RX 9070 XT 90 °C VRAM: https://techpowerup.com/333594/leakers-record-90-c-vram-temperatures-on-unnamed-radeon-rx-9070-xt-custom-cards
- TechPowerUp forum, RX 9070 VRAM temp: https://techpowerup.com/forums/threads/is-rx-9070-vram-temperature-regular-value-or-hotspot.334188/page-7

Storage / board
- LanOC, Crucial T700 (81 °C throttle, 90 °C shutdown): https://lanoc.org/review/storage/crucial-t700-pcie-gen5-nvme-m-2-ssd-2tb?showall=1
- HotHardware, T700 throttles without cooler: https://hothardware.com/news/crucial-t700-pcie-5-ssd-throttles-severely-without-a-cooler
- Tom's Hardware, Crucial T700 4TB: https://www.tomshardware.com/pc-components/ssds/crucial-t700-4tb-ssd-review/2
- Tom's Hardware, X870E Aorus Xtreme chipset >100 °C: https://www.tomshardware.com/pc-components/motherboards/enthusiasts-report-gigabyte-x870e-aorus-xtreme-chipset-temps-exceeding-100-c
- TechPowerUp forum, X670E Hero chipset temperature: https://techpowerup.com/forums/threads/asus-rog-x670e-hero-chipset-temperature.328578/unread
- Guru3D, ASUS Strix X670E-E VRM thermals: https://www.guru3d.com/review/asus-strix-x670e-e-gaming-wifi-review/page-17/

Note: TechPowerUp and Igor's Lab review pages returned HTTP 403 to automated fetching; their
figures above come from search-result excerpts and should be spot-checked manually.
