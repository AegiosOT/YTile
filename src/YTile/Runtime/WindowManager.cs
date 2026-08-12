using System.Text.Json;
using System.Threading.Channels;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using YTile.Config;
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

    /// <summary>Non-zero: this tiled window fills the work area, others are hidden.</summary>
    public nint MonocleHwnd { get; set; }

    // User resize adjustments, consumed by Layouts.Compute: one first-half
    // ratio per BSP split (default 0.5), one width weight per column
    // (default 1.0). Keyed by split/column index, so window churn shifts
    // which visual boundary an entry controls — 'ytile retile' resets them.
    public List<double> BspRatios { get; } = [];
    public List<double> ColumnWeights { get; } = [];

    public IReadOnlyList<double> SizingFor(LayoutKind kind)
        => kind == LayoutKind.Columns ? ColumnWeights : BspRatios;

    public void ClearSizing()
    {
        BspRatios.Clear();
        ColumnWeights.Clear();
    }

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

    public MonitorDesc Desc { get; set; }
    public List<Workspace> Workspaces { get; } = [];
    public int Active { get; set; }
    public Workspace ActiveWs => Workspaces[Active];

    /// <summary>Reserved strip per edge (bars) — subtracted from the work area.</summary>
    public (int L, int T, int R, int B) Reserved { get; set; }

    public RectI EffectiveWorkArea => new(
        Desc.WorkArea.X + Reserved.L,
        Desc.WorkArea.Y + Reserved.T,
        Math.Max(0, Desc.WorkArea.W - Reserved.L - Reserved.R),
        Math.Max(0, Desc.WorkArea.H - Reserved.T - Reserved.B));
}

/// <summary>
/// The single-actor window manager: owns all state, consumes the unified
/// message queue. Nothing outside this class mutates monitors or workspaces.
/// Windows on inactive workspaces are cloaked (see CloakControl); the
/// _selfCloaked set suppresses the Cloak/Hide events our own hiding produces
/// so they are not mistaken for windows going away.
/// </summary>
internal sealed class WindowManager(string version, bool dryRun, bool startPaused, YTileConfig config, EventHub events)
{
    public const int WorkspaceCount = 9;

    // A clamped resize larger than this margin means the window refused the cell.
    private const int FitTolerance = 8;

    private YTileConfig _config = config;

    private readonly List<MonitorCtx> _monitors = [];
    private readonly Dictionary<nint, (int M, int W)> _windowLoc = [];
    private readonly HashSet<nint> _selfCloaked = [];
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private bool _paused = startPaused;
    private bool _stopRequested;
    private int _focusedMonitor;
    private nint _borderHwnd;
    private nint _dragHwnd;

    // Real on-screen rect when the drag began — the baseline that classifies
    // move vs resize and sizes resize deltas. Requested* is only the rect we
    // asked for; windows that clamp (WM_GETMINMAXINFO) never occupy it.
    private RectI _dragStartRect;
    private bool _dragStartValid;

    // Monitor reconciliation timings (all load-bearing against real OS behavior):
    // 500ms trailing debounce on change bursts, 3s grace before trusting a
    // monitor removal (docks report spurious removals on resume), 10s after
    // resume during which minimize events are ignored (the OS minimizes and
    // shuffles windows while re-initializing displays).
    private long _displayChangePendingAtMs;
    private long _removalGraceStartMs;
    private long _resumeGraceUntilMs;

    // Deferred post-uncloak pokes for Chromium windows: the immediate nudge
    // can race DWM's cloak-state propagation, so we poke again shortly after.
    private readonly List<(nint Hwnd, long DueMs)> _pendingNudges = [];

    // After we cloak windows, the OS may hand foreground back to one of them
    // (e.g. when a keybind's transient console closes). Within this window,
    // foreground on a self-cloaked window is churn — not the user asking to
    // follow it to its hidden workspace.
    private long _cloakChurnUntilMs;

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
                            CheckDisplayChange();
                            ProcessPendingNudges();
                            break;
                        case WmMessage.DisplayChange:
                            _displayChangePendingAtMs = Environment.TickCount64;
                            break;
                        case WmMessage.PowerResume:
                            _resumeGraceUntilMs = Environment.TickCount64 + 10_000;
                            _displayChangePendingAtMs = Environment.TickCount64;
                            Log("resume from sleep — minimize suppression for 10s");
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
        List<nint>? unrecovered = null;
        foreach (nint leftover in CloakPersistence.LoadAndDelete())
        {
            if (!IsWindowAlive(leftover))
            {
                continue;
            }
            if (CloakControl.SetCloak(leftover, false))
            {
                Log($"restored 0x{leftover:X8} cloaked by a previous instance");
            }
            else
            {
                (unrecovered ??= []).Add(leftover);
                Log($"FAILED to restore 0x{leftover:X8} — kept in the recovery file");
            }
        }
        if (unrecovered is not null)
        {
            CloakPersistence.Save(unrecovered);
        }

