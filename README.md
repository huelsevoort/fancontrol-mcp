# FanControl MCP Plugin

Ein [FanControl](https://getfancontrol.com)-Plugin, das einen **MCP-Server (Model Context Protocol)** direkt in FanControl startet.
So können KI-Assistenten wie Claude Temperaturen und Lüfterdrehzahlen lesen, Lüfter steuern und Profile wechseln.

```
Claude / MCP-Client ──HTTP──▶ http://127.0.0.1:48620/mcp ──▶ FanControl.MCP.dll (im FanControl-Prozess) ──▶ FanControl-Engine
```

- Eine einzelne DLL ohne Abhängigkeiten, läuft im **Desktop-App-** und im **Windows-Service-Modus** von FanControl
- MCP-Transport „Streamable HTTP“ (JSON-Antworten), Protokollversionen 2024-11-05 bis 2025-11-25
- Lauscht nur auf `127.0.0.1`, prüft den `Origin`-Header (Schutz vor DNS-Rebinding), optional Bearer-Token
- Getestet mit FanControl **V285** (.NET 10)

## Tools

| Tool | Art | Beschreibung |
|---|---|---|
| `get_status` | lesen | Plugin-/FanControl-Version, Modus, ob die Engine erreichbar ist, aktives Profil |
| `get_snapshot` | lesen | Alles auf einmal: Temperaturen, Lüfter-RPM, Controls, virtuelle Sensoren |
| `list_temperatures` | lesen | Alle Temperatursensoren inkl. Custom Sensors (Mix, Offset, Durchschnitt …) |
| `list_fans` | lesen | Alle Drehzahlsensoren (RPM) |
| `list_controls` | lesen | Lüfter-/Pumpenausgänge: aktuelle %, manuell/automatisch, Kurve, Step-Up/Down, Start/Stop |
| `list_fan_curves` | lesen | Alle Kurven mit Typ, aktueller Ausgabe und vollständigen Einstellungen (Punkte, Quelle, Hysterese) |
| `list_configs` | lesen | Verfügbare Konfigurationsdateien (Profile) |
| `set_fan_speed` | schreiben | Control auf feste Drehzahl in % setzen (manueller Modus) |
| `set_fan_automatic` | schreiben | Control zurück auf seine Kurve, optional eine andere Kurve zuweisen |
| `set_control_enabled` | schreiben | Control aktivieren / an BIOS zurückgeben |
| `load_config` | schreiben | Anderes Profil laden |
| `save_config` | schreiben | Aktuellen Zustand ins aktive Profil speichern |
| `set_virtual_sensor` | schreiben | Wert eines virtuellen Temperatursensors „MCP Virtual N“ setzen |
| `get_fan_curve_guide` | lesen | Eingebauter Best-Practice-Leitfaden (siehe unten) |
| `analyze_fan_setup` | lesen | Prüft das aktuelle Setup gegen die Best Practices und liefert Befunde mit Empfehlungen |
| `update_fan_curve` | schreiben | Graph-/Linear-/Flat-Kurve ändern: Punkte, Temperaturquelle, Hysterese, Reaktionszeit |
| `create_fan_curve` / `delete_fan_curve` | schreiben | Kurve anlegen bzw. löschen (nur, wenn sie nicht mehr benutzt wird) |
| `update_control_tuning` | schreiben | Step Up/Down, Start/Stop, Minimum, Offset eines Ausgangs |
| `create_time_average_sensor` / `delete_custom_sensor` | schreiben | Geglätteten Sensor anlegen bzw. Custom Sensor löschen |

Alle Änderungen wirken nach etwa einer Sekunde, werden aber **erst mit `save_config` gespeichert**.
Ein Neustart von FanControl oder `load_config` setzt sie zurück.

## Eingebauter Skill: Best Practices für Lüfterkurven

Das Plugin bringt einen Tuning-Leitfaden mit ([Guides/fan-curve-best-practices.md](src/FanControl.MCP/Guides/fan-curve-best-practices.md),
in die DLL eingebettet). Er deckt ab: Sicherheitsgrenzen je Bauteil (Ryzen, Intel, GPU, Wassertemperatur), die passende
Temperaturquelle je Lüfter, Kurvenform und Vorlagen, Hysterese und Reaktionszeit, Step Up/Down, Start/Stop, Luftstrom,
eine Tabelle „Symptom → Ursache → Lösung“ und einen Arbeitsablauf (erst analysieren, Vorschlag machen, Bestätigung abwarten, anwenden, beobachten, erst dann speichern).

Der Leitfaden kommt auf drei Wegen zum Modell, damit jeder MCP-Client ihn bekommt:

| Weg | Wofür |
|---|---|
| **Server-Instructions** | Weisen das Modell an, vor jeder Kurvenänderung den Leitfaden zu lesen und `analyze_fan_setup` aufzurufen |
| **Tool** `get_fan_curve_guide` | Für Clients, die nur Tools nutzen; das Modell holt ihn sich selbst |
| **Resource** `fancontrol://guides/fan-curve-best-practices` | Zum Anhängen per @-Erwähnung (z. B. in Claude Code) |
| **Prompts** (Slash-Commands) | `tune_fan_curves` (Ziel: silent/balanced/performance, Fokus), `diagnose_fan_noise` (Symptom), `review_fan_setup` (nur lesen). Der Leitfaden wird jeweils mitgeliefert |

In Claude Code erscheinen die Prompts als `/mcp__fancontrol__tune_fan_curves` usw.

`analyze_fan_setup` prüft unter anderem: 0 % an CPU-/Pumpen-/Radiatorlüftern, Kurven, die nie oder zu spät 100 % erreichen,
ungeglättete CPU-Temperatur als Quelle, Radiatorlüfter auf CPU- statt Wassertemperatur, eine Untergrenze unter dem Stall-Wert,
fallende Kurven, fehlende Quellen, vergessene manuelle Overrides, fehlende Kalibrierung und ungenutzte Kurven.
Die Rolle eines Ausgangs (CPU, Pumpe, Radiator, Gehäuse) wird aus dem Namen geraten. Eindeutige Namen in FanControl verbessern also die Analyse.

### Virtuelle Sensoren

Das Plugin legt `MCP Virtual 1`–`4` als Temperaturquellen in FanControl an. Du kannst sie in eigenen Kurven verwenden,
z. B. eine Mix-Kurve „Max(CPU-Kurve, Boost-Kurve)“, deren Boost-Kurve am virtuellen Sensor hängt. Dann kann die KI einen
„Boost“ auslösen, ohne deine Konfiguration anzufassen. Diese Funktion nutzt nur die offizielle Plugin-API und funktioniert
auch, falls ein künftiges FanControl-Update den Engine-Zugriff bricht.

## Installation

Voraussetzungen: FanControl (V285 oder neuer) und das [.NET 10 SDK](https://dotnet.microsoft.com/download) zum Bauen.

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Das Skript baut das Plugin, kopiert `FanControl.MCP.dll` nach `C:\Program Files (x86)\FanControl\Plugins`
(fragt per UAC nach Adminrechten) und startet FanControl neu. Optionen: `-FanControlDir <Pfad>`, `-NoRestart`, `-Uninstall`.

Manuell: `dotnet build src/FanControl.MCP -c Release`, dann
`src/FanControl.MCP/bin/Release/net10.0-windows10.0.22621.0/FanControl.MCP.dll` in den `Plugins`-Ordner kopieren und FanControl neu starten.

Ob der Server läuft, steht in `C:\Program Files (x86)\FanControl\log.txt`:
`[FanControl.MCP] MCP server listening on http://127.0.0.1:48620/mcp`

## Mit Claude verbinden

**Claude Code**

```bash
claude mcp add --transport http fancontrol http://127.0.0.1:48620/mcp
```

**Claude Desktop** (oder jeder Client, der nur stdio kann), über die Brücke [`mcp-remote`](https://www.npmjs.com/package/mcp-remote) (Node.js nötig), in `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "fancontrol": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://127.0.0.1:48620/mcp"]
    }
  }
}
```

Mit Token: `"args": ["-y", "mcp-remote", "http://127.0.0.1:48620/mcp", "--header", "Authorization: Bearer DEIN_TOKEN"]`
bzw. bei Claude Code `--header "Authorization: Bearer DEIN_TOKEN"`.

## Einstellungen

Beim ersten Start entsteht `Plugins\FanControl.MCP.json`:

```json
{
  "port": 48620,
  "bindAddress": "127.0.0.1",
  "authToken": "",
  "allowWrite": true,
  "minimumManualPercent": 0,
  "virtualSensorCount": 4
}
```

| Einstellung | Bedeutung |
|---|---|
| `port` | TCP-Port des Endpunkts |
| `bindAddress` | Bind-Adresse. Auf `127.0.0.1` lassen, außer du sicherst den Zugang anders ab |
| `authToken` | Wenn gesetzt, muss jeder Request `Authorization: Bearer <token>` senden |
| `allowWrite` | `false` = reiner Lesemodus, alle schreibenden Tools werden abgelehnt |
| `minimumManualPercent` | Sicherheitsuntergrenze für `set_fan_speed` (z. B. `30`) |
| `virtualSensorCount` | Anzahl virtueller Sensoren (0–16) |

Nach Änderungen FanControl neu starten (oder in FanControl „Refresh“).

## Wie es funktioniert

Die offizielle Plugin-API von FanControl (`IPlugin`, `IPlugin2`, `IPlugin3`) erlaubt nur, **eigene** Sensoren hinzuzufügen.
Für vollen Zugriff sucht das Plugin die laufende `FanControlEngine` im eigenen Prozess:
FanControl abonniert das `RefreshRequested`-Event des Plugins (`IPlugin3`). Von dort führt die Kette der Event-Abonnenten
`PluginBackendProvider → ComputerAccessLayer → FanControlEngine`. Die Suche ist auf FanControl-eigene Typen begrenzt
und läuft in beiden Modi (Desktop-App und Service).

Schreibzugriffe laufen im Desktop-Modus über den WPF-Dispatcher, im Service-Modus unter dem Lock der Engine.
Eine Änderung an einem Control erzeugt eine Kopie seiner `SerializableControlConfig` und übergibt sie mit `UpdateConfig`,
also genau wie die FanControl-Oberfläche.

**Einschränkung:** Engine-Zugriff nutzt interne (aber öffentliche) Typen aus `FanControl.Library.dll`. Ändert ein
FanControl-Update diese, liefern die Engine-Tools eine Fehlermeldung statt FanControl zum Absturz zu bringen.
Neu bauen gegen die neue Version (`install.ps1`) behebt das meist. Virtuelle Sensoren funktionieren weiter.

## Testen

```powershell
# Protokolltest ohne FanControl (Plugin wird im Testprozess gehostet)
dotnet run --project tests/SmokeTest

