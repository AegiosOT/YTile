using YTile.Core;

namespace YTile.Tests;

[TestClass]
public sealed class LayoutResizerTests
{
    // Wide area: with two windows the BSP split is vertical (side by side).
    private static readonly RectI Area = new(0, 0, 2560, 1400);
    private const int Gap = 8;

    private static RectI[] Recompute(LayoutKind kind, int count, List<double> ratios, List<double> weights)
        => Layouts.Compute(kind, Area, count, Gap, kind == LayoutKind.Columns ? weights : ratios);

    [TestMethod]
    public void Bsp_DragRightEdge_PersistsWidth()
    {
        List<double> ratios = [];
        List<double> weights = [];
        RectI[] before = Recompute(LayoutKind.Bsp, 2, ratios, weights);

        bool changed = LayoutResizer.ApplyEdgeDeltas(
            LayoutKind.Bsp, Area, 2, Gap, ratios, weights, index: 0,
            dLeft: 0, dTop: 0, dRight: 200, dBottom: 0, minDelta: 8);

        Assert.IsTrue(changed);
        RectI[] after = Recompute(LayoutKind.Bsp, 2, ratios, weights);
        Assert.IsTrue(Math.Abs(after[0].W - (before[0].W + 200)) <= 1,
            $"expected ~{before[0].W + 200}, got {after[0].W}");
        Assert.AreEqual(before[1].Right, after[1].Right, "outer edge must not move");
    }

    [TestMethod]
    public void Bsp_LastWindowLeftEdge_AdjustsEarlierSplit()
    {
        List<double> ratios = [];
        List<double> weights = [];
        RectI[] before = Recompute(LayoutKind.Bsp, 2, ratios, weights);

        // Growing the last window leftward shrinks the first window.
        bool changed = LayoutResizer.ApplyEdgeDeltas(
            LayoutKind.Bsp, Area, 2, Gap, ratios, weights, index: 1,
            dLeft: -150, dTop: 0, dRight: 0, dBottom: 0, minDelta: 8);

        Assert.IsTrue(changed);
        RectI[] after = Recompute(LayoutKind.Bsp, 2, ratios, weights);
        Assert.IsTrue(Math.Abs(after[0].W - (before[0].W - 150)) <= 1,
            $"expected ~{before[0].W - 150}, got {after[0].W}");
        Assert.IsTrue(after[1].X < before[1].X);
    }

    [TestMethod]
    public void Bsp_OuterEdges_DoNothing()
    {
        List<double> ratios = [];
        List<double> weights = [];

        // Window 0's left/top edges and window 1's right edge sit on the work
        // area boundary — no split controls them.
        Assert.IsFalse(LayoutResizer.ApplyEdgeDeltas(
            LayoutKind.Bsp, Area, 2, Gap, ratios, weights, 0, dLeft: -50, 0, 0, 0, minDelta: 8));
        Assert.IsFalse(LayoutResizer.ApplyEdgeDeltas(
            LayoutKind.Bsp, Area, 2, Gap, ratios, weights, 0, 0, dTop: -50, 0, 0, minDelta: 8));
        Assert.IsFalse(LayoutResizer.ApplyEdgeDeltas(
            LayoutKind.Bsp, Area, 2, Gap, ratios, weights, 1, 0, 0, dRight: 50, 0, minDelta: 8));
        Assert.AreEqual(0, ratios.Count);
    }

    [TestMethod]
    public void Bsp_CornerDrag_AdjustsTwoSplits()
    {
        List<double> ratios = [];
        List<double> weights = [];
        RectI[] before = Recompute(LayoutKind.Bsp, 3, ratios, weights);

        // Dwindle with 3 windows on a wide area: window 1 is the top half of
        // the right region — its left edge is split 0 (vertical), its bottom
        // edge is split 1 (horizontal).
        bool changed = LayoutResizer.ApplyEdgeDeltas(
            LayoutKind.Bsp, Area, 3, Gap, ratios, weights, index: 1,
            dLeft: -100, dTop: 0, dRight: 0, dBottom: 120, minDelta: 8);

        Assert.IsTrue(changed);
        RectI[] after = Recompute(LayoutKind.Bsp, 3, ratios, weights);
        Assert.IsTrue(Math.Abs(after[1].X - (before[1].X - 100)) <= 1,
            $"left edge: expected ~{before[1].X - 100}, got {after[1].X}");
        Assert.IsTrue(Math.Abs(after[1].H - (before[1].H + 120)) <= 1,
            $"height: expected ~{before[1].H + 120}, got {after[1].H}");
    }