        foreach (MonitorDesc desc in MonitorProbe.Enumerate())
        {
            var mc = new MonitorCtx(desc);
            foreach (Workspace ws in mc.Workspaces)
            {
                ws.Layout = _config.DefaultLayout;
            }
            _monitors.Add(mc);
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
        PublishEvent("ready");
    }

    /// <summary>Pushes {event, state} to IPC subscribers (bars, scripts).</summary>
    private void PublishEvent(string name)
    {
        if (!events.HasSubscribers)
        {
            return;
        }

        var notification = new NotificationDto(name, BuildState());
        events.Publish(JsonSerializer.Serialize(notification, ProtocolJsonContext.Default.NotificationDto));
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
            var reservations = new Dictionary<string, (int L, int T, int R, int B)>();
            var sizings = new Dictionary<string, (double[] Bsp, double[] Cols)[]>();
            foreach (MonitorCtx mc in _monitors)
            {
                layouts[mc.Desc.Device] = [.. mc.Workspaces.Select(w => w.Layout)];
                reservations[mc.Desc.Device] = mc.Reserved;
                sizings[mc.Desc.Device] = [.. mc.Workspaces.Select(w => (w.BspRatios.ToArray(), w.ColumnWeights.ToArray()))];
            }

            _monitors.Clear();
            foreach (MonitorDesc desc in fresh)
            {
                var mc = new MonitorCtx(desc);
                foreach (Workspace ws in mc.Workspaces)
                {
                    ws.Layout = _config.DefaultLayout;
                }
                if (layouts.TryGetValue(desc.Device, out List<LayoutKind>? kinds))
                {
                    for (int i = 0; i < WorkspaceCount; i++)
                    {
                        mc.Workspaces[i].Layout = kinds[i];
                    }
                }
                if (sizings.TryGetValue(desc.Device, out (double[] Bsp, double[] Cols)[]? sizing))
                {
                    for (int i = 0; i < WorkspaceCount; i++)
                    {
                        mc.Workspaces[i].BspRatios.AddRange(sizing[i].Bsp);
                        mc.Workspaces[i].ColumnWeights.AddRange(sizing[i].Cols);
                    }
                }
                if (reservations.TryGetValue(desc.Device, out (int L, int T, int R, int B) reserved))
                {
                    mc.Reserved = reserved;
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
                RemoveWindow(e.Hwnd);
                break;

            case WmEventKind.Minimize:
                // The OS spuriously minimizes windows while re-initializing
                // displays after resume — don't unmanage during the grace.
                if (Environment.TickCount64 >= _resumeGraceUntilMs)
                {
                    RemoveWindow(e.Hwnd);
                }
                break;

            case WmEventKind.FocusChange:
                HandleFocusChange(e.Hwnd);
                break;

            case WmEventKind.MoveResizeStart:
                // Don't yank a window out from under an active drag.
                if (_windowLoc.ContainsKey(e.Hwnd))
                {
                    _dragHwnd = e.Hwnd;
                    _dragStartValid = TryGetRect(e.Hwnd, out _dragStartRect);
                }
                break;

            case WmEventKind.MoveResizeEnd:
            {
                bool haveStart = _dragStartValid && _dragHwnd == e.Hwnd;
                RectI startRect = _dragStartRect;
                _dragHwnd = 0;
                _dragStartValid = false;
                if (!_windowLoc.TryGetValue(e.Hwnd, out (int M, int W) loc)
                    || loc.W != _monitors[loc.M].Active
                    || IsFloating(loc, e.Hwnd))
                {
                    break; // floating windows move freely
                }

                Workspace ws = _monitors[loc.M].ActiveWs;
                int dragged = ws.Windows.FindIndex(w => w.Hwnd == e.Hwnd);
                if (dragged < 0 || ws.MonocleHwnd != 0 || !TryGetRect(e.Hwnd, out RectI dropped))
                {
                    Retile(loc.M);
                    break;
                }

                // The pre-drag baseline: the real observed rect when the drag
                // began, or the last requested rect if START was lost (the raw
                // event channel drops oldest under pressure).
                ManagedWindow win = ws.Windows[dragged];
                RectI before = haveStart
                    ? startRect
                    : new RectI(win.RequestedX, win.RequestedY, win.RequestedW, win.RequestedH);

                bool sizeUnchanged = Math.Abs(dropped.W - before.W) <= FitTolerance
                                  && Math.Abs(dropped.H - before.H) <= FitTolerance;
                if (sizeUnchanged)
                {
                    // A drag: swap with the window whose cell the cursor
                    // dropped on. Dropped anywhere else, it snaps back.
                    if (TryGetCursor(out int cx, out int cy))
                    {
                        int target = ws.Windows.FindIndex(w => w.Hwnd != e.Hwnd
                            && cx >= w.LastRect.X && cx < w.LastRect.Right
                            && cy >= w.LastRect.Y && cy < w.LastRect.Bottom);
                        if (target >= 0)
                        {
                            (ws.Windows[dragged], ws.Windows[target]) = (ws.Windows[target], ws.Windows[dragged]);
                            ws.FocusedIndex = target;
                            Log($"drag-swap 0x{e.Hwnd:X8} -> slot {target}");
                            Retile(loc.M);
                            PublishEvent("move");
                            break;
                        }
                    }
                }
                else if (LayoutResizer.ApplyEdgeDeltas(
                    ws.Layout, _monitors[loc.M].EffectiveWorkArea, ws.Windows.Count, _config.Gap,
                    ws.BspRatios, ws.ColumnWeights, dragged,
                    dropped.X - before.X, dropped.Y - before.Y,
                    dropped.Right - before.Right, dropped.Bottom - before.Bottom,
                    minDelta: FitTolerance))
                {
                    // A resize: the edge deltas are folded into the layout so
                    // the retile keeps the user's size instead of snapping back.
                    Log($"resize 0x{e.Hwnd:X8} folded into layout");
                    Retile(loc.M);
                    PublishEvent("resize");
                    break;
                }

                Retile(loc.M);
                break;
            }
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
            // Foreground bouncing back to a window we just cloaked is OS churn
            // (transient consoles from keybinds, etc.) — never follow it.
            if (_selfCloaked.Contains(hwnd) && Environment.TickCount64 < _cloakChurnUntilMs)
            {
                return;
            }

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
            PublishEvent("focus_change");
        }
        else if (IsFloating(loc, hwnd))
        {
            _focusedMonitor = loc.M;
            SetBorder(hwnd);
            PublishEvent("focus_change");
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

        RuleAction? rule = _config.RuleFor(in snapshot);
        if (rule == RuleAction.Ignore)
        {
            return false;
        }
        if (rule == RuleAction.Float)
        {
            int floatMonitor = MonitorIndexFor(hwnd);
            MonitorCtx floatMc = _monitors[floatMonitor];
            floatMc.ActiveWs.Floating.Add(new ManagedWindow(hwnd, snapshot.ProcessId, snapshot.ExeName));
            _windowLoc[hwnd] = (floatMonitor, floatMc.Active);
            if (!adopting)
            {
                Log($"manage 0x{hwnd:X8} {snapshot.ExeName} \"{snapshot.Title}\" -> floating (rule)");
                PublishEvent("manage");
            }
            return true;
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
        // A new window on a monocled workspace ends the monocle — predictable
        // over invisible-new-window surprises.
        ExitMonocle(ws);
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
        if (!adopting)
        {
            PublishEvent("manage");
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
            _dragStartValid = false;
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

        if (ws.MonocleHwnd == hwnd)
        {
            ws.MonocleHwnd = 0;
            if (loc.W == mc.Active)
            {
                foreach (ManagedWindow w in ws.Windows)
                {
                    if (_selfCloaked.Contains(w.Hwnd))
                    {
                        UncloakWin(w.Hwnd);
                    }
                }
            }
        }

        Log($"unmanage 0x{hwnd:X8}{(loc.W == mc.Active ? "" : $" (ws {loc.W + 1})")}");
        // A floating window occupies no cell; hidden workspaces retile on switch.
        if (wasTiled && loc.W == mc.Active)
        {
            Retile(loc.M);
        }
        PublishEvent("unmanage");
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

        if (ws.MonocleHwnd != 0)
        {
            ManagedWindow? monocle = ws.Windows.Find(w => w.Hwnd == ws.MonocleHwnd);
            if (monocle is not null)
            {
                RectI cell = mc.EffectiveWorkArea.Shrink(_config.Gap);
                monocle.LastRect = cell;
                RectI monoAdjusted = WindowPositioner.Apply(monocle.Hwnd, cell, dryRun);
                if (!dryRun)
                {
                    monocle.RequestedX = monoAdjusted.X;
                    monocle.RequestedY = monoAdjusted.Y;
                    monocle.RequestedW = monoAdjusted.W;
                    monocle.RequestedH = monoAdjusted.H;
                    monocle.RequestedAtMs = Environment.TickCount64;
                    monocle.PendingVerify = true;
                }
                return;
            }
            ws.MonocleHwnd = 0; // window went away — fall through to normal tiling
        }

        RectI[] cells = Layouts.Compute(ws.Layout, mc.EffectiveWorkArea, ws.Windows.Count, _config.Gap, ws.SizingFor(ws.Layout));
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
                && Environment.TickCount64 >= _resumeGraceUntilMs
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
        PublishEvent("float_change");
    }

    private bool IsFloating((int M, int W) loc, nint hwnd)
        => _monitors[loc.M].Workspaces[loc.W].Floating.Exists(w => w.Hwnd == hwnd);

    private void ProcessPendingNudges()
    {
        if (_pendingNudges.Count == 0)
        {
            return;
        }

        long now = Environment.TickCount64;
        for (int i = _pendingNudges.Count - 1; i >= 0; i--)
        {
            (nint hwnd, long dueMs) = _pendingNudges[i];
            if (now < dueMs)
            {
                continue;
            }
            _pendingNudges.RemoveAt(i);
            if (IsWindowAlive(hwnd) && !_selfCloaked.Contains(hwnd))
            {
                WindowPositioner.Nudge(hwnd);
            }
        }
    }

    /// <summary>Runs on reaper ticks: applies a debounced display change.</summary>
    private void CheckDisplayChange()
    {
        long now = Environment.TickCount64;
        if (_displayChangePendingAtMs == 0 || now - _displayChangePendingAtMs < 500)
        {
            return;
        }

        List<MonitorDesc> fresh = MonitorProbe.Enumerate();
        if (fresh.Count == 0)
        {
            return; // transient (display re-init) — retry next tick
        }

        var freshDevices = new HashSet<string>(fresh.Select(d => d.Device));
        bool removalDetected = _monitors.Exists(mc => !freshDevices.Contains(mc.Desc.Device));
        if (removalDetected)
        {
            // Docks report spurious removals on resume — wait out the grace
            // period; if the monitor reappears, this resolves to a no-op.
            if (_removalGraceStartMs == 0)
            {
                _removalGraceStartMs = now;
                Log("monitor removal detected — confirming for 3s");
                return;
            }
            if (now - _removalGraceStartMs < 3000)
            {
                return;
            }
        }

        _displayChangePendingAtMs = 0;
        _removalGraceStartMs = 0;
        ApplyMonitorChanges(fresh);
    }

    /// <summary>
    /// Rebuilds the monitor list preserving every workspace (and its windows)
    /// by device name; windows from vanished monitors move to the same-index
    /// workspace of the first remaining monitor.
    /// </summary>
    private void ApplyMonitorChanges(List<MonitorDesc> fresh)
    {
        var byDevice = _monitors.ToDictionary(mc => mc.Desc.Device);
        var rebuilt = new List<MonitorCtx>(fresh.Count);
        foreach (MonitorDesc desc in fresh)
        {
            if (byDevice.Remove(desc.Device, out MonitorCtx? existing))
            {
                existing.Desc = desc; // refreshed handle + geometry + work area
                rebuilt.Add(existing);
            }
            else
            {
                var mc = new MonitorCtx(desc);
                foreach (Workspace ws in mc.Workspaces)
                {
                    ws.Layout = _config.DefaultLayout;
                }
                rebuilt.Add(mc);
                Log($"monitor added: {desc.Device} work={desc.WorkArea}");
            }
        }

        // Whatever is left in byDevice vanished — rehome its windows.
        foreach (MonitorCtx orphan in byDevice.Values)
        {
            Log($"monitor removed: {orphan.Desc.Device} — rehoming its windows");
            MonitorCtx target = rebuilt[0];
            for (int i = 0; i < WorkspaceCount; i++)
            {
                target.Workspaces[i].Windows.AddRange(orphan.Workspaces[i].Windows);
                target.Workspaces[i].Floating.AddRange(orphan.Workspaces[i].Floating);
            }
        }

        _monitors.Clear();
        _monitors.AddRange(rebuilt);
        _focusedMonitor = Math.Clamp(_focusedMonitor, 0, _monitors.Count - 1);

        // Monitor indices changed — rebuild the location map from the lists.
        _windowLoc.Clear();
        for (int m = 0; m < _monitors.Count; m++)
        {
            for (int w = 0; w < WorkspaceCount; w++)
            {
                foreach (ManagedWindow win in _monitors[m].Workspaces[w].Windows)
                {
                    _windowLoc[win.Hwnd] = (m, w);
                }
                foreach (ManagedWindow win in _monitors[m].Workspaces[w].Floating)
                {
                    _windowLoc[win.Hwnd] = (m, w);
                }
            }
        }

        Log($"monitors reconciled: {_monitors.Count} active, {_windowLoc.Count} windows");
        if (!_paused)
        {
            // Position first, uncloak after (the Chromium rule), then hide
            // anything that landed on an inactive workspace.
            RetileAll();
            NormalizeCloaks();
        }
        PublishEvent("monitors_change");
    }

    /// <summary>Restores the invariant: hidden iff on an inactive workspace.</summary>
    private void NormalizeCloaks()
    {
        for (int m = 0; m < _monitors.Count; m++)
        {
            MonitorCtx mc = _monitors[m];
            for (int w = 0; w < WorkspaceCount; w++)
            {
                bool shouldHide = w != mc.Active;
                foreach (ManagedWindow win in mc.Workspaces[w].Windows)
                {
                    NormalizeCloak(win.Hwnd, shouldHide);
                }
                foreach (ManagedWindow win in mc.Workspaces[w].Floating)
                {
                    NormalizeCloak(win.Hwnd, shouldHide);
                }
            }
        }
    }

    private void NormalizeCloak(nint hwnd, bool shouldHide)
    {
        bool hidden = _selfCloaked.Contains(hwnd);
        if (shouldHide && !hidden)
        {
            CloakWin(hwnd);
        }
        else if (!shouldHide && hidden)
        {
            UncloakWin(hwnd);
        }
    }

    private static unsafe nint ForegroundHwnd() => (nint)PInvoke.GetForegroundWindow().Value;

    private static unsafe bool IsWindowAlive(nint hwnd) => PInvoke.IsWindow(new HWND(hwnd));

    private static unsafe bool TryGetRect(nint hwnd, out RectI rect)
    {
        RECT r = default;
        if (!PInvoke.GetWindowRect(new HWND(hwnd), &r))
        {
            rect = default;
            return false;
        }
        rect = RectI.From(r);
        return true;
    }

    private static unsafe bool TryGetCursor(out int x, out int y)
    {
        System.Drawing.Point point = default;
        if (!PInvoke.GetCursorPos(&point))
        {
            x = y = 0;
            return false;
        }
        x = point.X;
        y = point.Y;
        return true;
    }

    /// <summary>Ends a monocle, restoring the windows it hid (active ws only).</summary>
    private void ExitMonocle(Workspace ws)
    {
        if (ws.MonocleHwnd == 0)
        {
            return;
        }

        ws.MonocleHwnd = 0;
        foreach (ManagedWindow w in ws.Windows)
        {
            if (_selfCloaked.Contains(w.Hwnd))
            {
                UncloakWin(w.Hwnd);
            }
        }
    }

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
            // Chromium's occlusion tracker can miss the uncloak and leave the
            // renderer suspended (black surface) — force a recompute now and
            // again after DWM's cloak state has settled.
            if (WindowSnapshot.ClassNameOf(hwnd).StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal))
            {
                WindowPositioner.Nudge(hwnd);
                _pendingNudges.Add((hwnd, Environment.TickCount64 + 300));
            }
        }
    }

    private void UncloakAll()
    {
        if (_selfCloaked.Count == 0)
        {
            return;
        }

        List<nint>? failed = null;
        foreach (nint hwnd in _selfCloaked)
        {
            if (!IsWindowAlive(hwnd))
            {
                continue;
            }
            // One retry — the cross-process shell call can fail transiently.
            if (!CloakControl.SetCloak(hwnd, false) && !CloakControl.SetCloak(hwnd, false))
            {
                (failed ??= []).Add(hwnd);
                Log($"FAILED to restore 0x{hwnd:X8} — kept in the recovery file");
            }
        }

        _selfCloaked.Clear();
        foreach (nint hwnd in failed ?? [])
        {
            _selfCloaked.Add(hwnd);
        }
        // Failed windows stay on disk so the next start can rescue them.
        CloakPersistence.Save(_selfCloaked);
        Log(failed is null ? "restored all hidden windows" : $"restored hidden windows, {failed.Count} FAILED");
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

        // komorebi's shipped workspace order: uncloak first, THEN position.
        // The positioning pass (FRAMECHANGED + NOCOPYBITS) is what forces a
        // real repaint after the uncloak — Chromium renderers stay suspended
        // otherwise, showing a stale black surface.
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
        // Everything on the outgoing workspace is cloaked now; coming back
        // resumes normal tiling rather than a stale monocle.
        from.MonocleHwnd = 0;
        _cloakChurnUntilMs = Environment.TickCount64 + 1500;

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
            // Park foreground on the desktop so the OS can't hand it back to
            // a window we just cloaked.
            if (!dryRun)
            {
                FocusControl.FocusDesktop();
            }
        }

        Log($"monitor {monitor} -> workspace {index + 1}");
        PublishEvent("workspace_change");
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

            case "reload":
            {
                _config = YTileConfig.Load(null, out string? configError);
                if (!_paused)
                {
                    Resync();
                    RetileAll();
                }
                Log(configError is null ? "config reloaded" : $"config reloaded with problems: {configError}");
                PublishEvent("reload");
                return configError is null
                    ? new CommandReply(true, Message: $"reloaded ({_windowLoc.Count} windows)")
                    : new CommandReply(false, configError);
            }

            case "state":
                return new CommandReply(true, State: BuildState());

            case "pause":
                _paused = true;
                UncloakAll();
                ClearBorder();
                Log("paused");
                PublishEvent("pause");
                return new CommandReply(true, Message: "paused");

            case "reserve":
            {
                // "monitor left top right bottom" — a bar reserving its strip.
                string[] parts = req.Arg?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
                if (parts.Length != 5
                    || !int.TryParse(parts[0], out int rm)
                    || !int.TryParse(parts[1], out int rl)
                    || !int.TryParse(parts[2], out int rt)
                    || !int.TryParse(parts[3], out int rr)
                    || !int.TryParse(parts[4], out int rb))
                {
                    return new CommandReply(false, "usage: reserve <monitor> <left> <top> <right> <bottom>");
                }
                if (rm < 0 || rm >= _monitors.Count)
                {
                    return new CommandReply(false, $"monitor must be 0..{_monitors.Count - 1}");
                }

                _monitors[rm].Reserved = (rl, rt, rr, rb);
                if (!_paused)
                {
                    Retile(rm);
                }
                Log($"monitor {rm} reserved l={rl} t={rt} r={rr} b={rb}");
                PublishEvent("reserve");
                return new CommandReply(true, Message: $"reserved on monitor {rm}");
            }

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
                PublishEvent("resume");
                return new CommandReply(true, Message: $"resumed ({_windowLoc.Count} windows)");

            case "retile":
                if (_paused)
                {
                    return new CommandReply(false, "paused — resume first");
                }
                // retile is the documented reset for resize adjustments.
                foreach (MonitorCtx m in _monitors)
                {
                    foreach (Workspace w in m.Workspaces)
                    {
                        w.ClearSizing();
                    }
                }
                RetileAll();
                PublishEvent("retile");
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
                PublishEvent("layout_change");
                return new CommandReply(true, Message: $"layout {req.Arg} on monitor {_focusedMonitor}");
            }

