using System.Threading.Channels;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using YTile.Core;
using YTile.Protocol;
using YTile.Win32;

namespace YTile.Runtime;

internal sealed class ManagedWindow(nint hwnd, uint pid, string exe)
{
    public nint Hwnd { get; } = hwnd;
    public uint Pid { get; } = pid;
    public string Exe { get; } = exe;
    public RectI LastRect { get; set; }
}

internal sealed class Workspace
{
    public LayoutKind Layout { get; set; } = LayoutKind.Bsp;
    public List<ManagedWindow> Windows { get; } = [];
    public int FocusedIndex { get; set; }

    public ManagedWindow? Focused =>
        FocusedIndex >= 0 && FocusedIndex < Windows.Count ? Windows[FocusedIndex] : null;
}

/// <summary>
/// The single-actor window manager: owns all state, consumes the unified
/// message queue. Nothing outside this class mutates monitors or workspaces.
/// </summary>
internal sealed class WindowManager(string version, bool dryRun, bool startPaused, int gap)
{
    // COLORREF is 0x00BBGGRR — this is #569CD6 (calm blue).
    private const uint FocusBorderColor = 0x00D69C56;

    private readonly List<(MonitorDesc Desc, Workspace Ws)> _monitors = [];
    private readonly Dictionary<nint, int> _windowMonitor = [];
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private bool _paused = startPaused;
    private int _focusedMonitor;
    private nint _borderHwnd;
    private nint _dragHwnd;

