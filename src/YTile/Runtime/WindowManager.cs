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

    // Rect we last asked the OS for, so the reaper can detect windows whose
    // WM_GETMINMAXINFO minimum silently clamped the resize above the cell.
    public int RequestedX { get; set; }
    public int RequestedY { get; set; }
    public int RequestedW { get; set; }
    public int RequestedH { get; set; }
    public long RequestedAtMs { get; set; }
    public bool PendingVerify { get; set; }

    // Set when the user explicitly re-tiled this window via 'ytile float':
    // respect that choice even if the window overflows its cell.
    public bool NoAutoFloat { get; set; }
}

internal sealed class Workspace
{
    public LayoutKind Layout { get; set; } = LayoutKind.Bsp;
    public List<ManagedWindow> Windows { get; } = [];
    public List<ManagedWindow> Floating { get; } = [];
    public int FocusedIndex { get; set; }

    public ManagedWindow? Focused =>
        FocusedIndex >= 0 && FocusedIndex < Windows.Count ? Windows[FocusedIndex] : null;

    public bool IsEmpty => Windows.Count == 0 && Floating.Count == 0;
}

internal sealed class MonitorCtx
{
    public MonitorCtx(MonitorDesc desc)
    {
        Desc = desc;
        for (int i = 0; i < WindowManager.WorkspaceCount; i++)
        {
            Workspaces.Add(new Workspace());
        }
    }

    public MonitorDesc Desc { get; }
    public List<Workspace> Workspaces { get; } = [];
    public int Active { get; set; }
    public Workspace ActiveWs => Workspaces[Active];
}

/// <summary>
/// The single-actor window manager: owns all state, consumes the unified
/// message queue. Nothing outside this class mutates monitors or workspaces.
/// Windows on inactive workspaces are cloaked (see CloakControl); the
/// _selfCloaked set suppresses the Cloak/Hide events our own hiding produces
/// so they are not mistaken for windows going away.
/// </summary>
internal sealed class WindowManager(string version, bool dryRun, bool startPaused, int gap)
{
    public const int WorkspaceCount = 9;

    // COLORREF is 0x00BBGGRR — this is #569CD6 (calm blue).
    private const uint FocusBorderColor = 0x00D69C56;

    // A clamped resize larger than this margin means the window refused the cell.
    private const int FitTolerance = 8;

