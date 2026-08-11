using Windows.Win32.Foundation;

namespace YTile.Core;

/// <summary>Integer rectangle in physical pixels, X/Y/W/H form.</summary>
internal readonly record struct RectI(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public int CenterX => X + W / 2;
    public int CenterY => Y + H / 2;

    public static RectI From(RECT r) => new(r.left, r.top, r.right - r.left, r.bottom - r.top);

    /// <summary>Shrinks the rect by <paramref name="margin"/> on all four sides.</summary>
    public RectI Shrink(int margin) => new(X + margin, Y + margin, Math.Max(0, W - 2 * margin), Math.Max(0, H - 2 * margin));

    public override string ToString() => $"{W}x{H}@{X},{Y}";
}
