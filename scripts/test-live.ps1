param(
    [Parameter(Mandatory = $true)][string]$HostAlias,
    [int]$RemotePort = 27890,
    [switch]$Deploy)
$ErrorActionPreference = "Stop"
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
if ($env:PATHEXT -notmatch '(^|;)\.EXE(;|$)') { $env:PATHEXT = ".COM;.EXE;.BAT;.CMD;" + $env:PATHEXT }
$root = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $env:LOCALAPPDATA "PortkeyBuild\sdk\dotnet.exe"
$scratch = Join-Path $env:LOCALAPPDATA ("PortkeyBuild\live-" + [Guid]::NewGuid().ToString("N"))
$report = Join-Path $root "artifacts\live"
if ($Deploy) { $report = Join-Path $root "artifacts\deployment" }
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
& (Join-Path $env:SystemRoot "System32\robocopy.exe") $root $scratch /E /XD .git .vs bin obj artifacts __pycache__ /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Source copy failed" }
Push-Location $scratch
try {
    & $sdk build tests/Portkey.Tests -c Release -m:1
    if ($LASTEXITCODE -ne 0) { throw "Live-test build failed" }
    $mode = "--live-readonly"
    if ($Deploy) { $mode = "--live-deploy" }
    & $sdk (Join-Path $scratch "tests\Portkey.Tests\bin\Release\net10.0\Portkey.Tests.dll") $mode $HostAlias $RemotePort $report
    $result = $LASTEXITCODE
} finally { Pop-Location }
Remove-Item -LiteralPath $scratch -Recurse -Force
exit $result
