#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [[ ${1:-} == --windows ]]; then
  shift
  exec /mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$(wslpath -w "$PWD/scripts/build.ps1")" "$@"
fi
sdk=${DOTNET:-dotnet}
"$sdk" run --project tests/Portkey.Tests -c Release -m:1
python3 -m unittest discover -s tests/linux -v
"$sdk" publish src/Portkey.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -m:1 -o artifacts/win-x64
test -s artifacts/win-x64/Portkey.exe
sha256sum artifacts/win-x64/Portkey.exe
