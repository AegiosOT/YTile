<#
.SYNOPSIS
    Installs YTile (Windows tiling window manager) for the current user.

.DESCRIPTION
    Downloads the latest release binaries into %LOCALAPPDATA%\Programs\ytile,
    puts that directory on the user PATH, and seeds a default config if none
    exists. No admin rights required and nothing is written outside the user
    profile.

    Run directly:
        irm https://raw.githubusercontent.com/AltimG/YTile/main/scripts/install.ps1 | iex

    Options are read from environment variables, since a piped script takes no
    parameters:
        $env:YTILE_AUTOSTART = 1    also register YTile to start at login
        $env:YTILE_START     = 1    start the daemon when the install finishes
        $env:YTILE_VERSION   = v0.1.0   install a specific tag (default: latest)
        $env:YTILE_UNINSTALL = 1    remove YTile instead of installing it
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Repo       = 'AltimG/YTile'
$InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\ytile'
$ConfigDir  = Join-Path $env:USERPROFILE '.config\ytile'
$ConfigPath = Join-Path $ConfigDir 'ytile.json'
$StateDir   = Join-Path $env:LOCALAPPDATA 'ytile'
$RunKey     = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$Binaries   = @('ytiled.exe', 'ytile.exe')

function Write-Step($msg) { Write-Host "  $msg" }
function Write-Head($msg) { Write-Host ''; Write-Host $msg -ForegroundColor Cyan }

function Stop-YTile {
    $proc = Get-Process ytiled -ErrorAction SilentlyContinue
    if (-not $proc) { return }
    Write-Step 'stopping the running daemon (its exe is locked while it runs)'
    $cli = Join-Path $InstallDir 'ytile.exe'
    if (Test-Path $cli) {
        # Graceful: lets it restore cloaked windows and the taskbar.
        & $cli stop 2>$null | Out-Null
        for ($i = 0; $i -lt 20 -and -not $proc.HasExited; $i++) { Start-Sleep -Milliseconds 150; $proc.Refresh() }
    }
    if (-not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -Confirm:$false
        Start-Sleep -Milliseconds 300
    }
}

function Add-ToUserPath($dir) {
    $current = [Environment]::GetEnvironmentVariable('PATH', 'User')
    $entries = @()
    if ($current) { $entries = $current -split ';' | Where-Object { $_ } }
    if ($entries -contains $dir) {
        Write-Step "PATH already contains $dir"
        return
    }
    [Environment]::SetEnvironmentVariable('PATH', (@($entries) + $dir) -join ';', 'User')
    Write-Step "added $dir to your user PATH"
    Write-Host '    (open a new terminal for this to take effect elsewhere)' -ForegroundColor DarkGray
}

function Remove-FromUserPath($dir) {
    $current = [Environment]::GetEnvironmentVariable('PATH', 'User')
    if (-not $current) { return }
    $kept = $current -split ';' | Where-Object { $_ -and $_ -ne $dir }
    [Environment]::SetEnvironmentVariable('PATH', ($kept -join ';'), 'User')
    Write-Step "removed $dir from your user PATH"
}

# ---------------------------------------------------------------- uninstall --

if ($env:YTILE_UNINSTALL) {
    Write-Head 'Uninstalling YTile'
    Stop-YTile

    if (Get-ItemProperty -Path $RunKey -Name YTile -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $RunKey -Name YTile
        Write-Step 'removed the autostart entry'
    }
    Remove-FromUserPath $InstallDir
    foreach ($dir in @($InstallDir, $StateDir)) {
        if (Test-Path $dir) {
            Remove-Item $dir -Recurse -Force -Confirm:$false
            Write-Step "deleted $dir"
        }
    }

    Write-Host ''
    Write-Host 'YTile removed.' -ForegroundColor Green
    if (Test-Path $ConfigPath) {
        Write-Host "Your config was left alone at $ConfigPath — delete it by hand if you want it gone." -ForegroundColor DarkGray
    }
    return
}

# ------------------------------------------------------------------ install --

Write-Head 'Installing YTile'

if ([Environment]::Is64BitOperatingSystem -eq $false) {
    throw 'YTile ships x64 binaries only.'
}