    [TestMethod]
    public void Bsp_SmallKeyboardStep_AppliesWithMinDeltaOne()
    {
        List<double> ratios = [];
        List<double> weights = [];
        RectI[] before = Recompute(LayoutKind.Bsp, 2, ratios, weights);

        bool changed = LayoutResizer.ApplyEdgeDeltas(
            LayoutKind.Bsp, Area, 2, Gap, ratios, weights, 0, 0, 0, dRight: 5, 0, minDelta: 1);

        Assert.IsTrue(changed);
        RectI[] after = Recompute(LayoutKind.Bsp, 2, ratios, weights);
        Assert.IsTrue(Math.Abs(after[0].W - (before[0].W + 5)) <= 1);
    }

    [TestMethod]
    public void Bsp_HugeDelta_ClampsAtMaxRatio()
    {
        List<double> ratios = [];
        List<double> weights = [];

        Assert.IsTrue(LayoutResizer.ApplyEdgeDeltas(
            LayoutKind.Bsp, Area, 2, Gap, ratios, weights, 0, 0, 0, dRight: 100_000, 0, minDelta: 8));
        Assert.AreEqual(Layouts.MaxRatio, ratios[0]);

        // Already at the clamp — pushing further is a no-op.
        Assert.IsFalse(LayoutResizer.ApplyEdgeDeltas(
            LayoutKind.Bsp, Area, 2, Gap, ratios, weights, 0, 0, 0, dRight: 100_000, 0, minDelta: 8));
    }

    [TestMethod]
    public void Columns_MiddleColumn_BothEdges()
    {
        var area = new RectI(0, 0, 2400, 1400);
        List<double> ratios = [];
        List<double> weights = [];

        // Grow the middle column 100px on each side.
        bool changed = LayoutResizer.ApplyEdgeDeltas(
            LayoutKind.Columns, area, 3, 0, ratios, weights, index: 1,
            dLeft: -100, dTop: 0, dRight: 100, dBottom: 0, minDelta: 8);

        Assert.IsTrue(changed);
        RectI[] after = Layouts.Compute(LayoutKind.Columns, area, 3, 0, weights);
        Assert.IsTrue(Math.Abs(after[0].W - 700) <= 2, $"col0 {after[0].W}");
        Assert.IsTrue(Math.Abs(after[1].W - 1000) <= 2, $"col1 {after[1].W}");
        Assert.IsTrue(Math.Abs(after[2].W - 700) <= 2, $"col2 {after[2].W}");
        Assert.AreEqual(area.Right, after[^1].Right);
    }

    [TestMethod]
    public void Columns_NeighborNeverBelowMinimumWidth()
    {
        var area = new RectI(0, 0, 2400, 1400);
        List<double> ratios = [];
        List<double> weights = [];

        Assert.IsTrue(LayoutResizer.ApplyEdgeDeltas(
            LayoutKind.Columns, area, 2, 0, ratios, weights, 0, 0, 0, dRight: 100_000, 0, minDelta: 8));
        RectI[] after = Layouts.Compute(LayoutKind.Columns, area, 2, 0, weights);
        Assert.IsTrue(after[1].W >= 48, $"neighbor collapsed to {after[1].W}px");
        Assert.AreEqual(area.Right, after[^1].Right);
    }

    [TestMethod]
    public void Columns_OuterEdges_DoNothing()
    {
        List<double> ratios = [];
        List<double> weights = [];

        Assert.IsFalse(LayoutResizer.ApplyEdgeDeltas(
            LayoutKind.Columns, Area, 2, Gap, ratios, weights, 0, dLeft: -50, 0, 0, 0, minDelta: 8));
        Assert.IsFalse(LayoutResizer.ApplyEdgeDeltas(
            LayoutKind.Columns, Area, 2, Gap, ratios, weights, 1, 0, 0, dRight: 50, 0, minDelta: 8));
        Assert.AreEqual(0, weights.Count);
    }

    [TestMethod]
    public void SingleWindow_NothingToResize()
    {
        List<double> ratios = [];
        List<double> weights = [];
        Assert.IsFalse(LayoutResizer.ApplyEdgeDeltas(
            LayoutKind.Bsp, Area, 1, Gap, ratios, weights, 0, -50, -50, 50, 50, minDelta: 8));
    }

    [TestMethod]
    public void JitterBelowMinDelta_Ignored()
    {
        List<double> ratios = [];
        List<double> weights = [];
        Assert.IsFalse(LayoutResizer.ApplyEdgeDeltas(
            LayoutKind.Bsp, Area, 2, Gap, ratios, weights, 0, 0, 0, dRight: 5, 0, minDelta: 8));
    }
}