            case "monocle":
            {
                if (_paused)
                {
                    return new CommandReply(false, "paused — resume first");
                }

                MonitorCtx mc = _monitors[_focusedMonitor];
                Workspace ws = mc.ActiveWs;
                if (ws.MonocleHwnd != 0)
                {
                    ExitMonocle(ws);
                    Retile(_focusedMonitor);
                    PublishEvent("monocle");
                    return new CommandReply(true, Message: "monocle off");
                }

                nint foreground = ForegroundHwnd();
                nint target = foreground != 0
                    && _windowLoc.TryGetValue(foreground, out (int M, int W) fgLoc)
                    && fgLoc.M == _focusedMonitor && fgLoc.W == mc.Active
                    && ws.Windows.Exists(w => w.Hwnd == foreground)
                        ? foreground
                        : ws.Focused?.Hwnd ?? 0;
                if (target == 0 || !ws.Windows.Exists(w => w.Hwnd == target))
                {
                    return new CommandReply(false, "no tiled window to monocle");
                }

                ws.MonocleHwnd = target;
                Retile(_focusedMonitor);
                foreach (ManagedWindow w in ws.Windows)
                {
                    if (w.Hwnd != target)
                    {
                        CloakWin(w.Hwnd);
                    }
                }
                PublishEvent("monocle");
                return new CommandReply(true, Message: $"monocle 0x{target:X8}");
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
                    PublishEvent("float_change");
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
                    // Nothing further this way on the workspace — cross to the
                    // adjacent monitor if there is one.
                    return CrossMonitor(req, dir.Value, ws);
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
                PublishEvent("move");
                return new CommandReply(true, Message: "moved");
            }

