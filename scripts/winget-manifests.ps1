# Generates winget manifests for a release from the templates in packaging/winget:
# fills in the version, the SHA256 of the released zip, and the release date.
# The zip must be the exact asset the release will serve — NativeAOT builds are
# not reproducible across machines, so a locally rebuilt zip hashes differently.
# Usage: pwsh scripts/winget-manifests.ps1 -Version 0.1.0 -ZipPath publish/ytile-0.1.0-win-x64.zip [-OutDir publish/winget]
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$ZipPath,
    [string]$OutDir = 'publish/winget'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$templates = Join-Path $root 'packaging/winget'

$sha = (Get-FileHash $ZipPath -Algorithm SHA256).Hash.ToLower()
$date = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
foreach ($template in Get-ChildItem $templates -Filter *.yaml) {
    $content = Get-Content $template.FullName -Raw
    $content = $content.Replace('{{VERSION}}', $Version).Replace('{{SHA256}}', $sha).Replace('{{RELEASE_DATE}}', $date)
    # Drop the template banner; keep the schema hint line.
    $content = ($content -split "`n" | Where-Object { $_ -notmatch '^# Template' }) -join "`n"
    Set-Content -Path (Join-Path $OutDir $template.Name) -Value $content -Encoding utf8
    Write-Host "wrote $(Join-Path $OutDir $template.Name)"
}
