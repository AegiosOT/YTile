# Publishes NativeAOT ytiled.exe + ytile.exe into publish/ (git-ignored, on PATH).
# Usage: pwsh scripts/publish.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'publish'

# The NativeAOT linker resolves MSVC via vswhere.exe and fails with a garbled
# "vswhere.exe is not recognized" error unless the VS Installer dir is on PATH.
$vsInstaller = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if ((Test-Path $vsInstaller) -and ($env:PATH -notlike "*$vsInstaller*")) {
    $env:PATH = "$vsInstaller;$env:PATH"
}

dotnet publish (Join-Path $root 'src/YTile') -r win-x64 -c Release -o $out
if ($LASTEXITCODE) { exit $LASTEXITCODE }
dotnet publish (Join-Path $root 'src/YTile.Cli') -r win-x64 -c Release -o $out
if ($LASTEXITCODE) { exit $LASTEXITCODE }

Write-Host ''
& (Join-Path $out 'ytiled.exe') --version
& (Join-Path $out 'ytile.exe') --version
Write-Host "published to $out"