            case "resize":
            {
                if (_paused)
                {
                    return new CommandReply(false, "paused — resume first");
                }

                // "<dir> [px]" — grow the focused window toward <dir> by px
                // (default config resizeStep); negative px shrinks.
                string[] parts = req.Arg?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
                Direction? dir = parts.Length is 1 or 2 ? DirectionParser.Parse(parts[0]) : null;
                int step = _config.ResizeStep;
                if (dir is null || (parts.Length == 2 && !int.TryParse(parts[1], out step)))
                {
                    return new CommandReply(false, "usage: resize <left|right|up|down> [px]");
                }

                MonitorCtx mc = _monitors[_focusedMonitor];
                Workspace ws = mc.ActiveWs;
                if (ws.MonocleHwnd != 0)
                {
                    return new CommandReply(false, "monocled — leave monocle first");
                }
                if (ws.Focused is null)
                {
                    return new CommandReply(false, "no tiled window focused");
                }

                (int dl, int dt, int dr, int db) = dir.Value switch
                {
                    Direction.Left => (-step, 0, 0, 0),
                    Direction.Right => (0, 0, step, 0),
                    Direction.Up => (0, -step, 0, 0),
                    _ => (0, 0, 0, step),
                };
                bool changed = LayoutResizer.ApplyEdgeDeltas(
                    ws.Layout, mc.EffectiveWorkArea, ws.Windows.Count, _config.Gap,
                    ws.BspRatios, ws.ColumnWeights, ws.FocusedIndex, dl, dt, dr, db, minDelta: 1);
                if (!changed)
                {
                    return new CommandReply(false, $"no adjustable edge {parts[0]} of focused");
                }

                Retile(_focusedMonitor);
                PublishEvent("resize");
                return new CommandReply(true, Message: $"resized {parts[0]} {step}px");
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
        _cloakChurnUntilMs = Environment.TickCount64 + 1500;
        Retile(_focusedMonitor);

        nint next = ws.Focused?.Hwnd ?? 0;
        if (next != 0 && !dryRun)
        {
            FocusControl.Focus(next);
        }
        else if (!dryRun)
        {
            FocusControl.FocusDesktop();
        }

        Log($"send 0x{target:X8} -> workspace {number}");
        PublishEvent("workspace_change");
        return new CommandReply(true, Message: $"sent 0x{target:X8} to workspace {number}");
    }

