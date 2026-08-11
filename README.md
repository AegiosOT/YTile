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
- [ ] Eligibility + window tracking (adoption pass, `known_hwnds`)
- [ ] Layouts: BSP, Columns
- [ ] Focus + directional movement
- [ ] Workspaces (cloak-based hiding)
- [ ] Reaper + application quirks table
- [ ] IPC (NDJSON) + `ytile` CLI verbs
- [ ] Focus border (`DWMWA_BORDER_COLOR`)
- [ ] Monitor reconciliation

## Building

Requires the .NET 10 SDK.

```
dotnet build                                          # dev build (JIT)
dotnet publish src/YTile -r win-x64 -c Release        # NativeAOT daemon  -> ytiled.exe
dotnet publish src/YTile.Cli -r win-x64 -c Release    # NativeAOT CLI     -> ytile.exe
```

## Running

```
ytiled --debug-events    # watch the window-event stream with tiling verdicts
```

## License

GPL-3.0 — see [LICENSE](LICENSE).
