# Builds native\win-x64\fathom.dll (Syzygy tablebase probing, Fathom + dolphin_tb.c) with the Microsoft C compiler.
# A prebuilt fathom.dll is in the repository, so this is only needed after changing the C sources.
# Needs Visual Studio 2022 or its Build Tools ("Desktop development with C++"). Usage: powershell -ExecutionPolicy Bypass -File native\build-fathom.ps1
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "Visual C++ build tools not found" }
$out = "win-x64"
New-Item -ItemType Directory -Force $out | Out-Null
$cmd = "`"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul && cl /nologo /O2 /I fathom /std:c11 /experimental:c11atomics /LD /Fe:$out\fathom.dll /Fo:$out\ dolphin_tb.c fathom\tbprobe.c"
cmd /c $cmd
if ($LASTEXITCODE -ne 0) { throw "build failed" }
Remove-Item "$out\*.obj", "$out\*.exp", "$out\*.lib" -ErrorAction SilentlyContinue
Write-Host "Built $out\fathom.dll"
