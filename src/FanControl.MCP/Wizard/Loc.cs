using System.Text.Json.Nodes;

namespace FanControl.MCP.Wizard;

/// <summary>
/// User-facing texts of the wizard and planner in other languages. The English text is the key, so the catalogue
/// stays readable in code and untranslated strings simply fall back to English. Model-facing fields
/// ("instructions", ids, values) are never translated.
/// </summary>
public static class Loc
{
    public static string Normalize(string? language)
    {
        var l = (language ?? "en").Trim().ToLowerInvariant();
        return l.StartsWith("de", StringComparison.Ordinal) ? "de" : "en";
    }

    /// <summary>Picks the text for the language; use for interpolated strings.</summary>
    public static string Say(string lang, string en, string de) => lang == "de" ? de : en;

    public static string T(string lang, string en) => lang == "de" && De.TryGetValue(en, out var de) ? de : en;

    private static readonly HashSet<string> TranslatedFields = ["title", "why", "prompt", "label", "hint", "goalLabel"];

    /// <summary>Translates known user-facing fields in a JSON tree in place.</summary>
    public static T2 Translate<T2>(T2 node, string lang) where T2 : JsonNode
    {
        if (lang == "en") return node;
        Walk(node, lang);
        return node;
    }

    private static void Walk(JsonNode? node, string lang)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    var v = o[key];
                    if (TranslatedFields.Contains(key) && v is JsonValue jv && jv.TryGetValue<string>(out var s)) o[key] = T(lang, s);
                    else Walk(v, lang);
                }
                break;
            case JsonArray a:
                foreach (var item in a) Walk(item, lang);
                break;
        }
    }

    private static readonly Dictionary<string, string> De = new(StringComparer.Ordinal)
    {
        // ------------------------------------------------------------ goals
        ["Quietest without performance loss"] = "Am leisesten ohne Leistungsverlust",
        ["As quiet as possible while CPU/GPU never get hot enough to lose boost clocks. Temperatures may run warmer, performance stays at 100 %."] =
            "So leise wie möglich, ohne dass CPU/GPU so warm werden, dass sie Boost-Takt verlieren. Temperaturen dürfen höher sein, die Leistung bleibt bei 100 %.",
        ["Balanced"] = "Ausgewogen",
        ["Moderate noise with comfortable temperature headroom. Good default for mixed use."] =
            "Moderate Lautstärke mit bequemem Temperaturpuffer. Guter Standard für gemischte Nutzung.",
        ["Maximum cooling / performance"] = "Maximale Kühlung / Leistung",
        ["Lowest temperatures and maximum boost headroom (overclocking, long renders, hot rooms). Noise is secondary."] =
            "Niedrigste Temperaturen und maximaler Boost-Spielraum (Übertakten, lange Renderings, heiße Räume). Lautstärke ist zweitrangig.",
        ["Silent first"] = "Stille zuerst",
        ["Noise matters most; a small performance loss under heavy load is acceptable. Case fans may stop at idle."] =
            "Ruhe ist am wichtigsten; ein kleiner Leistungsverlust unter Volllast ist in Ordnung. Gehäuselüfter dürfen im Leerlauf stehen.",
        ["Steady and predictable"] = "Gleichmäßig und vorhersehbar",
        ["Few speed changes: fans move between a small number of steady levels (streaming, recording, sensitive to ramping)."] =
            "Wenige Drehzahlwechsel: Lüfter bewegen sich zwischen wenigen festen Stufen (Streaming, Aufnahmen, empfindlich gegen Hochdrehen).",

        // ------------------------------------------------------------ step: goal
        ["Goal"] = "Ziel",
        ["Decides where curves end, how warm components may run and how aggressive smoothing is."] =
            "Bestimmt, wo Kurven enden, wie warm Bauteile werden dürfen und wie stark geglättet wird.",
        ["What should the setup optimise for?"] = "Worauf soll das Setup optimiert werden?",
        ["How sensitive are you to fan noise?"] = "Wie empfindlich bist du bei Lüftergeräuschen?",
        ["Low"] = "Gering", ["Noise rarely bothers me"] = "Geräusche stören mich selten",
        ["Medium"] = "Mittel", ["High"] = "Hoch", ["I notice every fan"] = "Ich höre jeden Lüfter",
        ["Which fan behaviour bothers you most?"] = "Welches Lüfterverhalten stört dich am meisten?",
        ["Speeding up and down"] = "Ständiges Hoch- und Runterdrehen",
        ["Noise at idle / desktop"] = "Geräusch im Leerlauf / am Desktop",
        ["Loud under load (gaming, rendering)"] = "Laut unter Last (Spielen, Rendern)",
        ["Humming / beating between fans"] = "Brummen / Schwebung zwischen Lüftern",
        ["Motor whine or clicking at low speed"] = "Motorsurren oder Klicken bei niedriger Drehzahl",
        ["Nothing specific"] = "Nichts Bestimmtes",
        ["Anything else about your goal? (e.g. 'max quiet while gaming with headphones off')"] =
            "Noch etwas zu deinem Ziel? (z. B. „beim Spielen ohne Kopfhörer möglichst leise“)",

        // ------------------------------------------------------------ step: usage
        ["Usage & room"] = "Nutzung & Raum",
        ["Typical load pattern and room temperature set idle floors and summer headroom."] =
            "Typische Last und Raumtemperatur bestimmen Leerlauf-Untergrenzen und Sommerpuffer.",
        ["What do you mainly use the PC for?"] = "Wofür nutzt du den PC hauptsächlich?",
        ["Gaming"] = "Spielen", ["Office / browsing"] = "Büro / Surfen", ["Rendering / encoding / compiling"] = "Rendern / Kodieren / Kompilieren",
        ["AI / long GPU compute"] = "KI / lange GPU-Berechnungen", ["Streaming / recording"] = "Streaming / Aufnahmen", ["Always-on / server"] = "Dauerbetrieb / Server",
        ["Typical games or apps (optional, helps estimate CPU vs GPU load)"] = "Typische Spiele oder Programme (optional, hilft CPU- vs. GPU-Last abzuschätzen)",
        ["Typical room temperature in °C"] = "Übliche Raumtemperatur in °C",
        ["Room temperature on hot summer days in °C (optional)"] = "Raumtemperatur an heißen Sommertagen in °C (optional)",
        ["Where does the PC stand?"] = "Wo steht der PC?",
        ["On the desk"] = "Auf dem Schreibtisch", ["On the floor"] = "Auf dem Boden", ["More dust"] = "Mehr Staub",
        ["In a cabinet / enclosed desk"] = "In einem Schrank / geschlossenen Fach", ["Hot air can build up"] = "Warme Luft kann sich stauen",

        // ------------------------------------------------------------ step: case
        ["Case"] = "Gehäuse",
        ["Front panel type and size decide how much airflow fans can actually move."] =
            "Frontpanel und Größe entscheiden, wie viel Luft die Lüfter tatsächlich bewegen können.",
        ["Which case is it (brand and model)?"] = "Welches Gehäuse ist es (Hersteller und Modell)?",
        ["Case size"] = "Gehäusegröße",
        ["Small form factor / ITX"] = "Kompakt / ITX", ["Mid tower"] = "Midi-Tower", ["Full tower"] = "Big-Tower", ["Other"] = "Andere",
        ["Front panel"] = "Frontpanel",
        ["Mesh / perforated"] = "Mesh / gelocht", ["Solid with side vents"] = "Geschlossen mit seitlichen Öffnungen", ["Restrictive"] = "Bremst den Luftstrom",
        ["Glass / panoramic"] = "Glas / Panorama", ["Restrictive, often side/bottom intake"] = "Bremst, oft Ansaugung seitlich/unten",
        ["Dust filters on intakes?"] = "Staubfilter an den Einlässen?",
        ["Yes"] = "Ja", ["No"] = "Nein", ["Don't know"] = "Weiß nicht",

        // ------------------------------------------------------------ step: fans
        ["Fans & airflow"] = "Lüfter & Luftstrom",
        ["Position and direction of every fan decide which temperature should drive it and whether the case has positive pressure."] =
            "Position und Richtung jedes Lüfters entscheiden, welche Temperatur ihn steuern sollte und ob im Gehäuse Überdruck herrscht.",
        ["For each fan output: where is it, which way does it blow, size, model and how many fans hang on it? Headers with 0 RPM are probably empty. Use FanControl's 'identify' if unsure which header is which."] =
            "Für jeden Lüfterausgang: Wo sitzt er, in welche Richtung bläst er, Größe, Modell und wie viele Lüfter hängen daran? Anschlüsse mit 0 RPM sind vermutlich leer. Im Zweifel hilft FanControls „Identify“.",
        ["Front"] = "Front", ["Top, front position"] = "Oben, vordere Position", ["Top, rear position"] = "Oben, hintere Position",
        ["Top (single/other)"] = "Oben (einzeln/sonstige)", ["Rear"] = "Hinten", ["Bottom"] = "Boden", ["Side panel"] = "Seitenteil",
        ["On a radiator (AIO/loop)"] = "Am Radiator (AIO/Kreislauf)", ["CPU air cooler"] = "CPU-Luftkühler", ["Pump (AIO/loop)"] = "Pumpe (AIO/Kreislauf)",
        ["Graphics card"] = "Grafikkarte", ["Other / unknown"] = "Sonstige / unbekannt",
        ["Intake"] = "Einlass", ["Exhaust"] = "Auslass", ["Internal (CPU/GPU/pump)"] = "Intern (CPU/GPU/Pumpe)",

        // ------------------------------------------------------------ step: cpu cooling
        ["CPU cooling"] = "CPU-Kühlung",
        ["Air coolers react in seconds, AIOs follow the slow liquid temperature: different curve sources."] =
            "Luftkühler reagieren in Sekunden, AIOs folgen der trägen Wassertemperatur: unterschiedliche Kurvenquellen.",
        ["CPU model (detected, confirm or correct)"] = "CPU-Modell (erkannt, bestätigen oder korrigieren)",
        ["CPU cooler type"] = "Art des CPU-Kühlers",
        ["Air cooler (tower/top-flow)"] = "Luftkühler (Tower/Top-Flow)", ["AIO liquid cooler"] = "AIO-Wasserkühlung", ["Custom water loop"] = "Eigener Wasserkreislauf", ["Stock/boxed cooler"] = "Boxed-/Standardkühler",
        ["Cooler model (e.g. 'Thermalright Peerless Assassin 120 SE', 'Arctic Liquid Freezer III 360')"] =
            "Kühlermodell (z. B. „Thermalright Peerless Assassin 120 SE“, „Arctic Liquid Freezer III 360“)",
        ["AIO/loop: radiator size in mm (240, 280, 360, 420)"] = "AIO/Kreislauf: Radiatorgröße in mm (240, 280, 360, 420)",
        ["AIO/loop: where is the radiator?"] = "AIO/Kreislauf: Wo sitzt der Radiator?",
        ["Top"] = "Oben", ["Side"] = "Seite", ["Rear (120/140)"] = "Hinten (120/140)",
        ["AIO/loop: radiator fans blow"] = "AIO/Kreislauf: Die Radiatorlüfter blasen",
        ["Into the case (intake)"] = "Ins Gehäuse (Einlass)", ["Out of the case (exhaust)"] = "Aus dem Gehäuse (Auslass)",
        ["AIO/loop: where is the pump connected?"] = "AIO/Kreislauf: Wo ist die Pumpe angeschlossen?",
        ["AIO_PUMP / W_PUMP header"] = "AIO_PUMP- / W_PUMP-Anschluss", ["CPU_FAN header"] = "CPU_FAN-Anschluss",
        ["SATA power (fixed speed)"] = "SATA-Strom (feste Drehzahl)", ["USB / vendor hub"] = "USB / Hersteller-Hub",

        // ------------------------------------------------------------ step: gpu
        ["Exact model and cooler type decide fan minimums and how warm it may run."] =
            "Genaues Modell und Kühlerart bestimmen Lüfter-Minimum und wie warm sie werden darf.",
        ["Graphics card model including the board partner (detected, confirm), e.g. 'MSI RTX 4070 Gaming X Trio'"] =
            "Grafikkartenmodell inkl. Hersteller (erkannt, bitte bestätigen), z. B. „MSI RTX 4070 Gaming X Trio“",
        ["GPU cooler type"] = "Art des GPU-Kühlers",
        ["Open-air (2-3 axial fans)"] = "Offen (2–3 Axiallüfter)", ["Blower (single radial fan)"] = "Blower (ein Radiallüfter)",
        ["Water-cooled / hybrid"] = "Wassergekühlt / Hybrid", ["Passive"] = "Passiv",
        ["How is the GPU mounted?"] = "Wie ist die GPU eingebaut?",
        ["Normal (horizontal)"] = "Normal (waagerecht)", ["Vertical mount"] = "Senkrecht", ["Check clearance to the side panel"] = "Abstand zum Seitenteil prüfen",
        ["Should the GPU fans stop at idle (zero-RPM) if the card supports it?"] = "Sollen die GPU-Lüfter im Leerlauf stehen (Zero-RPM), wenn die Karte das kann?",

        // ------------------------------------------------------------ step: software
        ["BIOS & other software"] = "BIOS & andere Software",
        ["Another tool or a bad BIOS fallback can override or undermine FanControl."] =
            "Ein anderes Programm oder eine schlechte BIOS-Rückfallebene kann FanControl übersteuern oder aushebeln.",
        ["What do the BIOS fan settings do if FanControl is not running?"] = "Was machen die BIOS-Lüftereinstellungen, wenn FanControl nicht läuft?",
        ["A sane BIOS curve"] = "Eine vernünftige BIOS-Kurve", ["Recommended fallback"] = "Empfohlene Rückfallebene",
        ["Full speed"] = "Volle Drehzahl", ["Safe but loud"] = "Sicher, aber laut",
        ["Very quiet BIOS curve"] = "Sehr leise BIOS-Kurve", ["Risky if FanControl crashes"] = "Riskant, falls FanControl abstürzt",
        ["Are the motherboard fan headers set to the right mode (PWM for 4-pin fans, DC for 3-pin)?"] =
            "Sind die Lüfteranschlüsse im BIOS richtig eingestellt (PWM für 4-Pin-, DC für 3-Pin-Lüfter)?",
        ["Left on Auto"] = "Auf Auto gelassen",
        ["Other fan/RGB/GPU tools installed (detected ones are listed)"] = "Weitere Lüfter-/RGB-/GPU-Programme installiert (erkannte sind eingetragen)",
        ["Does FanControl start with Windows (or run as service)?"] = "Startet FanControl mit Windows (oder läuft als Dienst)?",
    };
}
