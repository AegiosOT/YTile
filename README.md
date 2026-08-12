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

## Building

Requires the .NET 10 SDK.

```
dotnet build                                          # dev build (JIT)
dotnet publish src/YTile -r win-x64 -c Release        # NativeAOT daemon  -> ytiled.exe
dotnet publish src/YTile.Cli -r win-x64 -c Release    # NativeAOT CLI     -> ytile.exe
```

## Running

```
ytiled                   # start the daemon (auto-pauses if komorebi.exe is running)
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
  "rules": [
    { "match": "exe", "pattern": "Battle.net.exe", "action": "float" },
    { "match": "title", "pattern": "Picture.in.[Pp]icture", "strategy": "regex", "action": "float" }
  ]
}
```

Rules match on `exe`/`class`/`title` with `equals` (default), `prefix`, or `regex`
strategies; actions are `ignore` and `float`. Status bars (komorebi-bar, ybar,
zebar, yasb) are ignored built-in. `ytile reload` applies changes live.
Hotkeys: pair with [whkd](https://github.com/LGUG2Z/whkd) — see
[examples/whkdrc-ytile](examples/whkdrc-ytile).

## License

GPL-3.0 — see [LICENSE](LICENSE).
