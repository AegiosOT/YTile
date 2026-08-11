using Windows.Win32;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.System.Com;
using Windows.Win32.UI.WindowsAndMessaging;
using Windows.Win32.Foundation;

namespace YTile.Win32;

/// <summary>
/// Hides windows the way Windows' own virtual desktops do: DWM cloaking via
/// the undocumented ImmersiveShell <c>IApplicationView::SetCloak</c>. Cloaked
/// windows keep WS_VISIBLE (no taskbar churn, no Show/Hide event storms) and
/// are excluded from alt-tab. Falls back to ShowWindow if the interface is
/// unavailable on this Windows build.
///
/// All COM access is raw vtable calls — no marshaling, AOT-clean. The
/// interface pointers live in the MTA, so any MTA thread may call them; every
/// entry point re-asserts CoInitializeEx(MTA), which is a cheap no-op when the
/// thread is already initialized.
/// </summary>
internal static unsafe class CloakControl
{
    private static Guid s_clsidImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static Guid s_iidIServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    private static Guid s_sidApplicationViewCollection = new("1841C6D7-4F9D-42C0-AF41-8747538F10E5");

    // IApplicationViewCollection* — held for the daemon's lifetime.
    private static void* s_collection;
    private static bool s_initTried;

    public static bool Available { get; private set; }

    public static void Init()
    {
        if (s_initTried)
        {
            return;
        }
        s_initTried = true;

        PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);

        void* provider = null;
        HRESULT hr;
        fixed (Guid* clsid = &s_clsidImmersiveShell)
        fixed (Guid* iid = &s_iidIServiceProvider)
        {
            hr = PInvoke.CoCreateInstance(clsid, null, CLSCTX.CLSCTX_LOCAL_SERVER, iid, &provider);
        }
        if (hr.Failed || provider is null)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} cloak: ImmersiveShell unavailable (0x{hr.Value:X8}) — using ShowWindow fallback");
            return;
        }

        // IServiceProvider::QueryService is vtable slot 3.
        void* collection = null;
        fixed (Guid* sid = &s_sidApplicationViewCollection)
        fixed (Guid* iid = &s_sidApplicationViewCollection)
        {
            hr = new HRESULT(((delegate* unmanaged[Stdcall]<void*, Guid*, Guid*, void**, int>)(*(void***)provider)[3])(
                provider, sid, iid, &collection));
        }
        Release(provider);

        if (hr.Failed || collection is null)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} cloak: IApplicationViewCollection unavailable (0x{hr.Value:X8}) — using ShowWindow fallback");
            return;
        }

        s_collection = collection;
        Available = true;
    }

    /// <summary>
    /// Returns false when the window did not reach the requested state. An
    /// uncloak is VERIFIED against DWMWA_CLOAKED: ShowWindow cannot clear a
    /// shell cloak, so a fallback "success" after a failed COM uncloak would
    /// otherwise strand the window invisible while reporting success.
    /// </summary>
    public static bool SetCloak(nint hwnd, bool hide)
    {
        bool applied = Available && TryCloak(hwnd, hide);
        if (!applied)
        {
            // Fallback (also used if a single view lookup fails): plain hide/show.
            applied = PInvoke.ShowWindow(new HWND(hwnd), hide ? SHOW_WINDOW_CMD.SW_HIDE : SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
        }

        return hide ? applied : applied && IsUncloaked(hwnd);
    }

    private static bool IsUncloaked(nint hwnd)
    {
        uint cloaked = 0;
        PInvoke.DwmGetWindowAttribute(new HWND(hwnd), DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint));
        return cloaked == 0;
    }

    private static bool TryCloak(nint hwnd, bool hide)
    {
        PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);

        // IApplicationViewCollection::GetViewForHwnd is vtable slot 6.
        void* view = null;
        int hr = ((delegate* unmanaged[Stdcall]<void*, nint, void**, int>)(*(void***)s_collection)[6])(
            s_collection, hwnd, &view);
        if (hr < 0 || view is null)
        {
            return false;
        }

        // IApplicationView::SetCloak is vtable slot 12 (IUnknown 0-2,
        // IInspectable 3-5, SetFocus 6, SwitchTo 7, TryInvokeBack 8,
        // GetThumbnailWindow 9, GetMonitor 10, GetVisibility 11).
        // cloakType 1 = user-invisible; flags 2 = cloak, 0 = uncloak.
        hr = ((delegate* unmanaged[Stdcall]<void*, uint, int, int>)(*(void***)view)[12])(
            view, 1, hide ? 2 : 0);
        Release(view);
        return hr >= 0;
    }

    private static void Release(void* unknown)
        => ((delegate* unmanaged[Stdcall]<void*, uint>)(*(void***)unknown)[2])(unknown);
}

/// <summary>
/// Crash insurance: the set of windows we currently keep cloaked, persisted to
/// disk. If the daemon dies without uncloaking, the next start restores every
/// listed window — no window is ever stranded invisible.
/// </summary>
internal static class CloakPersistence
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ytile");
    private static readonly string FilePath = Path.Combine(Dir, "cloaked.txt");

    public static void Save(IReadOnlyCollection<nint> hwnds)
    {
        try
        {
            if (hwnds.Count == 0)
            {
                File.Delete(FilePath);
                return;
            }
            Directory.CreateDirectory(Dir);
            File.WriteAllLines(FilePath, hwnds.Select(h => h.ToString()));
        }
        catch (IOException)
        {
            // Persistence is best-effort; never take the daemon down over it.
        }
    }

    public static List<nint> LoadAndDelete()
    {
        var result = new List<nint>();
        try
        {
            if (File.Exists(FilePath))
            {
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    if (nint.TryParse(line, out nint hwnd))
                    {
                        result.Add(hwnd);
                    }
                }
                File.Delete(FilePath);
            }
        }
        catch (IOException)
        {
        }
        return result;
    }
}
