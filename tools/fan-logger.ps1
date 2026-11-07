<#
.SYNOPSIS
  Logs temperatures, fan speeds (via the FanControl MCP plugin) and CPU/GPU load, clocks and throttling to CSV.

.DESCRIPTION
  Every -IntervalSeconds one row is appended to logs\fan-log-<timestamp>.csv. The current content of logs\phase.txt
  is written into the "phase" column, so experiments can be labelled while the logger runs.
  CPU load comes from Windows performance counters (language independent WMI classes), GPU data from nvidia-smi.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\fan-logger.ps1 -Minutes 30
#>
param(
    [int]$Minutes = 30,
    [int]$IntervalSeconds = 5,
    [string]$Endpoint = "http://127.0.0.1:48620/mcp",
    [double]$CpuBaseMHz = 0
)

$ErrorActionPreference = "Continue"
$root = Split-Path $PSScriptRoot -Parent
$logDir = Join-Path $root "logs"
New-Item -ItemType Directory -Force $logDir | Out-Null
$phaseFile = Join-Path $logDir "phase.txt"
if (-not (Test-Path $phaseFile)) { Set-Content $phaseFile "baseline" -Encoding UTF8 }
$csv = Join-Path $logDir ("fan-log-{0:yyyyMMdd-HHmmss}.csv" -f (Get-Date))
if ($CpuBaseMHz -le 0) { $CpuBaseMHz = (Get-CimInstance Win32_Processor | Select-Object -First 1).MaxClockSpeed }
$smi = "$env:SystemRoot\System32\nvidia-smi.exe"
$inv = [Globalization.CultureInfo]::InvariantCulture

function Mcp([string]$name) {
    $body = @{ jsonrpc = "2.0"; id = 1; method = "tools/call"; params = @{ name = $name; arguments = @{} } } | ConvertTo-Json -Depth 6 -Compress
    (Invoke-RestMethod -Uri $Endpoint -Method Post -Body ([Text.Encoding]::UTF8.GetBytes($body)) -ContentType "application/json; charset=utf-8" -TimeoutSec 5).result.structuredContent
}
function F($v, [string]$fmt = "0.#") { if ($null -eq $v -or "$v" -eq "") { "" } else { ([double]$v).ToString($fmt, $inv) } }

$header = "time,phase,cpu_temp,cpu_avg,gpu_temp,case_src,cpu_util,cpu_mhz,max_core_util,gpu_util,gpu_w,gpu_mhz,gpu_throttle,cpu_fan_pct,cpu_fan_rpm,in_front_pct,in_front_rpm,in_top_pct,ex_top_pct,ex_back_pct,ex_back_rpm,gpu_fan_pct,gpu_fan_rpm"
Set-Content $csv $header -Encoding UTF8
Write-Output "Logging to $csv"

$end = (Get-Date).AddMinutes($Minutes)
while ((Get-Date) -lt $end) {
    $tick = Get-Date
    try {
        $phase = (Get-Content $phaseFile -Raw -ErrorAction SilentlyContinue).Trim() -replace ',', ';'
        $s = Mcp "get_snapshot"; $t = $s.temperatures; $c = $s.controls; $f = $s.fans
        $cpuInfo = Get-CimInstance Win32_PerfFormattedData_Counters_ProcessorInformation
        $tot = $cpuInfo | Where-Object Name -eq '_Total'
        $maxCore = ($cpuInfo | Where-Object Name -match '^\d+,\d+$' | Measure-Object PercentProcessorUtility -Maximum).Maximum
        $g = (& $smi --query-gpu=utilization.gpu,power.draw,clocks.gr,clocks_throttle_reasons.active --format=csv,noheader,nounits 2>$null) -split ',\s*'
        $throttle = switch ($g[3]) { "0x0000000000000000" { "none" } "0x0000000000000001" { "idle" } "0x0000000000000004" { "power" } default { $g[3] } }

        $row = @(
            $tick.ToString("HH:mm:ss"), $phase,
            (F ($t | ? id -eq '/amdcpu/0/temperature/2').value), (F ($t | ? id -eq 'Time/CPU Avg.').value),
            (F ($t | ? id -eq 'NVApiWrapper/0-TU106-A/sensor/0').value), (F ($t | ? name -eq 'Case Temp').value),
            (F $tot.PercentProcessorUtility "0"), (F ($CpuBaseMHz * $tot.PercentProcessorPerformance / 100) "0"), (F $maxCore "0"),
            $g[0], $g[1], $g[2], $throttle,
            (F ($c | ? name -eq 'CPU').currentPercent), (F ($f | ? id -eq '/lpc/nct6799d/fan/1').value "0"),
            (F ($c | ? name -eq 'Intake Front').currentPercent), (F ($f | ? id -eq '/lpc/nct6799d/fan/2').value "0"),
            (F ($c | ? name -eq 'Intake Top').currentPercent), (F ($c | ? name -eq 'Exhaust Top').currentPercent),
            (F ($c | ? name -eq 'Exhaust Back').currentPercent), (F ($f | ? id -eq '/lpc/nct6799d/fan/0').value "0"),
            (F ($c | ? name -like '*RTX*').currentPercent), (F ($f | ? id -like 'NVApi*').value "0")
        ) -join ','
        Add-Content $csv $row -Encoding UTF8
    } catch {
        Add-Content $csv ("{0},ERROR {1}" -f $tick.ToString("HH:mm:ss"), ($_.Exception.Message -replace ',', ';')) -Encoding UTF8
    }
    $sleep = $IntervalSeconds - ((Get-Date) - $tick).TotalSeconds
    if ($sleep -gt 0) { Start-Sleep -Milliseconds ([int]($sleep * 1000)) }
}
Write-Output "Done: $csv"