    private readonly List<MonitorCtx> _monitors = [];
    private readonly Dictionary<nint, (int M, int W)> _windowLoc = [];
    private readonly HashSet<nint> _selfCloaked = [];
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private bool _paused = startPaused;
    private bool _stopRequested;
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
                            VerifyCellFits();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log($"error processing {msg.GetType().Name}: {ex.Message}");
                }

                if (_stopRequested)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        // Never exit leaving windows invisible.
        UncloakAll();
        ClearBorder();
    }

    private void Bootstrap()
    {
        CloakControl.Init();

        // Crash insurance: restore anything a dead ytiled left cloaked.
        foreach (nint leftover in CloakPersistence.LoadAndDelete())
        {
            if (IsWindowAlive(leftover))
            {
                CloakControl.SetCloak(leftover, false);
                Log($"restored 0x{leftover:X8} cloaked by a previous instance");
            }
        }

        foreach (MonitorDesc desc in MonitorProbe.Enumerate())
        {
            _monitors.Add(new MonitorCtx(desc));
            Log($"monitor {desc.Device} work={desc.WorkArea}{(desc.Primary ? " primary" : "")}");
        }

        if (_monitors.Count == 0)
        {
            throw new InvalidOperationException("no monitors found");
        }

        Adopt();
        SeedFocusFromForeground();
        Log($"adopted {_windowLoc.Count} windows{(_paused ? " (paused)" : "")}");
        if (!_paused)
        {
            RetileAll();
        }
    }

    /// <summary>Aligns focused monitor/window with whatever really has foreground.</summary>
    private void SeedFocusFromForeground()
    {
        nint foreground = ForegroundHwnd();
        if (foreground == 0 || !_windowLoc.TryGetValue(foreground, out (int M, int W) loc))
        {
            return;
        }

        MonitorCtx mc = _monitors[loc.M];
        if (loc.W != mc.Active)
        {
            return;
        }

        int idx = mc.ActiveWs.Windows.FindIndex(w => w.Hwnd == foreground);
        if (idx >= 0)
        {
            mc.ActiveWs.FocusedIndex = idx;
            _focusedMonitor = loc.M;
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
    /// Everything lands on each monitor's active workspace; per-device layout
    /// choices survive. All cloaked windows are restored first so the
    /// adoption pass can see them.
    /// </summary>
    private void Resync()
    {
        UncloakAll();

        List<MonitorDesc> fresh = MonitorProbe.Enumerate();
        if (fresh.Count > 0)
        {
            var layouts = new Dictionary<string, List<LayoutKind>>();
            foreach (MonitorCtx mc in _monitors)
            {
                layouts[mc.Desc.Device] = [.. mc.Workspaces.Select(w => w.Layout)];
            }

            _monitors.Clear();
            foreach (MonitorDesc desc in fresh)
            {
                var mc = new MonitorCtx(desc);
                if (layouts.TryGetValue(desc.Device, out List<LayoutKind>? kinds))
                {
                    for (int i = 0; i < WorkspaceCount; i++)
                    {
                        mc.Workspaces[i].Layout = kinds[i];
                    }
                }
                _monitors.Add(mc);
            }
        }
        else
        {
            foreach (MonitorCtx mc in _monitors)
            {
                foreach (Workspace ws in mc.Workspaces)
                {
                    ws.Windows.Clear();
                    ws.Floating.Clear();
                    ws.FocusedIndex = 0;
                }
                mc.Active = 0;
            }
        }

        _focusedMonitor = 0;
        _windowLoc.Clear();
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
                if (!_windowLoc.ContainsKey(e.Hwnd))
                {
                    TryAddWindow(e.Hwnd, retile: true, adopting: false);
                }
                break;

            case WmEventKind.Cloak:
            case WmEventKind.Hide:
                // Our own hiding fires these — ignore them or every workspace
                // switch would unmanage its windows.
                if (!_selfCloaked.Contains(e.Hwnd))
                {
                    RemoveWindow(e.Hwnd);
                }
                break;

            case WmEventKind.Destroy:
            case WmEventKind.Minimize:
                RemoveWindow(e.Hwnd);
                break;

            case WmEventKind.FocusChange:
                HandleFocusChange(e.Hwnd);
                break;

            case WmEventKind.MoveResizeStart:
                // Don't yank a window out from under an active drag.
                if (_windowLoc.ContainsKey(e.Hwnd))
                {
                    _dragHwnd = e.Hwnd;
                }
                break;

            case WmEventKind.MoveResizeEnd:
                _dragHwnd = 0;
                // v0: any user drag/resize of a TILED window snaps back to the
                // layout; floating windows move freely.
                if (_windowLoc.TryGetValue(e.Hwnd, out (int M, int W) loc)
                    && loc.W == _monitors[loc.M].Active
                    && !IsFloating(loc, e.Hwnd))
                {
                    Retile(loc.M);
                }
                break;
        }
    }

    private void HandleFocusChange(nint hwnd)
    {
        // Stale event — a newer window already took foreground.
        if (ForegroundHwnd() != hwnd)
        {
            return;
        }

        if (!_windowLoc.TryGetValue(hwnd, out (int M, int W) loc))
        {
            // Unknown-but-eligible windows arrive here when Show raced us
            // (e.g. restored from minimize before IsIconic cleared).
            if (!TryAddWindow(hwnd, retile: true, adopting: false))
            {
                return;
            }
            loc = _windowLoc[hwnd];
        }

        MonitorCtx mc = _monitors[loc.M];
        if (loc.W != mc.Active)
        {
            // A window on a hidden workspace took foreground (notification
            // click, app self-activation) — follow it there.
            Log($"foreground on hidden workspace — switching monitor {loc.M} to workspace {loc.W + 1}");
            SwitchWorkspace(loc.M, loc.W, focusTarget: hwnd);
            return;
        }

        int idx = mc.ActiveWs.Windows.FindIndex(w => w.Hwnd == hwnd);
        if (idx >= 0)
        {
            mc.ActiveWs.FocusedIndex = idx;
            _focusedMonitor = loc.M;
            SetBorder(hwnd);
        }
        else if (IsFloating(loc, hwnd))
        {
            _focusedMonitor = loc.M;
            SetBorder(hwnd);
        }
    }

    private unsafe bool TryAddWindow(nint hwnd, bool retile, bool adopting)
    {
        if (_windowLoc.ContainsKey(hwnd))
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
        MonitorCtx mc = _monitors[monitor];
        Workspace ws = mc.ActiveWs;
        // Adoption preserves enumeration (Z) order; live adds go next to focus.
        int insertAt = adopting ? ws.Windows.Count : Math.Min(ws.FocusedIndex + 1, ws.Windows.Count);
        ws.Windows.Insert(insertAt, new ManagedWindow(hwnd, snapshot.ProcessId, snapshot.ExeName));
        _windowLoc[hwnd] = (monitor, mc.Active);

        if (!adopting)
        {
            Log($"manage 0x{hwnd:X8} {snapshot.ExeName} \"{snapshot.Title}\" -> monitor {monitor} ws {mc.Active + 1}");
        }
        if (retile)
        {
            Retile(monitor);
        }

        return true;
    }

    private unsafe void RemoveWindow(nint hwnd)
    {
        if (!_windowLoc.Remove(hwnd, out (int M, int W) loc))
        {
            return;
        }

        if (_selfCloaked.Remove(hwnd))
        {
            CloakPersistence.Save(_selfCloaked);
        }

        MonitorCtx mc = _monitors[loc.M];
        Workspace ws = mc.Workspaces[loc.W];
        bool wasTiled = false;
        int idx = ws.Windows.FindIndex(w => w.Hwnd == hwnd);
        if (idx >= 0)
        {
            wasTiled = true;
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
        else
        {
            int floatIdx = ws.Floating.FindIndex(w => w.Hwnd == hwnd);
            if (floatIdx >= 0)
            {
                ws.Floating.RemoveAt(floatIdx);
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

        Log($"unmanage 0x{hwnd:X8}{(loc.W == mc.Active ? "" : $" (ws {loc.W + 1})")}");
        // A floating window occupies no cell; hidden workspaces retile on switch.
        if (wasTiled && loc.W == mc.Active)
        {
            Retile(loc.M);
        }
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
        MonitorCtx mc = _monitors[monitor];
        Workspace ws = mc.ActiveWs;
        RectI[] cells = Layouts.Compute(ws.Layout, mc.Desc.WorkArea, ws.Windows.Count, gap);
        for (int i = 0; i < ws.Windows.Count; i++)
        {
            ManagedWindow w = ws.Windows[i];
            w.LastRect = cells[i];
            if (w.Hwnd == _dragHwnd)
            {
                continue; // mid-drag; MoveResizeEnd will snap it back
            }
            RectI adjusted = WindowPositioner.Apply(w.Hwnd, cells[i], dryRun);
            if (!dryRun)
            {
                w.RequestedX = adjusted.X;
                w.RequestedY = adjusted.Y;
                w.RequestedW = adjusted.W;
                w.RequestedH = adjusted.H;
                w.RequestedAtMs = Environment.TickCount64;
                w.PendingVerify = true;
            }
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
        foreach ((nint hwnd, (int M, int W) loc) in _windowLoc)
        {
            var h = new HWND(hwnd);
            if (!PInvoke.IsWindow(h))
            {
                (dead ??= []).Add(hwnd);
                continue;
            }

            // Visibility/iconic checks only apply to the visible workspace:
            // cloak-fallback (SW_HIDE) windows are legitimately invisible, and
            // IsIconic backstops a missed MINIMIZESTART — a minimized window
            // must not keep occupying a layout slot.
            if (loc.W == _monitors[loc.M].Active
                && !_selfCloaked.Contains(hwnd)
                && (!PInvoke.IsWindowVisible(h) || PInvoke.IsIconic(h)))
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

    /// <summary>
    /// Some apps clamp resizes to a minimum size (WM_GETMINMAXINFO), silently
    /// ending up bigger than their cell and overlapping neighbors. Detect that
    /// after the async SetWindowPos settles and float the window instead.
    /// </summary>
    private unsafe void VerifyCellFits()
    {
        if (_paused || dryRun)
        {
            return;
        }

        long now = Environment.TickCount64;
        List<(nint Hwnd, int Monitor)>? overflow = null;
        for (int m = 0; m < _monitors.Count; m++)
        {
            foreach (ManagedWindow w in _monitors[m].ActiveWs.Windows)
            {
                if (!w.PendingVerify || w.NoAutoFloat || now - w.RequestedAtMs < 200 || w.Hwnd == _dragHwnd)
                {
                    continue;
                }

                RECT r = default;
                if (!PInvoke.GetWindowRect(new HWND(w.Hwnd), &r))
                {
                    w.PendingVerify = false;
                    continue;
                }

                bool oversize = r.right - r.left > w.RequestedW + FitTolerance
                             || r.bottom - r.top > w.RequestedH + FitTolerance;
                if (!oversize)
                {
                    w.PendingVerify = false;
                    continue;
                }

                // Only a window that MOVED to its cell but refused the size has
                // provably clamped (WM_GETMINMAXINFO doesn't touch position).
                // An untouched rect just means SWP_ASYNCWINDOWPOS hasn't landed
                // yet — retry until a deadline rather than float a healthy window.
                bool positioned = Math.Abs(r.left - w.RequestedX) <= FitTolerance
                               && Math.Abs(r.top - w.RequestedY) <= FitTolerance;
                if (positioned)
                {
                    w.PendingVerify = false;
                    (overflow ??= []).Add((w.Hwnd, m));
                }
                else if (now - w.RequestedAtMs > 2000)
                {
                    w.PendingVerify = false;
                }
            }
        }

        if (overflow is null)
        {
            return;
        }

        foreach ((nint hwnd, int monitor) in overflow)
        {
            FloatWindow(hwnd, monitor, "minimum size exceeds its cell");
        }
    }

    private void FloatWindow(nint hwnd, int monitor, string reason)
    {
        Workspace ws = _monitors[monitor].ActiveWs;
        int idx = ws.Windows.FindIndex(w => w.Hwnd == hwnd);
        if (idx < 0)
        {
            return;
        }

        ManagedWindow w = ws.Windows[idx];
        ws.Windows.RemoveAt(idx);
        if (idx < ws.FocusedIndex)
        {
            ws.FocusedIndex--;
        }
        else if (ws.FocusedIndex >= ws.Windows.Count)
        {
            ws.FocusedIndex = Math.Max(0, ws.Windows.Count - 1);
        }

        w.PendingVerify = false;
        w.NoAutoFloat = false; // floating again clears any "keep tiled" pin
        ws.Floating.Add(w);
        Log($"float 0x{hwnd:X8} {w.Exe} — {reason}");
        Retile(monitor);
    }

    private bool IsFloating((int M, int W) loc, nint hwnd)
        => _monitors[loc.M].Workspaces[loc.W].Floating.Exists(w => w.Hwnd == hwnd);

    private static unsafe nint ForegroundHwnd() => (nint)PInvoke.GetForegroundWindow().Value;

    private static unsafe bool IsWindowAlive(nint hwnd) => PInvoke.IsWindow(new HWND(hwnd));

    private void CloakWin(nint hwnd)
    {
        if (dryRun)
        {
            Log($"DRYRUN cloak 0x{hwnd:X8}");
            return;
        }

        _selfCloaked.Add(hwnd);
        CloakPersistence.Save(_selfCloaked);
        if (!CloakControl.SetCloak(hwnd, true))
        {
            Log($"cloak failed for 0x{hwnd:X8}");
        }
    }

    private void UncloakWin(nint hwnd)
    {
        if (dryRun)
        {
            Log($"DRYRUN uncloak 0x{hwnd:X8}");
            return;
        }

        if (_selfCloaked.Remove(hwnd))
        {
            CloakPersistence.Save(_selfCloaked);
            CloakControl.SetCloak(hwnd, false);
        }
    }

    private void UncloakAll()
    {
        if (_selfCloaked.Count == 0)
        {
            return;
        }

        foreach (nint hwnd in _selfCloaked)
        {
            CloakControl.SetCloak(hwnd, false);
        }
        _selfCloaked.Clear();
        CloakPersistence.Save(_selfCloaked);
        Log("restored all hidden windows");
    }

    /// <summary>Uncloaks the target workspace, retiles, then cloaks the old one.</summary>
    private void SwitchWorkspace(int monitor, int index, nint focusTarget = 0)
    {
        MonitorCtx mc = _monitors[monitor];
        if (index == mc.Active)
        {
            return;
        }

        Workspace from = mc.ActiveWs;
        Workspace to = mc.Workspaces[index];
        mc.Active = index;

        // Show the incoming workspace first — less blank-screen time.
        foreach (ManagedWindow w in to.Windows)
        {
            UncloakWin(w.Hwnd);
        }
        foreach (ManagedWindow w in to.Floating)
        {
            UncloakWin(w.Hwnd);
        }
        Retile(monitor);

        foreach (ManagedWindow w in from.Windows)
        {
            CloakWin(w.Hwnd);
        }
        foreach (ManagedWindow w in from.Floating)
        {
            CloakWin(w.Hwnd);
        }

        _focusedMonitor = monitor;
        nint target = focusTarget != 0
            ? focusTarget
            : to.Focused?.Hwnd ?? (to.Floating.Count > 0 ? to.Floating[^1].Hwnd : 0);
        if (target != 0)
        {
            if (dryRun)
            {
                Log($"DRYRUN focus 0x{target:X8}");
            }
            else
            {
                FocusControl.Focus(target);
            }
        }
        else
        {
            ClearBorder();
        }

        Log($"monitor {monitor} -> workspace {index + 1}");
    }

    private CommandReply HandleCommand(CommandRequest req)
    {
        switch (req.Cmd)
        {
            case "version":
                return new CommandReply(true, Message: version);

            case "stop":
                _stopRequested = true;
                return new CommandReply(true, Message: "stopping");

            case "state":
                return new CommandReply(true, State: BuildState());

            case "pause":
                _paused = true;
                UncloakAll();
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
                Log($"resumed, managing {_windowLoc.Count} windows");
                return new CommandReply(true, Message: $"resumed ({_windowLoc.Count} windows)");

            case "retile":
                if (_paused)
                {
                    return new CommandReply(false, "paused — resume first");
                }
                RetileAll();
                return new CommandReply(true, Message: "retiled");

            case "workspace":
            case "send":
            {
                if (_paused)
                {
                    return new CommandReply(false, "paused — resume first");
                }
                if (!int.TryParse(req.Arg, out int number) || number < 1 || number > WorkspaceCount)
                {
                    return new CommandReply(false, $"workspace must be 1..{WorkspaceCount}");
                }

                int index = number - 1;
                MonitorCtx mc = _monitors[_focusedMonitor];
                if (index == mc.Active)
                {
                    return new CommandReply(true, Message: $"already on workspace {number}");
                }

                if (req.Cmd == "workspace")
                {
                    SwitchWorkspace(_focusedMonitor, index);
                    return new CommandReply(true, Message: $"workspace {number}");
                }

                return SendToWorkspace(mc, index, number);
            }

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
                _monitors[_focusedMonitor].ActiveWs.Layout = kind.Value;
                if (!_paused)
                {
                    Retile(_focusedMonitor);
                }
                return new CommandReply(true, Message: $"layout {req.Arg} on monitor {_focusedMonitor}");
            }

            case "float":
            {
                if (_paused)
                {
                    return new CommandReply(false, "paused — resume first");
                }

                MonitorCtx mc = _monitors[_focusedMonitor];
                nint foreground = ForegroundHwnd();
                nint target = foreground != 0
                    && _windowLoc.TryGetValue(foreground, out (int M, int W) fgLoc)
                    && fgLoc.M == _focusedMonitor && fgLoc.W == mc.Active
                        ? foreground
                        : mc.ActiveWs.Focused?.Hwnd ?? 0;
                if (target == 0)
                {
                    return new CommandReply(false, "no managed window to float");
                }

                Workspace ws = mc.ActiveWs;
                int floatIdx = ws.Floating.FindIndex(w => w.Hwnd == target);
                if (floatIdx >= 0)
                {
                    ManagedWindow w = ws.Floating[floatIdx];
                    ws.Floating.RemoveAt(floatIdx);
                    // The user insists on tiling — don't auto-float it again
                    // even if it overflows its cell.
                    w.NoAutoFloat = true;
                    int insertAt = Math.Min(ws.FocusedIndex + 1, ws.Windows.Count);
                    ws.Windows.Insert(insertAt, w);
                    Retile(_focusedMonitor);
                    return new CommandReply(true, Message: $"tiled 0x{target:X8}");
                }

                FloatWindow(target, _focusedMonitor, "user request");
                return new CommandReply(true, Message: $"floating 0x{target:X8}");
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

                Workspace ws = _monitors[_focusedMonitor].ActiveWs;
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

    private CommandReply SendToWorkspace(MonitorCtx mc, int index, int number)
    {
        Workspace ws = mc.ActiveWs;
        nint foreground = ForegroundHwnd();
        nint target = foreground != 0
            && _windowLoc.TryGetValue(foreground, out (int M, int W) fgLoc)
            && fgLoc.M == _focusedMonitor && fgLoc.W == mc.Active
                ? foreground
                : ws.Focused?.Hwnd ?? 0;
        if (target == 0)
        {
            return new CommandReply(false, "no managed window to send");
        }

        ManagedWindow? window = null;
        int idx = ws.Windows.FindIndex(w => w.Hwnd == target);
        if (idx >= 0)
        {
            window = ws.Windows[idx];
            ws.Windows.RemoveAt(idx);
            if (idx < ws.FocusedIndex)
            {
                ws.FocusedIndex--;
            }
            else if (ws.FocusedIndex >= ws.Windows.Count)
            {
                ws.FocusedIndex = Math.Max(0, ws.Windows.Count - 1);
            }
        }
        else
        {
            int floatIdx = ws.Floating.FindIndex(w => w.Hwnd == target);
            if (floatIdx >= 0)
            {
                window = ws.Floating[floatIdx];
                ws.Floating.RemoveAt(floatIdx);
            }
        }

        if (window is null)
        {
            return new CommandReply(false, "no managed window to send");
        }

        window.PendingVerify = false;
        mc.Workspaces[index].Windows.Add(window);
        _windowLoc[target] = (_focusedMonitor, index);
        if (_borderHwnd == target)
        {
            if (!dryRun)
            {
                FocusControl.SetBorder(target, null);
            }
            _borderHwnd = 0;
        }
        CloakWin(target);
        Retile(_focusedMonitor);

        nint next = ws.Focused?.Hwnd ?? 0;
        if (next != 0 && !dryRun)
        {
            FocusControl.Focus(next);
        }

        Log($"send 0x{target:X8} -> workspace {number}");
        return new CommandReply(true, Message: $"sent 0x{target:X8} to workspace {number}");
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
        foreach (MonitorCtx mc in _monitors)
        {
            var workspaces = new List<WorkspaceDto>(mc.Workspaces.Count);
            foreach (Workspace ws in mc.Workspaces)
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

                var floating = new List<WindowDto>(ws.Floating.Count);
                foreach (ManagedWindow w in ws.Floating)
                {
                    // Floating windows keep their own geometry — report the real one.
                    var snapshot = WindowSnapshot.Capture(w.Hwnd);
                    floating.Add(new WindowDto(
                        w.Hwnd,
                        w.Pid,
                        w.Exe,
                        snapshot.IsAlive ? snapshot.Title : "(gone)",
                        new RectDto(snapshot.X, snapshot.Y, snapshot.Width, snapshot.Height)));
                }

                workspaces.Add(new WorkspaceDto(
                    ws.Layout.ToString().ToLowerInvariant(), ws.FocusedIndex, windows, floating));
            }

            monitors.Add(new MonitorDto(
                mc.Desc.Device,
                mc.Desc.Primary,
                new RectDto(mc.Desc.WorkArea.X, mc.Desc.WorkArea.Y, mc.Desc.WorkArea.W, mc.Desc.WorkArea.H),
                mc.Active,
                workspaces));
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
