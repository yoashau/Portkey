$ErrorActionPreference = "Stop"
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
if ($env:PATHEXT -notmatch '(^|;)\.EXE(;|$)') { $env:PATHEXT = ".COM;.EXE;.BAT;.CMD;" + $env:PATHEXT }
$root = Split-Path -Parent $PSScriptRoot
$sdk = Join-Path $env:LOCALAPPDATA "PortkeyBuild\sdk\dotnet.exe"
if (-not (Test-Path $sdk)) { $sdk = (Get-Command dotnet.exe -ErrorAction Stop).Source }
$scratch = Join-Path $env:LOCALAPPDATA ("PortkeyBuild\icons-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
& (Join-Path $env:SystemRoot "System32\robocopy.exe") $root $scratch /E /XD .git .vs bin obj artifacts __pycache__ /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Source copy failed" }
Push-Location $scratch
try {
    & $sdk run --project tools/Portkey.IconGenerator -c Release -- (Join-Path $root "assets")
    if ($LASTEXITCODE -ne 0) { throw "Icon generation failed" }
} finally { Pop-Location }
Remove-Item -LiteralPath $scratch -Recurse -Force
