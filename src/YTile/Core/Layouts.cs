namespace YTile.Core;

internal enum LayoutKind
{
    Bsp,
    Columns,
}

/// <summary>
/// One BSP split: window <c>i</c> takes <see cref="FirstPx"/> of <see cref="Span"/>
/// along the split axis; the remainder dwindles into the next split.
/// </summary>
internal readonly record struct BspSplit(bool Vertical, int Span, int FirstPx);

/// <summary>
/// Layouts are pure functions: (work area, window count, gap, sizing) -> one
/// rect per window, in tiling order. No window handles, no OS calls — fully
/// unit-testable. Sizing is optional per-workspace state: BSP takes one ratio
/// per split (default 0.5), Columns one width weight per column (default 1.0);
/// null or empty reproduces the unadjusted layout exactly.
/// </summary>
internal static class Layouts
{
    // A split may not push either side below this share of its span.
    public const double MinRatio = 0.1;
    public const double MaxRatio = 0.9;

    // Column weights outside this range collapse or starve their neighbors.
    public const double MinWeight = 0.1;
    public const double MaxWeight = 10.0;

    public static RectI[] Compute(LayoutKind kind, RectI workArea, int count, int gap, IReadOnlyList<double>? sizing = null)
    {
        if (count <= 0)
        {
            return [];
        }

        RectI area = workArea.Shrink(gap);
        return kind switch
        {
            LayoutKind.Columns => Columns(area, count, gap, sizing),
            _ => BspCore(area, count, gap, sizing).Rects,
        };
    }

    /// <summary>
    /// The splits BSP performs for this configuration — lets a resize map a
    /// window's dragged edge back to the split (and ratio) that controls it.
    /// </summary>
    public static BspSplit[] BspSplits(RectI workArea, int count, int gap, IReadOnlyList<double>? ratios)
        => count <= 1 ? [] : BspCore(workArea.Shrink(gap), count, gap, ratios).Splits;

    private static RectI[] Columns(RectI area, int count, int gap, IReadOnlyList<double>? weights)
    {
        var rects = new RectI[count];
        if (!HasWeights(weights, count))
        {
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

        // Weighted: column widths proportional to their weights, positioned by
        // cumulative prefix so totals stay exact; last column still absorbs
        // the rounding remainder.
        int avail = area.W - gap * (count - 1);
        double total = 0;
        for (int i = 0; i < count; i++)
        {
            total += WeightAt(weights, i);
        }

        double acc = 0;
        int prevEnd = 0;
        for (int i = 0; i < count; i++)
        {
            acc += WeightAt(weights, i);
            int end = i == count - 1 ? avail : (int)(avail * acc / total);
            int x = area.X + prevEnd + i * gap;
            rects[i] = new RectI(x, area.Y, end - prevEnd, area.H);
            prevEnd = end;
        }

        return rects;
    }

    /// <summary>
    /// Dwindle BSP: each window splits the remaining area along the longer
    /// axis — taking its split's ratio of the span — and the remainder
    /// recurses into the second half.
    /// </summary>
    private static (RectI[] Rects, BspSplit[] Splits) BspCore(RectI area, int count, int gap, IReadOnlyList<double>? ratios)
    {
        var rects = new RectI[count];
        var splits = new BspSplit[Math.Max(0, count - 1)];
        RectI cur = area;
        for (int i = 0; i < count; i++)
        {
            if (i == count - 1)
            {
                rects[i] = cur;
                break;
            }

            bool vertical = cur.W >= cur.H;
            int span = (vertical ? cur.W : cur.H) - gap;
            int first = (int)(span * RatioAt(ratios, i));
            splits[i] = new BspSplit(vertical, span, first);
            if (vertical)
            {
                rects[i] = cur with { W = first };
                cur = new RectI(cur.X + first + gap, cur.Y, cur.W - first - gap, cur.H);
            }
            else
            {
                rects[i] = cur with { H = first };
                cur = new RectI(cur.X, cur.Y + first + gap, cur.W, cur.H - first - gap);
            }
        }

        return (rects, splits);
    }

    private static double RatioAt(IReadOnlyList<double>? ratios, int i)
        => ratios is not null && i < ratios.Count && ratios[i] > 0
            ? Math.Clamp(ratios[i], MinRatio, MaxRatio)
            : 0.5;

    private static double WeightAt(IReadOnlyList<double>? weights, int i)
        => weights is not null && i < weights.Count && weights[i] > 0
            ? Math.Clamp(weights[i], MinWeight, MaxWeight)
            : 1.0;

    private static bool HasWeights(IReadOnlyList<double>? weights, int count)
    {
        if (weights is null)
        {
            return false;
        }

        for (int i = 0; i < Math.Min(weights.Count, count); i++)
        {
            if (weights[i] > 0 && Math.Abs(weights[i] - 1.0) > 1e-9)
            {
                return true;
            }
        }

        return false;
    }
}