# Resolve the release to install.
$tag = $env:YTILE_VERSION
$apiUrl = if ($tag) {
    "https://api.github.com/repos/$Repo/releases/tags/$tag"
} else {
    "https://api.github.com/repos/$Repo/releases/latest"
}

Write-Step 'looking up the release'
try {
    $release = Invoke-RestMethod -Uri $apiUrl -Headers @{ 'User-Agent' = 'ytile-installer' }
} catch {
    throw "Could not reach the GitHub release API ($apiUrl): $($_.Exception.Message)"
}
Write-Step "release $($release.tag_name)"

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Stop-YTile

# Download to a temp dir first so a failed download cannot leave a half-installed
# directory behind.
$staging = Join-Path ([IO.Path]::GetTempPath()) ("ytile-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $staging | Out-Null
try {
    foreach ($name in $Binaries) {
        $asset = $release.assets | Where-Object { $_.name -eq $name } | Select-Object -First 1
        if (-not $asset) { throw "Release $($release.tag_name) has no asset named $name." }
        Write-Step "downloading $name"
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile (Join-Path $staging $name) -UseBasicParsing
    }

    # Verify against the release checksums when they are published.
    $sums = $release.assets | Where-Object { $_.name -eq 'SHA256SUMS.txt' } | Select-Object -First 1
    if ($sums) {
        Write-Step 'verifying checksums'
        $expected = @{}
        foreach ($line in (Invoke-WebRequest -Uri $sums.browser_download_url -UseBasicParsing).Content -split "`n") {
            if ($line -match '^\s*([0-9a-fA-F]{64})\s+\*?(\S+)\s*$') { $expected[$matches[2]] = $matches[1].ToLower() }
        }
        foreach ($name in $Binaries) {
            if (-not $expected.ContainsKey($name)) { continue }
            $actual = (Get-FileHash (Join-Path $staging $name) -Algorithm SHA256).Hash.ToLower()
            if ($actual -ne $expected[$name]) {
                throw "Checksum mismatch for $name — refusing to install. Expected $($expected[$name]), got $actual."
            }
        }
    } else {
        Write-Host '    (release publishes no SHA256SUMS.txt — skipping verification)' -ForegroundColor DarkGray
    }

    foreach ($name in $Binaries) {
        Copy-Item (Join-Path $staging $name) (Join-Path $InstallDir $name) -Force
    }
    Write-Step "installed to $InstallDir"
} finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue -Confirm:$false
}

Add-ToUserPath $InstallDir
# Make the tools usable in this session too, not just new terminals.
if (($env:PATH -split ';') -notcontains $InstallDir) { $env:PATH = "$InstallDir;$env:PATH" }

if (-not (Test-Path $ConfigPath)) {
    New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null
    @'
{
  "gap": 8,
  "focusBorderColor": "#569CD6",
  "defaultLayout": "bsp",
  "resizeStep": 50,
  "hideTaskbar": false,
  "rules": [
    { "match": "exe", "pattern": "Battle.net.exe", "action": "float" },
    { "match": "title", "pattern": "Picture.in.[Pp]icture", "strategy": "regex", "action": "float" }
  ]
}
'@ | Set-Content -Path $ConfigPath -Encoding UTF8
    Write-Step "wrote a starter config to $ConfigPath"
} else {
    Write-Step "kept your existing config at $ConfigPath"
}

if ($env:YTILE_AUTOSTART) {
    & (Join-Path $InstallDir 'ytile.exe') autostart on
}

if ($env:YTILE_START) {
    & (Join-Path $InstallDir 'ytile.exe') start
}

Write-Host ''
Write-Host "YTile $($release.tag_name) installed." -ForegroundColor Green
Write-Host ''
Write-Host '  ytile start            launch the daemon in the background'
Write-Host '  ytile autostart on     start it at every login'
Write-Host '  ytile state            show monitors, workspaces and windows'
Write-Host '  ytile --help           all commands'
Write-Host ''
Write-Host 'Hotkeys need whkd (https://github.com/LGUG2Z/whkd); see examples/whkdrc-ytile.' -ForegroundColor DarkGray
Write-Host 'Uninstall: $env:YTILE_UNINSTALL=1; irm https://raw.githubusercontent.com/AltimG/YTile/main/scripts/install.ps1 | iex' -ForegroundColor DarkGray
