# Run Godot once for a self-check shot, with a hard timeout, and clean up ONLY
# the process this script started.
#
# ---------------------------------------------------------------------------
# WHY THIS FILE EXISTS (read before "simplifying" it away)
#
# Twice during M3 a hung self-check run was stopped with an ad-hoc command:
#
#     Get-Process | Where-Object { $_.ProcessName -like '*Godot*' } |
#         Stop-Process -Force
#
# Godot's editor and its game process are started from the SAME executable name
# (Godot_v4.7.2-stable_mono_win64_console.exe launches ..._mono_win64.exe, and
# the editor is launched exactly the same way). So that filter matched the
# user's running editor and killed it -- both times. The second kill also cost
# data: the editor had Main.tscn open with a stale in-memory copy and wrote it
# back over the file when it died.
#
# THE RULES, hard-coded here so they do not depend on anyone remembering them:
#   1. Never match processes by name. Ever.
#   2. Start with -PassThru, keep that PID, also write it to .dev/last_run.pid.
#   3. Stop only that PID, and re-check StartTime first so a recycled PID can
#      never be hit.
#   4. Never trust a pre-existing artifact. Delete the outputs up front, and
#      only report success when a FRESH report file appears. An earlier version
#      of this script printed "OK" while reading a stale PNG from a previous
#      run -- a lying tool is worse than no tool.
# ---------------------------------------------------------------------------
#
# Usage:
#   powershell -File tools/dev/godot_run.ps1 -Shot ".dev\shots\m3_zones.png" -Frames 50
#   powershell -File tools/dev/godot_run.ps1 -Shot "..." -Frames 40 -Zoom 1.0 -Center "900,700"
#
# Exit codes: 0 = ran and produced a fresh report.
#             1 = ran but no fresh report (look at the log tail it prints).
#             2 = could not find the Godot executable.
#             3 = produced a report but we had to stop our own leftover process.

param(
    [Parameter(Mandatory = $true)][string]$Shot,
    [int]$Frames = 40,
    [string]$Zoom = "",
    [string]$Center = "",
    [int]$TimeoutSec = 180,
    [string]$GodotExe = $env:TT_GODOT_EXE
)

$projectDir = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$devDir = Join-Path $projectDir ".dev"
New-Item -ItemType Directory -Force -Path $devDir | Out-Null

if (-not $GodotExe) {
    $onPath = Get-Command "Godot*mono*console*.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($onPath) { $GodotExe = $onPath.Source }
}

if (-not $GodotExe) {
    $searchDirs = @(
        (Join-Path $env:ProgramFiles "Godot"),
        (Join-Path ${env:ProgramFiles(x86)} "Godot"),
        (Join-Path $env:LOCALAPPDATA "Programs\Godot")
    )
    foreach ($drive in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        if ($drive.Root) {
            $searchDirs += (Join-Path $drive.Root "SteamLibrary\steamapps\common\Godot Engine")
        }
    }
    foreach ($dir in $searchDirs) {
        if (-not $dir -or -not (Test-Path $dir)) { continue }
        $found = Get-ChildItem $dir -Filter "*mono*console*.exe" -File -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($found) { $GodotExe = $found.FullName; break }
    }
}

if (-not $GodotExe -or -not (Test-Path $GodotExe)) {
    Write-Host "[run] ERROR: Godot .NET (mono) console executable not found."
    Write-Host "      Set it with -GodotExe or the TT_GODOT_EXE env var."
    exit 2
}

if ([System.IO.Path]::IsPathRooted($Shot)) {
    $shotPath = $Shot
} else {
    $shotPath = Join-Path $projectDir $Shot
}
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $shotPath) | Out-Null

$reportPath = "$shotPath.report.json"

# Delete every artifact up front. Success is then defined as "the report file
# exists", which cannot be satisfied by leftovers from a previous run.
Remove-Item $shotPath, "$shotPath.meta.json", $reportPath -ErrorAction SilentlyContinue

