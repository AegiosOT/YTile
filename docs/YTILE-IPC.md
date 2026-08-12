# YTile IPC — integration guide (bars, scripts, external tools)

Everything talks to the daemon over one named pipe: `\\.\pipe\ytile`
(NDJSON both directions, UTF-8, one JSON document per line). The `ytile` CLI
is a thin client over the same protocol — anything below can also be driven by
shelling out to it.

## Request/reply

Connect, write one line, read one line, disconnect:

```
> {"cmd":"state"}
< {"ok":true,"state":{...}}

> {"cmd":"workspace","arg":"2"}
< {"ok":true,"message":"workspace 2"}
```

Verbs: `version, state, pause, resume, retile, reload, stop, float, monocle,
layout <bsp|columns>, focus <dir>, move <dir>, resize <dir> [px],
workspace <1-9>, send <1-9>,
reserve <monitor> <left> <top> <right> <bottom>` (multi-token args are one
space-joined `arg` string). Errors: `{"ok":false,"error":"..."}`.
The server enforces a 5 s read deadline per connection; up to 8 concurrent
pipe instances.

## Subscription (bars)

Send `{"cmd":"subscribe"}`. The reply `{"ok":true,"message":"subscribed"}` is
followed by a stream — one line per state change, connection stays open:

```json
{"event":"workspace_change","state":{ ...full StateDto... }}
```

Event names: `ready, manage, unmanage, focus_change, workspace_change,
layout_change, monocle, float_change, move, resize, retile, reserve, reload,
pause, resume, monitors_change`.

**`ready` is not once-per-daemon.** It is emitted at startup *and* again
whenever a new subscriber attaches, so a bar that starts late — or reconnects
after the daemon restarted — still gets the cue to re-apply its `reserve`
(reservations do not survive a restart). Treat `ready` as "re-assert your
setup now", make that handler idempotent, and expect to receive it when
another subscriber joins.
Every notification carries the **full state snapshot** (komorebi-style — no
delta tracking needed). A write failure drops the subscriber; reconnect by
re-subscribing. `ytile subscribe` prints this stream to stdout.

## State shape

```json
{
  "version": "0.1.0-dev",
  "paused": false,
  "dryRun": false,
  "monitors": [
    {
      "device": "\\\\.\\DISPLAY10",
      "primary": true,
      "workArea": {"x":0,"y":48,"w":2880,"h":1752},   // effective: reservations subtracted
      "active": 0,                                     // active workspace index (0-based)
      "workspaces": [
        {
          "layout": "bsp",
          "focused": 0,                                // focused tiled-window index
          "windows":  [ {"hwnd":123,"pid":1,"exe":"chrome.exe","title":"...","rect":{"x":8,"y":56,"w":1428,"h":1736}} ],
          "floating": [ ... same shape, rect = real window geometry ... ]
        }
        // ... always 9 workspaces per monitor
      ]
    }
  ]
}
```

Notes for a workspaces widget: focused workspace = `monitors[m].active`
(0-based; display as `active+1`); non-empty = `windows` or `floating`
non-empty; app identity comes from `exe`. All rects are physical pixels,
y-down. Hwnds are JSON numbers (may exceed 2^31 — parse as 64-bit).

## Work-area reservation handshake (bars)

On start and on every bar-height/monitor change:

```
ytile reserve <monitor> 0 <H> 0 0        # top bar of physical height H
```

- Monitor index = position in the `state` monitors array (primary first,
  then left-to-right).
- The strip is subtracted from the tiling work area immediately; layouts
  recompute. Reservations survive resume/monitor-resync (matched by device
  name) but NOT a daemon restart — re-apply on the `ready` event.
- On bar exit: `ytile reserve <monitor> 0 0 0 0`.
- Do not double-reserve (appbar + `reserve`) — same rule as komorebi offsets.

## Differences from komorebi's protocol

- Transport: named pipe, not AF_UNIX socket; NDJSON both ways; subscribers
  keep ONE connection (komorebi dials back per event).
- One flat schema (no Ring wrappers, no adjacently-tagged enums): state is
  plain arrays + an `active`/`focused` index.
- `reserve` replaces `MonitorWorkAreaOffset`.
- Workspaces are fixed at 9 per monitor, 0-based in the protocol, 1-based in
  CLI/UI.
