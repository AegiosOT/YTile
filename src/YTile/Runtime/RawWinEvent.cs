namespace YTile.Runtime;

/// <summary>
/// What the WinEvent hook callback captures — nothing more. Enrichment (title,
/// class, exe, styles) happens on the consumer side, off the hook thread.
/// </summary>
internal readonly record struct RawWinEvent(uint EventId, nint Hwnd, uint TimeMs);
