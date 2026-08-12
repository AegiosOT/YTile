namespace YTile.Core;

/// <summary>
/// Folds a resize of one window — pixel deltas on its four edges — into the
/// workspace's sizing state (<see cref="Layouts"/> BSP ratios / column
/// weights), so the next retile keeps the user's size instead of snapping
/// back. Pure math, no OS calls.
///
/// BSP edge → split mapping (dwindle geometry): window i is the first half of
/// split i, so its right (vertical split) or bottom (horizontal split) edge IS
/// that split's boundary. Its left/top edge belongs to the most recent earlier
/// split along the same axis — the one whose remainder region it lives in.
/// Edges on the outer work-area boundary belong to no split and are ignored.
/// </summary>
internal static class LayoutResizer
{
    // A weighted column may not shrink below this many pixels.
    private const int MinColumnPx = 50;

    /// <summary>
    /// Applies edge deltas from a resize of window <paramref name="index"/>.
    /// Deltas smaller than <paramref name="minDelta"/> are jitter and ignored.
    /// Returns true if any sizing state changed (caller should retile).
    /// </summary>
    public static bool ApplyEdgeDeltas(
        LayoutKind kind, RectI workArea, int count, int gap,
        List<double> bspRatios, List<double> columnWeights, int index,
        int dLeft, int dTop, int dRight, int dBottom, int minDelta)
    {
        if (count < 2 || index < 0 || index >= count)
        {
            return false;
        }

        return kind switch
        {
            LayoutKind.Columns => ApplyColumns(workArea, count, gap, columnWeights, index, dLeft, dRight, minDelta),
            _ => ApplyBsp(workArea, count, gap, bspRatios, index, dLeft, dTop, dRight, dBottom, minDelta),
        };
    }

    private static bool ApplyBsp(
        RectI workArea, int count, int gap, List<double> ratios, int index,
        int dLeft, int dTop, int dRight, int dBottom, int minDelta)
    {
        BspSplit[] splits = Layouts.BspSplits(workArea, count, gap, ratios);
        bool changed = false;

        if (Math.Abs(dRight) >= minDelta && index < splits.Length && splits[index].Vertical)
        {
            changed |= AdjustSplit(ratios, splits[index], index, dRight);
        }

        if (Math.Abs(dBottom) >= minDelta && index < splits.Length && !splits[index].Vertical)
        {
            changed |= AdjustSplit(ratios, splits[index], index, dBottom);
        }

        if (Math.Abs(dLeft) >= minDelta)
        {
            int vj = LastSplitBefore(splits, index, vertical: true);
            if (vj >= 0)
            {
                changed |= AdjustSplit(ratios, splits[vj], vj, dLeft);
            }
        }

        if (Math.Abs(dTop) >= minDelta)
        {
            int hj = LastSplitBefore(splits, index, vertical: false);
            if (hj >= 0)
            {
                changed |= AdjustSplit(ratios, splits[hj], hj, dTop);
            }
        }

        return changed;
    }

    private static int LastSplitBefore(BspSplit[] splits, int index, bool vertical)
    {
        for (int j = Math.Min(index, splits.Length) - 1; j >= 0; j--)
        {
            if (splits[j].Vertical == vertical)
            {
                return j;
            }
        }

        return -1;
    }

    /// <summary>Moves split <paramref name="j"/>'s boundary by <paramref name="deltaPx"/>.</summary>
    private static bool AdjustSplit(List<double> ratios, BspSplit split, int j, int deltaPx)
    {
        if (split.Span <= 0)
        {
            return false;
        }

        double next = Math.Clamp((double)(split.FirstPx + deltaPx) / split.Span, Layouts.MinRatio, Layouts.MaxRatio);
        if (Math.Abs(next - (double)split.FirstPx / split.Span) < 0.001)
        {
            return false; // already at a clamp bound, or a no-op delta
        }

        while (ratios.Count <= j)
        {
            ratios.Add(0.5);
        }
        ratios[j] = next;
        return true;
    }

    private static bool ApplyColumns(
        RectI workArea, int count, int gap, List<double> weights, int index,
        int dLeft, int dRight, int minDelta)
    {
        RectI[] cells = Layouts.Compute(LayoutKind.Columns, workArea, count, gap, weights);
        var widths = new double[count];
        for (int i = 0; i < count; i++)
        {
            widths[i] = cells[i].W;
        }

        bool changed = false;
        if (Math.Abs(dRight) >= minDelta && index < count - 1)
        {
            changed |= ShiftBoundary(widths, index, dRight);
        }

        if (Math.Abs(dLeft) >= minDelta && index > 0)
        {
            changed |= ShiftBoundary(widths, index - 1, dLeft);
        }

        if (!changed)
        {
            return false;
        }

        // Store widths as weights normalized to mean 1.0 — keeps the numbers
        // meaningful and drops any stale tail from a larger window count.
        double mean = 0;
        for (int i = 0; i < count; i++)
        {
            mean += widths[i];
        }
        mean /= count;
        if (mean <= 0)
        {
            return false;
        }

        weights.Clear();
        for (int i = 0; i < count; i++)
        {
            weights.Add(widths[i] / mean);
        }

        return true;
    }

    /// <summary>Moves the boundary between columns <paramref name="left"/> and left+1.</summary>
    private static bool ShiftBoundary(double[] widths, int left, int deltaPx)
    {
        double lo = -Math.Max(0, widths[left] - MinColumnPx);
        double hi = Math.Max(0, widths[left + 1] - MinColumnPx);
        double d = Math.Clamp((double)deltaPx, lo, hi);
        if (Math.Abs(d) < 1)
        {
            return false;
        }

        widths[left] += d;
        widths[left + 1] -= d;
        return true;
    }
}
