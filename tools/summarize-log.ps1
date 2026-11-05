<#
.SYNOPSIS
  Summarises a fan-logger CSV per phase (averages, maxima, throttle share). Defaults to the newest log.
.PARAMETER SkipSeconds
  Seconds ignored at the start of each phase so temperatures can settle.
#>
param([string]$Path, [int]$SkipSeconds = 45)

$inv = [Globalization.CultureInfo]::InvariantCulture
if (-not $Path) { $Path = (Get-ChildItem (Join-Path (Split-Path $PSScriptRoot -Parent) "logs") -Filter "fan-log-*.csv" | Sort-Object LastWriteTime | Select-Object -Last 1).FullName }
$rows = Import-Csv $Path | Where-Object { $_.cpu_temp -ne "" -and $_.phase -notlike "ERROR*" }

function N($v) { if ("$v" -eq "") { $null } else { [double]::Parse($v, $inv) } }
function Avg($list, $col) { $vals = $list | % { N $_.$col } | ? { $null -ne $_ }; if ($vals) { ($vals | Measure-Object -Average).Average } }
function Max($list, $col) { $vals = $list | % { N $_.$col } | ? { $null -ne $_ }; if ($vals) { ($vals | Measure-Object -Maximum).Maximum } }

$phases = [ordered]@{}
foreach ($r in $rows) { if (-not $phases.Contains($r.phase)) { $phases[$r.phase] = [System.Collections.Generic.List[object]]::new() }; $phases[$r.phase].Add($r) }

foreach ($name in $phases.Keys) {
    $all = $phases[$name]
    $start = [datetime]::ParseExact($all[0].time, "HH:mm:ss", $inv)
    $list = @($all | Where-Object { ([datetime]::ParseExact($_.time, "HH:mm:ss", $inv) - $start).TotalSeconds -ge $SkipSeconds })
    if ($list.Count -eq 0) { $list = @($all) }
    $throttled = @($list | ? { $_.gpu_throttle -notin @("none", "idle", "") }).Count
    [pscustomobject]@{
        Phase          = $name
        Samples        = $list.Count
        "CPU C avg/max" = "{0:0.0} / {1:0.0}" -f (Avg $list cpu_temp), (Max $list cpu_temp)
        "CPU MHz"      = "{0:0}" -f (Avg $list cpu_mhz)
        "CPU load %"   = "{0:0}" -f (Avg $list cpu_util)
        "GPU C avg/max" = "{0:0.0} / {1:0.0}" -f (Avg $list gpu_temp), (Max $list gpu_temp)
        "GPU MHz"      = "{0:0}" -f (Avg $list gpu_mhz)
        "GPU W"        = "{0:0}" -f (Avg $list gpu_w)
        "GPU load %"   = "{0:0}" -f (Avg $list gpu_util)
        "GPU limit"    = if ($throttled) { "{0}x {1}" -f $throttled, (($list | ? { $_.gpu_throttle -notin @("none", "idle", "") } | % gpu_throttle | Group-Object | Sort-Object Count -Descending | Select-Object -First 1).Name) } else { "-" }
        "CPU fan rpm"  = "{0:0}" -f (Avg $list cpu_fan_rpm)
        "Case in rpm"  = "{0:0}" -f (Avg $list in_front_rpm)
        "Case out rpm" = "{0:0}" -f (Avg $list ex_back_rpm)
        "GPU fan rpm"  = "{0:0}" -f (Avg $list gpu_fan_rpm)
    }
}