# Gegen das laufende FanControl, nur lesend
dotnet run --project tests/SmokeTest -- http://127.0.0.1:48620/mcp

# Mit Schreibtests: Control kurz auf 60 % und zurück, Tuning ändern und zurück;
# mit --curve zusätzlich Kurve ändern und zurück, Testkurve und Testsensor anlegen und wieder löschen
dotnet run --project tests/SmokeTest -- http://127.0.0.1:48620/mcp --write --control /lpc/nct6799d/control/0 --curve "Top / Back"
```

## Projektstruktur

```
src/FanControl.MCP/
  McpPlugin.cs          Plugin-Einstieg (IPlugin3), Lebenszyklus, Engine-Suche
  FanControlTools.cs    Definition aller MCP-Tools
  TuningSkill.cs        eingebauter Skill: Resource + Prompts zum Leitfaden
  SetupAnalyzer.cs      Regelprüfung des Setups (arbeitet auf JSON, offline testbar)
  Guides/               Best-Practice-Leitfaden (eingebettete Ressource)
  Engine/Changes.cs     Validierung von Kurven- und Tuning-Änderungen
  PluginSettings.cs     FanControl.MCP.json
  VirtualSensor.cs      virtuelle Temperatursensoren
  Engine/EngineLocator.cs  findet die FanControlEngine im Prozess
  Engine/EngineBridge.cs   Lesen/Schreiben von Sensoren, Controls, Kurven, Profilen
  Mcp/HttpServer.cs     minimaler HTTP/1.1-Server (Streamable HTTP, POST /mcp)
  Mcp/McpProtocol.cs    JSON-RPC 2.0 / MCP-Lifecycle und Tool-Aufrufe
tests/SmokeTest/        End-to-End-Test des Endpunkts
install.ps1             Bauen + Installieren + FanControl neu starten
```

## Sicherheitshinweis

Wer den Endpunkt erreicht, kann Lüfter verlangsamen oder abschalten. Der Server bindet deshalb standardmäßig nur an
`127.0.0.1` und lehnt Browser-Anfragen fremder Origins ab. Für mehr Schutz ein `authToken` setzen,
`minimumManualPercent` anheben oder mit `allowWrite: false` nur Lesezugriff erlauben.
