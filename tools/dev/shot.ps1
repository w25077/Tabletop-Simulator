# Capture one verification screenshot by launching the game, waiting N frames,
# saving a PNG plus a self-check report, then quitting.
# Needs no window focus, so it is repeatable.
#
# NOTE: This file is intentionally ASCII-only. Windows PowerShell 5.1 decodes
# .ps1 files as ANSI unless they carry a UTF-8 BOM, and hand-written scripts
# here have no BOM -- non-ASCII would be mojibake and break parsing.
#
# Usage:
#   powershell -File tools/dev/shot.ps1 -Name m1_board
#   powershell -File tools/dev/shot.ps1 -Name m2_cards -Frames 60
#
# Output in .dev/shots/:
#   <Name>.png              the frame
#   <Name>.png.meta.json    resolution / sampled colors / mean luminance
#   <Name>.png.report.json  DevReport self-check (font CJK coverage, pixel
#                           regions, UI rects, camera anchor invariant)

param(
    [Parameter(Mandatory = $true)][string]$Name,
    [int]$Frames = 40,
    [string]$Zoom = "",
    [string]$Center = "",
    [string]$GodotExe = $env:TT_GODOT_EXE
)

$projectDir = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$shotDir = Join-Path $projectDir ".dev\shots"
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null

$shotPath = Join-Path $shotDir "$Name.png"
foreach ($suffix in @("", ".meta.json", ".report.json")) {
    Remove-Item "$shotPath$suffix" -ErrorAction SilentlyContinue
}

# Resolve the Godot .NET (mono) console executable:
#   1. -GodotExe parameter   2. TT_GODOT_EXE env var   3. auto-detect
# The standard (non-mono) build cannot run C#, so we specifically want mono.
#
# Auto-detection deliberately has NO hard-coded machine paths: it looks on PATH
# first, then at the usual install roots, then at the Steam library folder on
# *every* filesystem drive. Point TT_GODOT_EXE at your install if it lives
# somewhere unusual.
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
    Write-Host "[shot] ERROR: Godot .NET (mono) console executable not found."
    Write-Host "       Set it explicitly:  -GodotExe ""C:\path\to\Godot_v4.x-stable_mono_win64_console.exe"""
    Write-Host "       Or via env var:     `$env:TT_GODOT_EXE = ""..."""
    exit 2
}

Write-Host "[shot] godot   = $GodotExe"
Write-Host "[shot] project = $projectDir"
Write-Host "[shot] rendering -> $shotPath"

# Godot writes benign noise to stderr in this environment (it cannot create
# user://logs/ because the harness sandbox blocks writes outside the workspace).
# Redirect it into a buffer instead of letting PowerShell turn it into a
# terminating error, which would abort the script mid-run.
# Optional view overrides: -Zoom 1 renders at 100% so card face detail is legible
# (the default "fit the whole table" view is ~52%, where card text is only a few px).
$extraArgs = @()
if ($Zoom)   { $extraArgs += @("--zoom", $Zoom) }
if ($Center) { $extraArgs += @("--center", $Center) }

# Redirect the app's save root into the workspace.
#
# user:// lives under %APPDATA%\Godot\app_userdata, which the harness sandbox
# refuses to write. Without this flag the save/load self-check could not run at
# all, and "it worked when I clicked it" would be the only evidence for M4's
# save feature. With it, the whole save -> mutate -> load -> compare loop is
# re-run on every self-check, and the user's real saves are never touched.
#
# The directory is wiped each run so the report never sees yesterday's files.
$saveRoot = Join-Path $projectDir ".dev\userdata"
if (Test-Path $saveRoot) { Remove-Item $saveRoot -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $saveRoot | Out-Null
$extraArgs += @("--save-root", $saveRoot.Replace('\', '/'))

# --fresh: skip "auto-load the last save" and start from the demo content.
# Every assertion in the report is written against that known starting table
# (deck of 20, fixed loose props, eight zone invariants). If a previous run's
# save were auto-loaded, the whole report would lose its baseline -- and the
# symptom would look like a dozen unrelated assertions failing at once.
$extraArgs += @("--fresh")

$prevEap = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$raw = & $GodotExe --path $projectDir -- --shot $shotPath --shot-frames $Frames @extraArgs --shot-exit 2>&1 | Out-String
$code = $LASTEXITCODE
$ErrorActionPreference = $prevEap

# Surface only the lines that matter.
# 关键：除了脚本异常，也要把 Godot 自己的 ERROR / WARNING 打出来 ——
# 漏掉它们会让人以为"跑起来没报错"，其实界面上正刷着错。
$benign = 'user://logs|Failed to read the root certificate store'
foreach ($line in ($raw -split "`r?`n")) {
    if ($line -match '\[DevCapture\]|\[DevObjectSim\]') {
        Write-Host "  $($line.Trim())"
        continue
    }

    if ($line -match 'ERROR|WARNING|SCRIPT ERROR|Parse Error|Unhandled exception|Invalid|Cannot|null instance') {
        if ($line -match $benign) { continue }
        Write-Host "  !! $($line.Trim())" -ForegroundColor Yellow
    }
}

if (-not (Test-Path $shotPath)) {
    Write-Host "[shot] FAILED - no PNG produced. Godot exit code: $code"
    Write-Host $raw
    exit 1
}

$kb = [math]::Round((Get-Item $shotPath).Length / 1KB, 1)
Write-Host "[shot] OK  $shotPath  ($kb KB)"

if (Test-Path "$shotPath.meta.json") {
    Write-Host "----- $Name.png.meta.json -----"
    Get-Content "$shotPath.meta.json" -Raw
}

# The report is large; print its location and let the caller read it.
if (Test-Path "$shotPath.report.json") {
    Write-Host "[shot] report -> $shotPath.report.json"
}
