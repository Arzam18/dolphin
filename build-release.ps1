# Builds the release folder: release\Dolphin2\ (exe + dll, network embedded in the dll and also copied beside it).
# Usage: powershell -ExecutionPolicy Bypass -File build-release.ps1 [-Net nets\netX.nnue]
# Builds into build\rel so a copy running from bin\ (e.g. in Arena) is never touched.
param([string]$Net = "")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
if ($Net -ne "") { Copy-Item $Net "dolphin.nnue" -Force }
if (-not (Test-Path "dolphin.nnue")) { throw "dolphin.nnue not found (pass -Net nets\netX.nnue)" }
(Get-Item "dolphin.nnue").LastWriteTime = Get-Date   # make MSBuild re-embed the network
$tmp = "build\rel"
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
dotnet build -c Release --nologo --no-incremental -o $tmp | Out-Host
if (-not (Test-Path "$tmp\Dolphin2.exe")) { throw "build failed" }

$out = "release\Dolphin2"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null
foreach ($f in "Dolphin2.exe", "Dolphin2.dll", "Dolphin2.deps.json", "Dolphin2.runtimeconfig.json") { Copy-Item "$tmp\$f" $out }
Copy-Item "dolphin.nnue" $out -Force
Copy-Item "HUONG_DAN.md" $out -Force -ErrorAction SilentlyContinue

# the network must also work when only the exe/dll are copied (embedded resource)
$probe = Join-Path $env:TEMP "dolphin2_probe"
if (Test-Path $probe) { Remove-Item $probe -Recurse -Force }
Copy-Item $out $probe -Recurse
Remove-Item "$probe\dolphin.nnue"
$id = "uci`nquit" | & "$probe\Dolphin2.exe" | Select-String "id name"
Write-Host "Embedded check: $id"
if ("$id" -notmatch "NNUE") { throw "network not embedded" }
"position startpos`ngo depth 12`nquit" | & "$out\Dolphin2.exe" | Select-String "bestmove"
Write-Host "Release ready in $out"
