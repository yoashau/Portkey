param([string]$Exe = (Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\win-x64\Portkey.exe"))
$ErrorActionPreference = "Stop"
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
$scratch = Join-Path $env:LOCALAPPDATA ("PortkeyBuild\smoke-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $scratch | Out-Null
$localExe = Join-Path $scratch "Portkey.exe"
Copy-Item -LiteralPath $Exe -Destination $localExe
$first = $null
try {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $localExe
    $start.WorkingDirectory = $scratch
    $start.UseShellExecute = $false
    $first = [Diagnostics.Process]::Start($start)
    Start-Sleep -Seconds 3
    if ($first.HasExited) { throw "Portkey exited at startup: $($first.ExitCode)" }
    Write-Output "PASS native Windows process remains running after tray-only startup"
    $log = Join-Path $env:LOCALAPPDATA "Portkey\Logs\portkey.log"
    $text = Get-Content -LiteralPath $log -Raw
    if ($text -notmatch "pid=$($first.Id); panelVisible=False") { throw "Tray-only startup was not confirmed by WPF state" }
    Write-Output "PASS WPF panel is hidden on startup (native application state log)"
    $start.Arguments = "--show"
    $second = [Diagnostics.Process]::Start($start)
    if (-not $second.WaitForExit(10000) -or $second.ExitCode -ne 0) { throw "Second instance did not exit cleanly" }
    Write-Output "PASS second instance exits cleanly"
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $first.Refresh()
        $text = Get-Content -LiteralPath $log -Raw
        $shown = $text -match "pid=$($first.Id); visible=True, active=True"
    } while (-not $first.HasExited -and -not $shown -and [DateTime]::UtcNow -lt $deadline)
    if ($first.HasExited -or -not $shown) { throw "First instance did not show the WPF panel" }
    Write-Output "PASS activation request opens and activates the existing WPF panel (native application state log)"
    Write-Output "This verifies process/window lifecycle only. Tray clicks, placement, themes and DPI still require interactive acceptance."
} finally {
    # Only the process created above; no ssh process enumeration or termination.
    if ($first -and -not $first.HasExited) { $first.Kill(); $first.WaitForExit(5000) | Out-Null }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