    /// <summary>
    /// Directional focus/move that ran off the monitor's edge: land on the
    /// adjacent monitor in that direction. Focus goes to the edge-most window
    /// of its active workspace (entering from the left lands leftmost, etc.);
    /// move transfers the focused window there, inserted at the entry edge.
    /// </summary>
    private CommandReply CrossMonitor(CommandRequest req, Direction dir, Workspace sourceWs)
    {
        int m2 = MonitorInDirection(_focusedMonitor, dir);
        if (m2 < 0)
        {
            return new CommandReply(false, $"no window or monitor {req.Arg} of focused");
        }

        MonitorCtx mc2 = _monitors[m2];
        Workspace ws2 = mc2.ActiveWs;

        if (req.Cmd == "focus")
        {
            nint hwnd;
            if (ws2.MonocleHwnd != 0)
            {
                // Everything but the monocle window is cloaked over there.
                hwnd = ws2.MonocleHwnd;
                ws2.FocusedIndex = Math.Max(0, ws2.Windows.FindIndex(w => w.Hwnd == hwnd));
            }
            else
            {
                int idx = EdgeWindow(ws2, dir);
                if (idx >= 0)
                {
                    ws2.FocusedIndex = idx;
                }
                hwnd = idx >= 0 ? ws2.Windows[idx].Hwnd
                    : ws2.Floating.Count > 0 ? ws2.Floating[^1].Hwnd : 0;
            }

            _focusedMonitor = m2;
            if (hwnd == 0)
            {
                // An empty monitor is still a valid focus target — park
                // foreground on the desktop so later commands land here.
                ClearBorder();
                if (!dryRun)
                {
                    FocusControl.FocusDesktop();
                }
                PublishEvent("focus_change");
                return new CommandReply(true, Message: $"focused monitor {m2} (empty)");
            }

            if (dryRun)
            {
                Log($"DRYRUN focus 0x{hwnd:X8}");
            }
            else
            {
                FocusControl.Focus(hwnd);
            }
            return new CommandReply(true, Message: $"focus 0x{hwnd:X8} (monitor {m2})");
        }

        // move: transfer the focused window to the adjacent monitor.
        ManagedWindow? win = sourceWs.Focused;
        if (win is null)
        {
            return new CommandReply(false, "no tiled window focused");
        }

        int srcMonitor = _focusedMonitor;
        if (sourceWs.MonocleHwnd == win.Hwnd)
        {
            // The monocle window is leaving — restore its hidden siblings.
            ExitMonocle(sourceWs);
        }
        sourceWs.Windows.RemoveAt(sourceWs.FocusedIndex);
        if (sourceWs.FocusedIndex >= sourceWs.Windows.Count)
        {
            sourceWs.FocusedIndex = Math.Max(0, sourceWs.Windows.Count - 1);
        }

        // A window arriving on a monocled workspace ends the monocle, same as
        // a newly created one.
        ExitMonocle(ws2);
        int insertAt = dir is Direction.Right or Direction.Down ? 0 : ws2.Windows.Count;
        ws2.Windows.Insert(insertAt, win);
        ws2.FocusedIndex = insertAt;
        _windowLoc[win.Hwnd] = (m2, mc2.Active);
        win.PendingVerify = false;
        _focusedMonitor = m2;

        Retile(srcMonitor);
        Retile(m2);
        // Second pass on the target: crossing a DPI boundary changes the
        // window's DWM frame, so the first positioning lands slightly off.
        Retile(m2);
        if (dryRun)
        {
            Log($"DRYRUN focus 0x{win.Hwnd:X8}");
        }
        else
        {
            FocusControl.Focus(win.Hwnd);
        }
        Log($"move 0x{win.Hwnd:X8} -> monitor {m2} ws {mc2.Active + 1}");
        PublishEvent("move");
        return new CommandReply(true, Message: $"moved to monitor {m2}");
    }

