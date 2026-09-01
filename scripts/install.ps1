<#
.SYNOPSIS
    Installs YTile (Windows tiling window manager) for the current user.

.DESCRIPTION
    Downloads the latest release binaries into %LOCALAPPDATA%\Programs\ytile,
    puts that directory on the user PATH, and seeds a default config if none
    exists. No admin rights required and nothing is written outside the user
    profile.

    Run directly:
        irm https://raw.githubusercontent.com/AegiosOT/YTile/main/scripts/install.ps1 | iex

    Options are read from environment variables, since a piped script takes no
    parameters:
        $env:YTILE_AUTOSTART = 1    also register YTile to start at login
        $env:YTILE_START     = 1    start the daemon when the install finishes
        $env:YTILE_VERSION   = v0.1.0   install a specific tag (default: latest)
        $env:YTILE_HOTKEYS   = ykeys|whkd|none   hotkey daemon `ytile start` and
                                    autostart bring up (default: ykeys, bundled)
        $env:YTILE_UNINSTALL = 1    remove YTile instead of installing it
        $env:YTILE_ALLUSERS  = 1    install into %ProgramFiles%\ytile instead of
                                    the user profile. Needs an elevated terminal,
                                    and is REQUIRED for elevated autostart: a
                                    logon task at run level HIGHEST must point at
                                    a directory only administrators can write,
                                    or replacing the binary would hand anyone
                                    running as you an administrator token.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Repo       = 'AegiosOT/YTile'
# A per-user install lives somewhere the ordinary token owns outright, which is
# fine on its own but cannot anchor an elevated logon task (see YTILE_ALLUSERS).
$AllUsers   = [bool]$env:YTILE_ALLUSERS
$InstallDir = if ($AllUsers) { Join-Path $env:ProgramFiles 'ytile' }
              else { Join-Path $env:LOCALAPPDATA 'Programs\ytile' }
$MachineEnv = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment'
$ConfigDir  = Join-Path $env:USERPROFILE '.config\ytile'
$ConfigPath = Join-Path $ConfigDir 'ytile.json'
$StateDir   = Join-Path $env:LOCALAPPDATA 'ytile'
$RunKey     = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$Binaries   = @('ytiled.exe', 'ytile.exe')
# Bundled from its own repo (github.com/AegiosOT/YKeys); releases up to v0.1.1
# predate it, so it is verified when present rather than required.
$OptionalBinaries = @('ykeys.exe')
$YKeysConfigPath  = Join-Path $env:USERPROFILE '.config\ykeys\ykeys.json'

function Write-Step($msg) { Write-Host "  $msg" }
function Write-Head($msg) { Write-Host ''; Write-Host $msg -ForegroundColor Cyan }

function Stop-YTile {
    # @(): two daemons can coexist (another session, --debug-events), and member
    # enumeration over an array makes `-not $procs.HasExited` always false.
    $procs = @(Get-Process ytiled -ErrorAction SilentlyContinue)
    if (-not $procs) { return }
    Write-Step 'stopping the running daemon (its exe is locked while it runs)'
    # The daemon may have been installed by winget rather than this script, so
    # fall back to whatever `ytile` resolves to on PATH - the stop goes over a
    # named pipe and works regardless of which channel installed the CLI.
    $cli = Join-Path $InstallDir 'ytile.exe'
    if (-not (Test-Path $cli)) {
        $resolved = Get-Command ytile -ErrorAction SilentlyContinue
        $cli = if ($resolved) { $resolved.Source } else { $null }
    }
    if ($cli -and (Test-Path $cli)) {
        # Graceful: lets it restore cloaked windows and the taskbar. try/catch
        # because under EAP=Stop, Windows PowerShell 5.1 turns any stderr line
        # of a redirected native command into a terminating NativeCommandError.
        try { & $cli stop 2>$null | Out-Null } catch {}
        for ($i = 0; $i -lt 20 -and @($procs | Where-Object { -not $_.HasExited }).Count; $i++) {
            Start-Sleep -Milliseconds 150
            foreach ($p in $procs) { $p.Refresh() }
        }
    }
    $alive = @($procs | Where-Object { -not $_.HasExited })
    if ($alive) {
        # SilentlyContinue: the graceful stop may land between the poll and here.
        $alive | Stop-Process -Force -Confirm:$false -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 300
    }
}

