using YTile.Core;

namespace YTile.Tests;

[TestClass]
public sealed class LayoutsTests
{
    private static readonly RectI Area = new(0, 0, 2560, 1400);

    [TestMethod]
    public void ZeroWindows_ReturnsEmpty()
    {
        Assert.AreEqual(0, Layouts.Compute(LayoutKind.Bsp, Area, 0, 8).Length);
        Assert.AreEqual(0, Layouts.Compute(LayoutKind.Columns, Area, 0, 8).Length);
    }

    [TestMethod]
    [DataRow((int)LayoutKind.Bsp)]
    [DataRow((int)LayoutKind.Columns)]
    public void SingleWindow_FillsShrunkArea(int kind)
    {
        RectI[] rects = Layouts.Compute((LayoutKind)kind, Area, 1, 10);
        Assert.AreEqual(1, rects.Length);
        Assert.AreEqual(Area.Shrink(10), rects[0]);
    }

    [TestMethod]
    [DataRow((int)LayoutKind.Bsp, 2)]
    [DataRow((int)LayoutKind.Bsp, 3)]
    [DataRow((int)LayoutKind.Bsp, 5)]
    [DataRow((int)LayoutKind.Bsp, 8)]
    [DataRow((int)LayoutKind.Columns, 2)]
    [DataRow((int)LayoutKind.Columns, 3)]
    [DataRow((int)LayoutKind.Columns, 5)]
    [DataRow((int)LayoutKind.Columns, 8)]
    public void Cells_AreWithinAreaAndDisjoint(int kind, int count)
    {
        RectI inner = Area.Shrink(8);
        RectI[] rects = Layouts.Compute((LayoutKind)kind, Area, count, 8);

        Assert.AreEqual(count, rects.Length);
        foreach (RectI r in rects)
        {
            Assert.IsTrue(r.W > 0 && r.H > 0, $"degenerate cell {r}");
            Assert.IsTrue(r.X >= inner.X && r.Y >= inner.Y && r.Right <= inner.Right && r.Bottom <= inner.Bottom,
                $"cell {r} escapes area {inner}");
        }

        for (int i = 0; i < rects.Length; i++)
        {
            for (int j = i + 1; j < rects.Length; j++)
            {
                Assert.IsFalse(Overlaps(rects[i], rects[j]), $"cells {rects[i]} and {rects[j]} overlap");
            }
        }
    }

    [TestMethod]
    public void Columns_AreEqualWidthAndCoverRow()
    {
        RectI inner = Area.Shrink(8);
        RectI[] rects = Layouts.Compute(LayoutKind.Columns, Area, 4, 8);

        foreach (RectI r in rects)
        {
            Assert.AreEqual(inner.Y, r.Y);
            Assert.AreEqual(inner.H, r.H);
        }

        // Widths differ by at most the integer-division remainder.
        int min = rects.Min(r => r.W);
        int max = rects.Max(r => r.W);
        Assert.IsTrue(max - min <= 4, $"column widths too uneven: {min}..{max}");
        Assert.AreEqual(inner.Right, rects[^1].Right);
    }

    [TestMethod]
    public void Bsp_ZeroGap_TilesExactly()
    {
        RectI[] rects = Layouts.Compute(LayoutKind.Bsp, Area, 4, 0);
        long total = rects.Sum(r => (long)r.W * r.H);
        Assert.AreEqual((long)Area.W * Area.H, total, "BSP with zero gap must cover the full area exactly");
    }

    [TestMethod]
    [DataRow((int)LayoutKind.Bsp, 3)]
    [DataRow((int)LayoutKind.Bsp, 5)]
    [DataRow((int)LayoutKind.Columns, 3)]
    [DataRow((int)LayoutKind.Columns, 5)]
    public void DefaultSizing_MatchesNullSizing(int kind, int count)
    {
        double[] defaults = (LayoutKind)kind == LayoutKind.Columns
            ? [.. Enumerable.Repeat(1.0, count)]
            : [.. Enumerable.Repeat(0.5, count - 1)];
        RectI[] plain = Layouts.Compute((LayoutKind)kind, Area, count, 8);
        RectI[] sized = Layouts.Compute((LayoutKind)kind, Area, count, 8, defaults);
        CollectionAssert.AreEqual(plain, sized, "default sizing must reproduce the unadjusted layout");
    }

    [TestMethod]
    public void Bsp_RatioResizesFirstSplit()
    {
        RectI inner = Area.Shrink(8);
        RectI[] rects = Layouts.Compute(LayoutKind.Bsp, Area, 2, 8, [0.7]);

        int span = inner.W - 8;
        Assert.AreEqual((int)(span * 0.7), rects[0].W);
        Assert.AreEqual(inner.Right, rects[1].Right, "second half must still absorb the remainder");
        Assert.AreEqual(rects[0].Right + 8, rects[1].X, "gap between the halves must hold");
    }

    [TestMethod]
    public void Bsp_RatiosAreClamped()
    {
        RectI inner = Area.Shrink(8);
        int span = inner.W - 8;
        RectI[] rects = Layouts.Compute(LayoutKind.Bsp, Area, 2, 8, [0.99]);
        Assert.AreEqual((int)(span * Layouts.MaxRatio), rects[0].W);

        rects = Layouts.Compute(LayoutKind.Bsp, Area, 2, 8, [0.01]);
        Assert.AreEqual((int)(span * Layouts.MinRatio), rects[0].W);
    }

    [TestMethod]
    public void Columns_WeightsAreProportional()
    {
        var area = new RectI(0, 0, 2560, 1400);
        RectI[] rects = Layouts.Compute(LayoutKind.Columns, area, 3, 0, [2.0, 1.0, 1.0]);

        Assert.AreEqual(1280, rects[0].W);
        Assert.AreEqual(640, rects[1].W);
        Assert.AreEqual(640, rects[2].W);
        Assert.AreEqual(area.Right, rects[^1].Right);
    }

    [TestMethod]
    [DataRow((int)LayoutKind.Bsp, 4)]
    [DataRow((int)LayoutKind.Columns, 4)]
    public void SizedCells_AreWithinAreaAndDisjoint(int kind, int count)
    {
        RectI inner = Area.Shrink(8);
        double[] sizing = (LayoutKind)kind == LayoutKind.Columns
            ? [2.0, 0.5, 1.5, 1.0]
            : [0.3, 0.7, 0.4];
        RectI[] rects = Layouts.Compute((LayoutKind)kind, Area, count, 8, sizing);

        Assert.AreEqual(count, rects.Length);
        foreach (RectI r in rects)
        {
            Assert.IsTrue(r.W > 0 && r.H > 0, $"degenerate cell {r}");
            Assert.IsTrue(r.X >= inner.X && r.Y >= inner.Y && r.Right <= inner.Right && r.Bottom <= inner.Bottom,
                $"cell {r} escapes area {inner}");
        }

        for (int i = 0; i < rects.Length; i++)
        {
            for (int j = i + 1; j < rects.Length; j++)
            {
                Assert.IsFalse(Overlaps(rects[i], rects[j]), $"cells {rects[i]} and {rects[j]} overlap");
            }
        }
    }

    private static bool Overlaps(RectI a, RectI b) =>
        a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;
}