$outLog = Join-Path $devDir "run.out.log"
$errLog = Join-Path $devDir "run.err.log"
$pidFile = Join-Path $devDir "last_run.pid"
Remove-Item $outLog, $errLog, $pidFile -ErrorAction SilentlyContinue

$godotArgs = @(
    "--path", $projectDir,
    "--",
    "--shot", $shotPath,
    "--shot-frames", "$Frames",
    "--shot-exit"
)
if ($Zoom)   { $godotArgs += @("--zoom", $Zoom) }
if ($Center) { $godotArgs += @("--center", $Center) }

Write-Host "[run] godot   = $GodotExe"
Write-Host "[run] shot    = $shotPath"
Write-Host "[run] timeout = ${TimeoutSec}s"

# -PassThru gives us the ONLY process this script is allowed to touch.
# NOTE: do NOT name these $pid / $args -- both are read-only automatic
# variables in PowerShell and assigning to them fails at runtime.
$proc = Start-Process -FilePath $GodotExe -ArgumentList $godotArgs `
    -RedirectStandardOutput $outLog -RedirectStandardError $errLog -NoNewWindow -PassThru

$childPid = $proc.Id
$startedAt = $proc.StartTime

# Durable record, so the PID can still be recovered if this shell dies.
Set-Content -Path $pidFile -Value "$childPid $($startedAt.ToString('o'))" -Encoding ASCII
Write-Host "[run] pid     = $childPid (started $startedAt)"

# The report is the last thing written, so waiting for it is the correct
# completion signal. (The PNG is written a moment earlier -- waiting on the PNG
# alone can report success for a run that then failed while writing the report.)
$deadline = (Get-Date).AddSeconds($TimeoutSec)
while ((Get-Date) -lt $deadline -and -not (Test-Path $reportPath)) {
    Start-Sleep -Milliseconds 400
}

$hasReport = Test-Path $reportPath

# Give the game a moment to exit on its own (--shot-exit), then clean up our own
# leftover. A lingering console wrapper is normal and harmless.
if ($hasReport) {
    $graceDeadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $graceDeadline) {
        if (-not (Get-Process -Id $childPid -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 400
    }
}

$killed = $false
$live = Get-Process -Id $childPid -ErrorAction SilentlyContinue
if ($live) {
    if ($live.StartTime -eq $startedAt) {
        Write-Host "[run] stopping leftover pid $childPid ONLY" -ForegroundColor Yellow
        Stop-Process -Id $childPid -Force -ErrorAction SilentlyContinue
        $killed = $true
    } else {
        Write-Host "[run] REFUSING to stop pid ${childPid}: start time changed ($($live.StartTime) != $startedAt)" -ForegroundColor Red
    }
}

# Surface the lines that matter, same policy as shot.ps1.
foreach ($line in (Get-Content $outLog -ErrorAction SilentlyContinue)) {
    if ($line -match '\[DevCapture\]|\[DevObjectSim\]|\[DevZoneSim\]') { Write-Host "  $($line.Trim())" }
}
$benign = 'user://logs|Failed to read the root certificate store'
foreach ($line in (Get-Content $errLog -ErrorAction SilentlyContinue)) {
    if ($line -match 'ERROR|WARNING|SCRIPT ERROR|Parse Error|Unhandled exception|Invalid|Cannot|null instance') {
        if ($line -match $benign) { continue }
        Write-Host "  !! $($line.Trim())" -ForegroundColor Yellow
    }
}

if (-not $hasReport) {
    Write-Host "[run] FAILED - no fresh report after ${TimeoutSec}s." -ForegroundColor Red
    Write-Host "      Check that no other Godot instance is holding this project,"
    Write-Host "      and look at the log tail above."
    exit 1
}

$kb = [math]::Round((Get-Item $shotPath).Length / 1KB, 1)
Write-Host "[run] OK  $shotPath  ($kb KB)"
Write-Host "[run] report -> $reportPath"

if ($killed) { exit 3 }
exit 0
