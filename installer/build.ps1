# Baut den SizeShift-Installer: erst die einzelne SizeShift.exe, dann das Setup.
# Aufruf aus dem Projektordner:  powershell -ExecutionPolicy Bypass -File installer\build.ps1
# Ergebnis: artifacts\SizeShift-Setup-<Version>.exe

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\TreeSizeBetter.App\TreeSizeBetter.App.csproj'
$publish = Join-Path $root 'artifacts\publish'

# Version aus der Projektdatei, damit App und Setup dieselbe Nummer tragen.
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = '0.0.0' }

Write-Host "SizeShift $version veröffentlichen ..."
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $project -c Release -r win-x64 -o $publish | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish ist fehlgeschlagen.' }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 nicht gefunden. Installieren mit: winget install JRSoftware.InnoSetup' }

Write-Host "Setup bauen ..."
& $iscc "/DAppVersion=$version" "/DSourceExe=$publish\SizeShift.exe" (Join-Path $PSScriptRoot 'SizeShift.iss') | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup ist fehlgeschlagen.' }

Get-Item (Join-Path $root "artifacts\SizeShift-Setup-$version.exe")
