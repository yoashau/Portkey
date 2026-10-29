param([string]$Dotnet = "", [switch]$SkipTests)
$ErrorActionPreference = "Stop"
$OutputEncoding = [Console]::OutputEncoding = [Text.UTF8Encoding]::new()
# WSL may inherit a PATHEXT without .EXE. Change only this build process.
if ($env:PATHEXT -notmatch '(^|;)\.EXE(;|$)') { $env:PATHEXT = ".COM;.EXE;.BAT;.CMD;" + $env:PATHEXT }
if (-not $Dotnet) {
    $privateSdk = Join-Path $env:LOCALAPPDATA "PortkeyBuild\sdk\dotnet.exe"
    if (Test-Path -LiteralPath $privateSdk) { $Dotnet = $privateSdk }
    else {
        $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue
        if ($command) { $Dotnet = $command.Source }
    }
    if (-not (Test-Path -LiteralPath $Dotnet)) { throw "Install .NET 10 SDK or supply -Dotnet with its full path" }
}
$root = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path $env:LOCALAPPDATA ("PortkeyBuild\build-" + [Guid]::NewGuid().ToString("N"))
$output = Join-Path $root "artifacts\win-x64"
# A fresh local working copy avoids UNC working-directory and WSL obj conflicts.
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
& (Join-Path $env:SystemRoot "System32\robocopy.exe") $root $scratch /E /XD .git .vs bin obj artifacts __pycache__ /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Source copy failed: $LASTEXITCODE" }
Push-Location $scratch
try {
    & $Dotnet --version
    if ($LASTEXITCODE -ne 0) { throw "A .NET 10 SDK is required" }
    if (-not $SkipTests) {
        & $Dotnet run --project tests/Portkey.Tests -c Release -m:1
        if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
    }
    & $Dotnet publish src/Portkey.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -m:1 -o publish
    if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Copy-Item (Join-Path $scratch "publish\Portkey.exe") $output -Force
    Get-Item (Join-Path $output "Portkey.exe") | Select-Object FullName,Length
    Write-Output ((Get-FileHash (Join-Path $output "Portkey.exe") -Algorithm SHA256).Hash)
} finally { Pop-Location }
# Delete only this invocation's generated scratch copy, after successful publish.
Remove-Item -LiteralPath $scratch -Recurse -Force