    public async Task RunAsync(ChannelReader<WmMessage> reader, CancellationToken ct)
    {
        Bootstrap();
        try
        {
            await foreach (WmMessage msg in reader.ReadAllAsync(ct))
            {
                try
                {
                    switch (msg)
                    {
                        case WmMessage.Os(var e):
                            HandleOsEvent(e);
                            break;
                        case WmMessage.Command(var req, var tcs):
                            // Canceled = the IPC side timed out and already told
                            // the client it failed; executing now would be worse.
                            if (tcs.Task.IsCompleted)
                            {
                                break;
                            }
                            try
                            {
                                tcs.TrySetResult(HandleCommand(req));
                            }
                            catch (Exception ex)
                            {
                                tcs.TrySetResult(new CommandReply(false, ex.Message));
                            }
                            break;
                        case WmMessage.ReaperTick:
                            ReapDeadWindows();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log($"error processing {msg.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        ClearBorder();
    }

    private void Bootstrap()
    {
        foreach (MonitorDesc desc in MonitorProbe.Enumerate())
        {
            _monitors.Add((desc, new Workspace()));
            Log($"monitor {desc.Device} work={desc.WorkArea}{(desc.Primary ? " primary" : "")}");
        }

        if (_monitors.Count == 0)
        {
            throw new InvalidOperationException("no monitors found");
        }

        Adopt();
        SeedFocusFromForeground();
        Log($"adopted {_windowMonitor.Count} windows{(_paused ? " (paused)" : "")}");
        if (!_paused)
        {
            RetileAll();
        }
    }

    /// <summary>Aligns focused monitor/window with whatever really has foreground.</summary>
    private unsafe void SeedFocusFromForeground()
    {
        nint foreground = (nint)PInvoke.GetForegroundWindow().Value;
        if (foreground == 0 || !_windowMonitor.TryGetValue(foreground, out int monitor))
        {
            return;
        }

        (_, Workspace ws) = _monitors[monitor];
        int idx = ws.Windows.FindIndex(w => w.Hwnd == foreground);
        if (idx >= 0)
        {
            ws.FocusedIndex = idx;
            _focusedMonitor = monitor;
            if (!_paused)
            {
                SetBorder(foreground);
            }
        }
    }

    /// <summary>Adds every eligible pre-existing top-level window to the model.</summary>
    private void Adopt()
    {
        foreach (nint hwnd in WindowEnumerator.TopLevelWindows())
        {
            TryAddWindow(hwnd, retile: false, adopting: true);
        }
    }

    /// <summary>
    /// Drops all window state and rebuilds it from the OS, re-enumerating
    /// monitors too (docking/undocking may have happened while paused).
    /// Layout choices survive by monitor device name.
    /// </summary>
    private void Resync()
    {
        List<MonitorDesc> fresh = MonitorProbe.Enumerate();
        if (fresh.Count > 0)
        {
            var layouts = new Dictionary<string, LayoutKind>();
            foreach ((MonitorDesc desc, Workspace ws) in _monitors)
            {
                layouts[desc.Device] = ws.Layout;
            }

            _monitors.Clear();
            foreach (MonitorDesc desc in fresh)
            {
                var ws = new Workspace();
                if (layouts.TryGetValue(desc.Device, out LayoutKind kind))
                {
                    ws.Layout = kind;
                }
                _monitors.Add((desc, ws));
            }
        }
        else
        {
            foreach ((_, Workspace ws) in _monitors)
            {
                ws.Windows.Clear();
                ws.FocusedIndex = 0;
            }
        }

        _focusedMonitor = 0;
        _windowMonitor.Clear();
        Adopt();
        SeedFocusFromForeground();
    }

    private void HandleOsEvent(RawWinEvent e)
    {
        if (_paused)
        {
            return;
        }

        switch (WinEventMap.Map(e.EventId))
        {
            case WmEventKind.Show:
            case WmEventKind.Uncloak:
                if (!_windowMonitor.ContainsKey(e.Hwnd))
                {
                    TryAddWindow(e.Hwnd, retile: true, adopting: false);
                }
                break;

            case WmEventKind.Destroy:
            case WmEventKind.Hide:
            case WmEventKind.Cloak:
            case WmEventKind.Minimize:
                RemoveWindow(e.Hwnd);
                break;

            case WmEventKind.FocusChange:
                HandleFocusChange(e.Hwnd);
                break;

            case WmEventKind.MoveResizeStart:
                // Don't yank a window out from under an active drag.
                if (_windowMonitor.ContainsKey(e.Hwnd))
                {
                    _dragHwnd = e.Hwnd;
                }
                break;

            case WmEventKind.MoveResizeEnd:
                _dragHwnd = 0;
                // v0: any user drag/resize snaps back to the layout.
                if (_windowMonitor.TryGetValue(e.Hwnd, out int monitor))
                {
                    Retile(monitor);
                }
                break;
        }
    }

    private unsafe void HandleFocusChange(nint hwnd)
    {
        // Stale event — a newer window already took foreground.
        if ((nint)PInvoke.GetForegroundWindow().Value != hwnd)
        {
            return;
        }

        // Unknown-but-eligible windows arrive here when Show raced us
        // (e.g. restored from minimize before IsIconic cleared).
        if (!_windowMonitor.TryGetValue(hwnd, out int monitor))
        {
            if (!TryAddWindow(hwnd, retile: true, adopting: false))
            {
                return;
            }
            monitor = _windowMonitor[hwnd];
        }

        (_, Workspace ws) = _monitors[monitor];
        int idx = ws.Windows.FindIndex(w => w.Hwnd == hwnd);
        if (idx >= 0)
        {
            ws.FocusedIndex = idx;
            _focusedMonitor = monitor;
            SetBorder(hwnd);
        }
    }

    private unsafe bool TryAddWindow(nint hwnd, bool retile, bool adopting)
    {
        if (_windowMonitor.ContainsKey(hwnd))
        {
            return false;
        }

        var snapshot = WindowSnapshot.Capture(hwnd);
        if (snapshot.ProcessId == _ownPid || !snapshot.Visible || snapshot.Iconic)
        {
            return false;
        }
        if (Eligibility.SkipReason(in snapshot) is not null)
        {
            return false;
        }

        // A maximized window would keep WS_MAXIMIZE while we position it,
        // desyncing its restore behavior — restore it before tiling.
        if (snapshot.Zoomed && !dryRun)
        {
            PInvoke.ShowWindow(new HWND(hwnd), SHOW_WINDOW_CMD.SW_RESTORE);
        }

        int monitor = MonitorIndexFor(hwnd);
        (_, Workspace ws) = _monitors[monitor];
        // Adoption preserves enumeration (Z) order; live adds go next to focus.
        int insertAt = adopting ? ws.Windows.Count : Math.Min(ws.FocusedIndex + 1, ws.Windows.Count);
        ws.Windows.Insert(insertAt, new ManagedWindow(hwnd, snapshot.ProcessId, snapshot.ExeName));
        _windowMonitor[hwnd] = monitor;

        if (!adopting)
        {
            Log($"manage 0x{hwnd:X8} {snapshot.ExeName} \"{snapshot.Title}\" -> monitor {monitor}");
        }
        if (retile)
        {
            Retile(monitor);
        }

        return true;
    }

    private unsafe void RemoveWindow(nint hwnd)
    {
        if (!_windowMonitor.Remove(hwnd, out int monitor))
        {
            return;
        }

        (_, Workspace ws) = _monitors[monitor];
        int idx = ws.Windows.FindIndex(w => w.Hwnd == hwnd);
        if (idx >= 0)
        {
            ws.Windows.RemoveAt(idx);
            if (idx < ws.FocusedIndex)
            {
                // Everything above the removed slot shifted down by one.
                ws.FocusedIndex--;
            }
            else if (ws.FocusedIndex >= ws.Windows.Count)
            {
                ws.FocusedIndex = Math.Max(0, ws.Windows.Count - 1);
            }
        }

        if (_dragHwnd == hwnd)
        {
            _dragHwnd = 0;
        }

        if (_borderHwnd == hwnd)
        {
            // Hidden/minimized/cloaked windows still exist — clear the border
            // attribute so it doesn't linger when the window comes back.
            if (!dryRun && PInvoke.IsWindow(new HWND(hwnd)))
            {
                FocusControl.SetBorder(hwnd, null);
            }
            _borderHwnd = 0;
        }

        Log($"unmanage 0x{hwnd:X8}");
        Retile(monitor);
    }

    private int MonitorIndexFor(nint hwnd)
    {
        nint handle = MonitorProbe.MonitorForWindow(hwnd);
        for (int i = 0; i < _monitors.Count; i++)
        {
            if (_monitors[i].Desc.Handle == handle)
            {
                return i;
            }
        }

        return 0;
    }

    private void Retile(int monitor)
    {
        (MonitorDesc desc, Workspace ws) = _monitors[monitor];
        RectI[] cells = Layouts.Compute(ws.Layout, desc.WorkArea, ws.Windows.Count, gap);
        for (int i = 0; i < ws.Windows.Count; i++)
        {
            ws.Windows[i].LastRect = cells[i];
            if (ws.Windows[i].Hwnd == _dragHwnd)
            {
                continue; // mid-drag; MoveResizeEnd will snap it back
            }
            WindowPositioner.Apply(ws.Windows[i].Hwnd, cells[i], dryRun);
        }
    }

    private void RetileAll()
    {
        for (int i = 0; i < _monitors.Count; i++)
        {
            Retile(i);
        }
    }

    private unsafe void ReapDeadWindows()
    {
        if (_paused)
        {
            return;
        }

        List<nint>? dead = null;
        foreach (nint hwnd in _windowMonitor.Keys)
        {
            var h = new HWND(hwnd);
            // IsIconic backstops a missed MINIMIZESTART — a minimized window
            // must not keep occupying a layout slot.
            if (!PInvoke.IsWindow(h) || !PInvoke.IsWindowVisible(h) || PInvoke.IsIconic(h))
            {
                (dead ??= []).Add(hwnd);
            }
        }

        if (dead is null)
        {
            return;
        }

        foreach (nint hwnd in dead)
        {
            RemoveWindow(hwnd);
        }
    }

    private CommandReply HandleCommand(CommandRequest req)
    {
        switch (req.Cmd)
        {
            case "version":
                return new CommandReply(true, Message: version);

            case "state":
                return new CommandReply(true, State: BuildState());

            case "pause":
                _paused = true;
                ClearBorder();
                Log("paused");
                return new CommandReply(true, Message: "paused");

            case "resume":
                _paused = false;
                if (!dryRun)
                {
                    // Deferred from startup when we began paused or dry.
                    FocusControl.Init();
                }
                Resync();
                RetileAll();
                Log($"resumed, managing {_windowMonitor.Count} windows");
                return new CommandReply(true, Message: $"resumed ({_windowMonitor.Count} windows)");

            case "retile":
                if (_paused)
                {
                    return new CommandReply(false, "paused — resume first");
                }
                RetileAll();
                return new CommandReply(true, Message: "retiled");

            case "layout":
            {
                LayoutKind? kind = req.Arg?.ToLowerInvariant() switch
                {
                    "bsp" => LayoutKind.Bsp,
                    "columns" => LayoutKind.Columns,
                    _ => null,
                };
                if (kind is null)
                {
                    return new CommandReply(false, $"unknown layout '{req.Arg}' (bsp|columns)");
                }
                _monitors[_focusedMonitor].Ws.Layout = kind.Value;
                if (!_paused)
                {
                    Retile(_focusedMonitor);
                }
                return new CommandReply(true, Message: $"layout {req.Arg} on monitor {_focusedMonitor}");
            }

            case "focus":
            case "move":
            {
                if (_paused)
                {
                    return new CommandReply(false, "paused — resume first");
                }
                Direction? dir = DirectionParser.Parse(req.Arg);
                if (dir is null)
                {
                    return new CommandReply(false, $"unknown direction '{req.Arg}' (left|right|up|down)");
                }

                Workspace ws = _monitors[_focusedMonitor].Ws;
                int target = DirectionalTarget(ws, dir.Value);
                if (target < 0)
                {
                    return new CommandReply(false, $"no window {req.Arg} of focused");
                }

                if (req.Cmd == "focus")
                {
                    nint hwnd = ws.Windows[target].Hwnd;
                    if (dryRun)
                    {
                        Log($"DRYRUN focus 0x{hwnd:X8}");
                    }
                    else
                    {
                        FocusControl.Focus(hwnd);
                    }
                    return new CommandReply(true, Message: $"focus 0x{hwnd:X8}");
                }

                (ws.Windows[ws.FocusedIndex], ws.Windows[target]) = (ws.Windows[target], ws.Windows[ws.FocusedIndex]);
                ws.FocusedIndex = target;
                Retile(_focusedMonitor);
                return new CommandReply(true, Message: "moved");
            }

            default:
                return new CommandReply(false, $"unknown command '{req.Cmd}'");
        }
    }

    /// <summary>Nearest window whose center lies in the given direction.</summary>
    private static int DirectionalTarget(Workspace ws, Direction dir)
    {
        ManagedWindow? focused = ws.Focused;
        if (focused is null)
        {
            return -1;
        }

        (int cx, int cy) = (focused.LastRect.CenterX, focused.LastRect.CenterY);
        int best = -1;
        long bestDist = long.MaxValue;
        for (int i = 0; i < ws.Windows.Count; i++)
        {
            if (i == ws.FocusedIndex)
            {
                continue;
            }

            int dx = ws.Windows[i].LastRect.CenterX - cx;
            int dy = ws.Windows[i].LastRect.CenterY - cy;
            bool inDirection = dir switch
            {
                Direction.Left => dx < 0 && Math.Abs(dx) >= Math.Abs(dy),
                Direction.Right => dx > 0 && Math.Abs(dx) >= Math.Abs(dy),
                Direction.Up => dy < 0 && Math.Abs(dy) >= Math.Abs(dx),
                Direction.Down => dy > 0 && Math.Abs(dy) >= Math.Abs(dx),
                _ => false,
            };
            if (!inDirection)
            {
                continue;
            }

            long dist = (long)dx * dx + (long)dy * dy;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }

        return best;
    }

    private StateDto BuildState()
    {
        var monitors = new List<MonitorDto>(_monitors.Count);
        foreach ((MonitorDesc desc, Workspace ws) in _monitors)
        {
            var windows = new List<WindowDto>(ws.Windows.Count);
            foreach (ManagedWindow w in ws.Windows)
            {
                var snapshot = WindowSnapshot.Capture(w.Hwnd);
                windows.Add(new WindowDto(
                    w.Hwnd,
                    w.Pid,
                    w.Exe,
                    snapshot.IsAlive ? snapshot.Title : "(gone)",
                    new RectDto(w.LastRect.X, w.LastRect.Y, w.LastRect.W, w.LastRect.H)));
            }

            monitors.Add(new MonitorDto(
                desc.Device,
                desc.Primary,
                new RectDto(desc.WorkArea.X, desc.WorkArea.Y, desc.WorkArea.W, desc.WorkArea.H),
                new WorkspaceDto(ws.Layout.ToString().ToLowerInvariant(), ws.FocusedIndex, windows)));
        }

        return new StateDto(version, _paused, dryRun, monitors);
    }

    private void SetBorder(nint hwnd)
    {
        if (dryRun || hwnd == _borderHwnd)
        {
            return;
        }

        if (_borderHwnd != 0)
        {
            FocusControl.SetBorder(_borderHwnd, null);
        }
        FocusControl.SetBorder(hwnd, FocusBorderColor);
        _borderHwnd = hwnd;
    }

    private void ClearBorder()
    {
        if (_borderHwnd != 0)
        {
            FocusControl.SetBorder(_borderHwnd, null);
            _borderHwnd = 0;
        }
    }

    private static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
}