function Stop-YKeys {
    # ykeys has no IPC; its exe is locked while running, which blocks both
    # upgrade overwrite and uninstall delete. Only the copy running from OUR
    # install dir: YKeys is also a standalone product, and someone else's
    # instance is not ours to kill. (Path is $null for processes we cannot
    # query, e.g. elevated ones - those are skipped, as Stop-Process would
    # fail on them anyway.)
    $bundled = Join-Path $InstallDir 'ykeys.exe'
    $procs = @(Get-Process ykeys -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $bundled })
    if (-not $procs) { return }
    Write-Step 'stopping ykeys (its exe is locked while it runs)'
    $procs | Stop-Process -Force -Confirm:$false -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300
}

# winget installs YTile too (AegiosOT.YTile); a copy from each channel on PATH is
# a recipe for running a stale binary and thinking it's upgraded.
function Test-WingetCopy {
    Test-Path (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\ytile.exe')
}

# The user PATH is usually REG_EXPAND_SZ; [Environment]::SetEnvironmentVariable
# reads it expanded and writes it back as REG_SZ, freezing entries other
# installers left as %JAVA_HOME%\bin etc. Go through the registry raw instead.
# An all-users install belongs on the machine PATH; a per-user one on the user
# PATH. Same raw-registry treatment either way.
function Open-EnvKey($writable) {
    if ($AllUsers) { return [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($MachineEnv, $writable) }
    return [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $writable)
}

function Get-UserPathRaw {
    $key = Open-EnvKey $false
    if (-not $key) { return '' }
    try { return [string]$key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
    finally { $key.Close() }
}

function Set-UserPathRaw($value) {
    $key = Open-EnvKey $true
    try {
        $kind = [Microsoft.Win32.RegistryValueKind]::ExpandString
        if ($key.GetValueNames() -contains 'Path') { $kind = $key.GetValueKind('Path') }
        $key.SetValue('Path', $value, $kind)
    } finally { $key.Close() }
    # A raw registry write skips the WM_SETTINGCHANGE broadcast that
    # [Environment]::SetEnvironmentVariable does; without it Explorer never
    # rereads the key and new terminals keep the old PATH until relogin.
    if (-not ('YTile.Native' -as [type])) {
        Add-Type -Namespace YTile -Name Native -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
'@
    }
    $result = [UIntPtr]::Zero
    # HWND_BROADCAST, WM_SETTINGCHANGE, SMTO_ABORTIFHUNG
    [void][YTile.Native]::SendMessageTimeout([IntPtr]0xffff, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 5000, [ref]$result)
}

function Add-ToUserPath($dir) {
    $current = Get-UserPathRaw
    $entries = @()
    if ($current) { $entries = @($current -split ';' | Where-Object { $_ }) }
    if ($entries -contains $dir) {
        Write-Step "PATH already contains $dir"
        return
    }
    Set-UserPathRaw ((@($entries) + $dir) -join ';')
    Write-Step "added $dir to your user PATH"
    Write-Host '    (open a new terminal for this to take effect elsewhere)' -ForegroundColor DarkGray
}

function Remove-FromUserPath($dir) {
    $entries = @((Get-UserPathRaw) -split ';' | Where-Object { $_ })
    if ($entries -notcontains $dir) { return }
    Set-UserPathRaw (($entries | Where-Object { $_ -ne $dir }) -join ';')
    Write-Step "removed $dir from your user PATH"
}

# ---------------------------------------------------------------- uninstall --

if ($env:YTILE_UNINSTALL) {
    Write-Head 'Uninstalling YTile'
    Stop-YTile
    Stop-YKeys

    if (Get-ItemProperty -Path $RunKey -Name YTile -ErrorAction SilentlyContinue) {
        Remove-ItemProperty -Path $RunKey -Name YTile
        Write-Step 'removed the autostart entry'
    }
    # An elevated install registers a logon task instead of the Run value.
    # Leaving it behind would point Task Scheduler at a deleted binary.
    #
    # $ErrorActionPreference is Stop for this script, and PowerShell turns a
    # native command's stderr into a terminating NativeCommandError under it.
    # `schtasks /query` writes to stderr whenever the task is simply absent -
    # the normal case - so querying it would abort the whole uninstall. Drop to
    # Continue for the two native calls and judge them by exit code instead.
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & schtasks /query /tn YTile *> $null
        if ($LASTEXITCODE -eq 0) {
            & schtasks /delete /tn YTile /f *> $null
            if ($LASTEXITCODE -eq 0) { Write-Step 'removed the elevated autostart task' }
            else { Write-Step 'could not remove the elevated autostart task - rerun from an admin terminal' }
        }
    } finally {
        $ErrorActionPreference = $prevEap
    }
    # Hand back any Win+ shell hotkeys ykeys suppressed - a persistent per-user
    # registry setting that nothing else would ever undo. Best-effort, and it
    # must run while ykeys.exe still exists, i.e. before InstallDir is deleted.
    $ykeys = Join-Path $InstallDir 'ykeys.exe'
    if (Test-Path $ykeys) {
        try {
            $out = & $ykeys shell-hotkeys restore 2>&1
            if ($out -notmatch 'nothing to restore') { Write-Step 'restored the Windows shell hotkeys (takes effect at next sign-in)' }
        } catch {}
    }
    Remove-FromUserPath $InstallDir
    $dirs = @($InstallDir, $StateDir)
    # %LOCALAPPDATA%\ykeys belongs to whichever ykeys is in use; leave it to a
    # still-running standalone instance (ours was stopped above).
    if (-not (Get-Process ykeys -ErrorAction SilentlyContinue)) {
        $dirs += Join-Path $env:LOCALAPPDATA 'ykeys'
    }
    foreach ($dir in $dirs) {
        if (Test-Path $dir) {
            # Non-fatal: a locked file must not abort the uninstall halfway,
            # after the Run key and PATH entry are already gone.
            try {
                Remove-Item $dir -Recurse -Force -Confirm:$false -ErrorAction Stop
                Write-Step "deleted $dir"
            } catch {
                Write-Host "  could not delete $dir - remove it by hand" -ForegroundColor Yellow
            }
        }
    }

    Write-Host ''
    Write-Host 'YTile removed.' -ForegroundColor Green
    if (Test-WingetCopy) {
        Write-Host 'A winget-installed copy of YTile is still present - remove it with: winget uninstall AegiosOT.YTile' -ForegroundColor Yellow
    }
    if (Test-Path $ConfigPath) {
        Write-Host "Your config was left alone at $ConfigPath - delete it by hand if you want it gone." -ForegroundColor DarkGray
    }
    if (Test-Path $YKeysConfigPath) {
        Write-Host "Your hotkey config was left alone at $YKeysConfigPath." -ForegroundColor DarkGray
    }
    # A lingering flag would turn the next install one-liner pasted into this
    # window into another uninstall.
    Remove-Item Env:YTILE_UNINSTALL -ErrorAction SilentlyContinue
    return
}

# ------------------------------------------------------------------ install --

Write-Head 'Installing YTile'

if ([Environment]::Is64BitOperatingSystem -eq $false) {
    throw 'YTile ships x64 binaries only.'
}

# Which hotkey daemon `ytile start` / autostart should bring up.
$hotkeys = if ($env:YTILE_HOTKEYS) { $env:YTILE_HOTKEYS.ToLower() } else {
    # A plain upgrade must not switch a whkd setup to ykeys, so read the choice
    # the user actually recorded: the autostart entry. Deliberately NOT a
    # running whkd process — that is evidence of the past, not of intent, and
    # someone trying ykeys with whkd still up would be silently kept on whkd.
    $entry  = Get-ItemProperty -Path $RunKey -Name YTile -ErrorAction SilentlyContinue
    $runCmd = if ($entry) { [string]$entry.YTile } else { '' }
    if ($runCmd -match '--whkd') { 'whkd' }
    elseif ($runCmd -match '--no-hotkeys') { 'none' }
    else { 'ykeys' }
}
if ($hotkeys -notin @('ykeys', 'whkd', 'none')) {
    throw "YTILE_HOTKEYS must be ykeys, whkd, or none (got '$env:YTILE_HOTKEYS')."
}
$hotkeyFlag = switch ($hotkeys) { 'whkd' { '--whkd' } 'none' { '--no-hotkeys' } default { $null } }

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

if ($AllUsers) {
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
             ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $admin) {
        throw "YTILE_ALLUSERS=1 installs into $InstallDir, which needs an elevated terminal. Re-run this from an administrator PowerShell."
    }
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
if ($AllUsers) {
    # Inherit %ProgramFiles%'s ACL rather than carrying over anything from a
    # previous per-user layout: the whole point is that non-administrators
    # cannot write here.
    $acl = Get-Acl $InstallDir
    $acl.SetAccessRuleProtection($false, $true)
    Set-Acl -Path $InstallDir -AclObject $acl
    Write-Step "install directory is administrator-only ($InstallDir)"
}
# An upgrade has to stop the daemon to overwrite its exe; remember to bring it
# back afterwards, or the one-liner upgrade leaves the user without tiling.
$wasRunning = [bool](Get-Process ytiled -ErrorAction SilentlyContinue)
Stop-YTile
Stop-YKeys

# Older releases have no ykeys.exe asset; verify it when present, skip otherwise.
$names = @($Binaries)
foreach ($name in $OptionalBinaries) {
    if ($release.assets | Where-Object { $_.name -eq $name }) {
        $names += $name
    } else {
        Write-Step "release $($release.tag_name) predates $name - skipping it"
    }
}

# Download to a temp dir first so a failed download cannot leave a half-installed
# directory behind.
$staging = Join-Path ([IO.Path]::GetTempPath()) ("ytile-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $staging | Out-Null
# Windows PowerShell 5.1 redraws the progress bar per buffer, slowing multi-MB
# downloads by an order of magnitude; iex shares the caller's scope, so save
# and restore instead of leaking the preference into their session.
$oldProgressPreference = $ProgressPreference
$ProgressPreference = 'SilentlyContinue'
try {
    foreach ($name in $names) {
        $asset = $release.assets | Where-Object { $_.name -eq $name } | Select-Object -First 1
        if (-not $asset) { throw "Release $($release.tag_name) has no asset named $name." }
        Write-Step "downloading $name"
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile (Join-Path $staging $name) -UseBasicParsing
    }

    # Every release ships SHA256SUMS.txt with a line per binary; a release
    # missing either is incomplete (e.g. caught mid-upload), not verifiable.
    $sums = $release.assets | Where-Object { $_.name -eq 'SHA256SUMS.txt' } | Select-Object -First 1
    if (-not $sums) {
        throw "Release $($release.tag_name) has no SHA256SUMS.txt - it may still be uploading; retry in a minute."
    }
    Write-Step 'verifying checksums'
    # GitHub serves release assets as application/octet-stream, so .Content is
    # a Byte[], not a string - splitting that on newlines yields "102 97 57..."
    # and every line fails the hash pattern, which used to fail the install as
    # "no entry for ytiled.exe". Decode explicitly, and accept CRLF.
    $sumsBody = (Invoke-WebRequest -Uri $sums.browser_download_url -UseBasicParsing).Content
    $sumsText = if ($sumsBody -is [byte[]]) { [Text.Encoding]::UTF8.GetString($sumsBody) } else { [string]$sumsBody }
    $expected = @{}
    foreach ($line in $sumsText -split "`r?`n") {
        if ($line -match '^\s*([0-9a-fA-F]{64})\s+\*?(\S+)\s*$') { $expected[$matches[2]] = $matches[1].ToLower() }
    }
    if ($expected.Count -eq 0) {
        throw "Could not parse SHA256SUMS.txt from release $($release.tag_name) - refusing to install unverified."
    }
    foreach ($name in $names) {
        if (-not $expected.ContainsKey($name)) {
            throw "SHA256SUMS.txt in release $($release.tag_name) has no entry for $name - refusing to install unverified."
        }
        $actual = (Get-FileHash (Join-Path $staging $name) -Algorithm SHA256).Hash.ToLower()
        if ($actual -ne $expected[$name]) {
            throw "Checksum mismatch for $name - refusing to install. Expected $($expected[$name]), got $actual."
        }
    }

    foreach ($name in $names) {
        Copy-Item (Join-Path $staging $name) (Join-Path $InstallDir $name) -Force
    }
    Write-Step "installed to $InstallDir"
} finally {
    $ProgressPreference = $oldProgressPreference
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue -Confirm:$false
}

Add-ToUserPath $InstallDir
# Make the tools usable in this session too, not just new terminals.
if (($env:PATH -split ';') -notcontains $InstallDir) { $env:PATH = "$InstallDir;$env:PATH" }

if (Test-WingetCopy) {
    Write-Host ''
    Write-Host 'Note: a winget-installed copy of YTile also exists. Whichever PATH entry was' -ForegroundColor Yellow
    Write-Host 'added first wins in new terminals, so `ytile` may keep running the winget copy.' -ForegroundColor Yellow
    Write-Host 'Pick one channel: winget uninstall AegiosOT.YTile   (or uninstall this copy instead)' -ForegroundColor Yellow
}

if (-not (Test-Path $ConfigPath)) {
    New-Item -ItemType Directory -Force -Path $ConfigDir | Out-Null
    @'
{
  "gap": 8,
  "focusBorderColor": "#FFFFFF",
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

# Always seed the hotkey config when it is missing, whatever flavor is active:
# a whkd user who later runs `ytile start` (or switches) would otherwise get a
# daemon with nothing bound and no hint that a config was ever expected.
if (-not (Test-Path $YKeysConfigPath)) {
    New-Item -ItemType Directory -Force -Path (Split-Path $YKeysConfigPath) | Out-Null
    @'
{
  "hotkeys": {
    "alt+h": "ytile focus left",
    "alt+j": "ytile focus down",
    "alt+k": "ytile focus up",
    "alt+l": "ytile focus right",

    "alt+shift+h": "ytile move left",
    "alt+shift+j": "ytile move down",
    "alt+shift+k": "ytile move up",
    "alt+shift+l": "ytile move right",

    "alt+1": "ytile workspace 1",
    "alt+2": "ytile workspace 2",
    "alt+3": "ytile workspace 3",
    "alt+4": "ytile workspace 4",
    "alt+shift+1": "ytile send 1",
    "alt+shift+2": "ytile send 2",
    "alt+shift+3": "ytile send 3",
    "alt+shift+4": "ytile send 4",

    "alt+plus": "ytile resize right",
    "alt+minus": "ytile resize right -50",
    "alt+shift+plus": "ytile resize down",
    "alt+shift+minus": "ytile resize down -50",

    "alt+t": "ytile float",
    "alt+b": "ytile layout bsp",
    "alt+c": "ytile layout columns",
    "alt+shift+x": "ytile retile",
    "alt+p": "ytile pause",
    "alt+shift+p": "ytile resume"
  }
}
'@ | Set-Content -Path $YKeysConfigPath -Encoding UTF8
    Write-Step "wrote starter hotkeys to $YKeysConfigPath"
} else {
    Write-Step "kept your existing hotkeys at $YKeysConfigPath"
}

if ($env:YTILE_AUTOSTART) {
    # Only an administrator-only install may anchor the HIGHEST-run-level logon
    # task; ytile itself refuses otherwise, so ask for it only when it can work.
    $elevatedFlag = if ($AllUsers) { '--elevated' } else { $null }
    $autostartArgs = @('autostart', 'on') + @($elevatedFlag | Where-Object { $_ }) +
                     @($hotkeyFlag | Where-Object { $_ })
    & (Join-Path $InstallDir 'ytile.exe') @autostartArgs
    # Native nonzero exits do not throw under EAP=Stop; an older pinned release
    # rejecting a hotkey flag must not masquerade as a successful install.
    if ($LASTEXITCODE) { throw "ytile $($autostartArgs -join ' ') failed (exit $LASTEXITCODE) - release $($release.tag_name) may predate the '$hotkeys' hotkey option." }
}

if ($env:YTILE_START -or $wasRunning) {
    $startArgs = @('start') + @($hotkeyFlag | Where-Object { $_ })
    & (Join-Path $InstallDir 'ytile.exe') @startArgs
    if ($LASTEXITCODE) { throw "ytile $($startArgs -join ' ') failed (exit $LASTEXITCODE) - release $($release.tag_name) may predate the '$hotkeys' hotkey option." }
}

Write-Host ''
Write-Host "YTile $($release.tag_name) installed." -ForegroundColor Green
Write-Host ''
Write-Host '  ytile start            launch the daemon in the background'
Write-Host '  ytile autostart on     start it at every login'
Write-Host '  ytile state            show monitors, workspaces and windows'
Write-Host '  ytile --help           all commands'
Write-Host ''
Write-Host 'Hotkeys: the bundled ykeys daemon starts with `ytile start`; bindings live in ~/.config/ykeys/ykeys.json.' -ForegroundColor DarkGray
Write-Host 'Prefer whkd (https://github.com/LGUG2Z/whkd)? Use: ytile start --whkd  (see examples/whkdrc-ytile)' -ForegroundColor DarkGray
Write-Host 'Uninstall: $env:YTILE_UNINSTALL=1; irm https://raw.githubusercontent.com/AegiosOT/YTile/main/scripts/install.ps1 | iex' -ForegroundColor DarkGray
