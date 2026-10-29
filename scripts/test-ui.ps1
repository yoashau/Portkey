$ErrorActionPreference = "Stop"
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
if ($env:PATHEXT -notmatch '(^|;)\.EXE(;|$)') { $env:PATHEXT = ".COM;.EXE;.BAT;.CMD;" + $env:PATHEXT }
$root = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $env:LOCALAPPDATA "PortkeyBuild\sdk\dotnet.exe"
$scratch = Join-Path $env:LOCALAPPDATA ("PortkeyBuild\ui-" + [Guid]::NewGuid().ToString("N"))
$report = Join-Path $root "artifacts\ui"
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
& (Join-Path $env:SystemRoot "System32\robocopy.exe") $root $scratch /E /XD .git .vs bin obj artifacts __pycache__ /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Source copy failed" }
Push-Location $scratch
try {
    & $sdk run --project tests/Portkey.UiTests -c Release -- $report
    if ($LASTEXITCODE -ne 0) { throw "UI component tests failed" }
} finally { Pop-Location }
Remove-Item -LiteralPath $scratch -Recurse -Force
