# Bring the running game window to the foreground.
#
# Why: Godot suspends its main loop while the window is in the background, which
# makes MCP game_eval / game_manage fail with "main loop is not advancing".
# Run this before any runtime assertion via MCP.
#
# NOTE: ASCII-only on purpose -- see the note in shot.ps1.
#
# Usage:
#   powershell -File tools/dev/focus_game.ps1
#   powershell -File tools/dev/focus_game.ps1 -TitleLike "Tabletop"

param(
    [string]$TitleLike = "Tabletop Simulator"
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class TtWin32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
}
"@

$proc = Get-Process |
    Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like "*$TitleLike*" } |
    Select-Object -First 1

if (-not $proc) {
    Write-Error "No visible window matching '$TitleLike'. Is the game running?"
    exit 1
}

$h = $proc.MainWindowHandle
if ([TtWin32]::IsIconic($h)) { [TtWin32]::ShowWindow($h, 9) | Out-Null }   # 9 = SW_RESTORE
[TtWin32]::BringWindowToTop($h) | Out-Null
[TtWin32]::SetForegroundWindow($h) | Out-Null

Write-Host "[focus] foreground: [$($proc.Id)] $($proc.MainWindowTitle)"