    /// <summary>Nearest other monitor whose center lies in the given direction.</summary>
    private int MonitorInDirection(int from, Direction dir)
    {
        RectI a = _monitors[from].Desc.WorkArea;
        int best = -1;
        long bestDist = long.MaxValue;
        for (int i = 0; i < _monitors.Count; i++)
        {
            if (i == from)
            {
                continue;
            }

            RectI b = _monitors[i].Desc.WorkArea;
            int dx = b.CenterX - a.CenterX;
            int dy = b.CenterY - a.CenterY;
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

    /// <summary>
    /// The window nearest the edge you enter through: moving right lands on
    /// the leftmost window, moving up lands on the bottommost, and so on.
    /// </summary>
    private static int EdgeWindow(Workspace ws, Direction entering)
    {
        int best = -1;
        int bestVal = int.MaxValue;
        for (int i = 0; i < ws.Windows.Count; i++)
        {
            RectI r = ws.Windows[i].LastRect;
            int val = entering switch
            {
                Direction.Right => r.CenterX,
                Direction.Left => -r.CenterX,
                Direction.Down => r.CenterY,
                _ => -r.CenterY,
            };
            if (val < bestVal)
            {
                bestVal = val;
                best = i;
            }
        }

        return best;
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
                    ws.Layout.ToString().ToLowerInvariant() + (ws.MonocleHwnd != 0 ? "+monocle" : ""),
                    ws.FocusedIndex, windows, floating));
            }

            RectI area = mc.EffectiveWorkArea;
            monitors.Add(new MonitorDto(
                mc.Desc.Device,
                mc.Desc.Primary,
                new RectDto(area.X, area.Y, area.W, area.H),
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
        FocusControl.SetBorder(hwnd, _config.FocusBorderColor);
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
