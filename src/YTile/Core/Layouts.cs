namespace YTile.Core;

internal enum LayoutKind
{
    Bsp,
    Columns,
}

/// <summary>
/// Layouts are pure functions: (work area, window count, gap) -> one rect per
/// window, in tiling order. No window handles, no OS calls — fully unit-testable.
/// </summary>
internal static class Layouts
{
    public static RectI[] Compute(LayoutKind kind, RectI workArea, int count, int gap)
    {
        if (count <= 0)
        {
            return [];
        }

        RectI area = workArea.Shrink(gap);
        return kind switch
        {
            LayoutKind.Columns => Columns(area, count, gap),
            _ => Bsp(area, count, gap),
        };
    }

    private static RectI[] Columns(RectI area, int count, int gap)
    {
        var rects = new RectI[count];
        int width = (area.W - gap * (count - 1)) / count;
        for (int i = 0; i < count; i++)
        {
            int x = area.X + i * (width + gap);
            // Last column absorbs the integer-division remainder.
            int w = i == count - 1 ? area.Right - x : width;
            rects[i] = new RectI(x, area.Y, w, area.H);
        }

        return rects;
    }

    /// <summary>
    /// Dwindle BSP: each window splits the remaining area in half, along the
    /// longer axis, and the remainder recurses into the second half.
    /// </summary>
    private static RectI[] Bsp(RectI area, int count, int gap)
    {
        var rects = new RectI[count];
        RectI cur = area;
        for (int i = 0; i < count; i++)
        {
            if (i == count - 1)
            {
                rects[i] = cur;
                break;
            }

            if (cur.W >= cur.H)
            {
                int w = (cur.W - gap) / 2;
                rects[i] = cur with { W = w };
                cur = new RectI(cur.X + w + gap, cur.Y, cur.W - w - gap, cur.H);
            }
            else
            {
                int h = (cur.H - gap) / 2;
                rects[i] = cur with { H = h };
                cur = new RectI(cur.X, cur.Y + h + gap, cur.W, cur.H - h - gap);
            }
        }

        return rects;
    }
}
