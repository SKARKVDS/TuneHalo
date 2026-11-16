# Builds a self-contained exe (no .NET runtime needed on the target PC) and packages it with Inno Setup.
# Usage:  powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 [-Version 1.0.1]
param([string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

if (-not (Test-Path "$root\app.ico")) { & "$PSScriptRoot\make-icon.ps1" }

dotnet publish "$root\TuneHalo.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:Version=$Version -o "$root\publish"
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$iscc = @(
    $env:ISCC,                                  # explicit override (set by the CI workflow)
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 introuvable : winget install JRSoftware.InnoSetup' }

& $iscc "/DAppVersion=$Version" "$PSScriptRoot\TuneHalo.iss"
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }
Write-Host "Installeur : $PSScriptRoot\Output\TuneHalo-Setup-$Version.exe"
