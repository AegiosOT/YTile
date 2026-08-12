# YTile

A tiling window manager for Windows. C#, .NET 10, NativeAOT. Tiling only — no bar, no widgets.

YTile is an independent, from-scratch implementation informed by a behavioral study of
[komorebi](https://github.com/LGUG2Z/komorebi). No komorebi source code is used or reproduced.
See [docs/komorebi-architecture-digest.md](docs/komorebi-architecture-digest.md) for the study and
[docs/language-decision.md](docs/language-decision.md) for why C#/NativeAOT and the v0.1 roadmap.

## Design pillars

- **Single-actor state.** One thread owns all window-manager state and consumes one message
  queue fed by OS events, IPC commands, and timer ticks. No shared mutexes.
- **Narrow event capture.** WinEvent hooks are registered only for the ranges the manager
  consumes; the hook callback never blocks (bounded drop-oldest channel + resync on overflow).
- **DWM-native chrome.** Focus indication via `DWMWA_BORDER_COLOR` — no overlay border windows.
- **AOT-first.** Developed on the JIT, shipped NativeAOT. `DisableRuntimeMarshalling` and
  CsWin32 with `allowMarshaling: false` from the first commit; both binaries publish AOT.
- **Quirks as data.** Per-application Windows quirks live in a declarative table consulted at
  defined pipeline points, not as special cases threaded through event handling.

## Status

Early development.

- [x] Event spine: narrow WinEvent hooks, message pump, `--debug-events` dump mode
- [x] Eligibility + window tracking (adoption pass, single-actor state)
- [x] Layouts: BSP (dwindle), Columns
- [x] Focus + directional movement (`ytile focus/move left|right|up|down`)
- [x] IPC (named-pipe NDJSON) + `ytile` CLI verbs
- [x] Focus border (`DWMWA_BORDER_COLOR`, Win11)
- [x] Reaper (liveness sweep)
- [x] Floating layer: auto-float when an app's minimum size exceeds its cell, `ytile float` toggle
- [x] Workspaces: 9 per monitor, cloak-based hiding (`IApplicationView::SetCloak`), crash-safe restore
- [x] IPC subscription stream + work-area reservation for bars ([docs/YTILE-IPC.md](docs/YTILE-IPC.md))
- [x] Config file: `~/.config/ytile/ytile.json` — gap, border color, default layout, window rules
  (ignore/float by exe/class/title; built-in ignores for status bars), `ytile reload`
- [x] Monitor reconciliation (hotplug, resume, work-area changes)
- [x] Drag-to-swap (drop a window on another cell), monocle (`ytile monocle`)
- [x] Resize deltas: edge-drags persist into the layout (BSP split ratios / column
  weights), `ytile resize <dir> [px]` for keyboard resizing, `ytile retile` resets
- [x] Cross-monitor focus/move: directional commands continue onto the adjacent
  monitor at the workspace edge
- [x] Workspace assignments survive pause/resume and `ytile reload` (placement
  snapshot + restore around the re-adoption pass)
- [x] Packaging: `scripts/publish.ps1`, `ytile start` (background daemon, file log),
  `ytile autostart on|off|status` (HKCU Run entry)

## Install

```powershell
irm https://raw.githubusercontent.com/AltimG/YTile/main/scripts/install.ps1 | iex
```

Downloads the latest release into `%LOCALAPPDATA%\Programs\ytile`, adds it to
your user PATH, and writes a starter config if you don't have one. Per-user, no
admin rights, nothing outside your profile. Options go in the environment,
since a piped script takes no parameters:

```powershell
$env:YTILE_AUTOSTART = 1    # also start YTile at login
$env:YTILE_START     = 1    # start the daemon when the install finishes
$env:YTILE_VERSION   = 'v0.1.0'   # pin a version instead of latest
$env:YTILE_UNINSTALL = 1    # remove YTile (config is left alone)
```

Re-run it any time to upgrade — it stops a running daemon first, since the
binaries are locked while it runs. Hotkeys need
[whkd](https://github.com/LGUG2Z/whkd) separately; see
[examples/whkdrc-ytile](examples/whkdrc-ytile).

## Building

Requires the .NET 10 SDK, plus VS Build Tools with the C++ workload for the
NativeAOT linker.

```
dotnet build                # dev build (JIT)
pwsh scripts/publish.ps1    # NativeAOT ytiled.exe + ytile.exe -> publish/
```

## Running

```
ytile start              # launch ytiled in the background
                         #   (logs to %LOCALAPPDATA%\ytile\ytiled.log)
ytile autostart on       # launch it at every login (ytile autostart off|status)

ytiled                   # or run it in the foreground (auto-pauses if komorebi.exe is running)
ytiled --dry-run         # log every SetWindowPos/focus instead of applying it
ytiled --force           # manage even while komorebi is running (they will fight)
ytiled --debug-events    # watch the window-event stream with tiling verdicts

ytile state              # monitors, windows, layout, focus
ytile focus left         # directional focus
ytile move right         # swap focused window in a direction
ytile resize left 80     # grow the focused window 80px leftward (negative shrinks)
ytile workspace 2        # switch the focused monitor's workspace (1-9)
ytile send 3             # send the focused window to a workspace
ytile layout columns     # bsp | columns, per active workspace
ytile float              # toggle floating for the focused window
ytile pause / resume / retile / stop
```

Windows that refuse to shrink to their cell (launchers like Battle.net enforce a
minimum size) are detected automatically and float instead of overlapping the layout.

Dragging a tiled window's edge resizes it for real: the new size is folded into
the layout (per-split ratios in BSP, per-column weights in Columns) and survives
retiles. `ytile resize <dir> [px]` does the same from the keyboard (`resizeStep`
sets the default amount); `ytile retile` resets all adjustments.

## Configuration

`~/.config/ytile/ytile.json` (see [examples/ytile.json](examples/ytile.json); all keys optional):

```json
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
```

`hideTaskbar` hides the shell taskbar outright (not Windows' auto-hide setting)
and tiles over the space it occupied — Windows keeps reserving the strip in the
work area, so YTile switches to full monitor bounds while the bar is gone. It is
restored whenever YTile stops or pauses. Auto-hide needs no setting: Windows
already reports the reclaimed space, and layouts follow it.

Rules match on `exe`/`class`/`title` with `equals` (default), `prefix`, or `regex`
strategies; actions are `ignore` and `float`. Status bars (komorebi-bar, ybar,
zebar, yasb) are ignored built-in. `ytile reload` applies changes live.
Hotkeys: pair with [whkd](https://github.com/LGUG2Z/whkd) — see
[examples/whkdrc-ytile](examples/whkdrc-ytile).

## License

GPL-3.0 — see [LICENSE](LICENSE).
