using Corecalc;
using Xunit;

namespace Corecalc.Tests;

/// <summary>
/// Regression tests for CellAddr/FullCellAddr equality. The == and != operators
/// once compared ca1.row against ca2.col instead of ca2.row, so they disagreed
/// with Equals in both directions. Only pairs where col == row compared
/// correctly, which is why the asymmetric cases below are the interesting ones.
/// </summary>
public class CellAddrTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]   // col != row: the case the old operator got wrong
    [InlineData(3, 3)]
    [InlineData(5, 99)]
    public void EqualAddressesCompareEqual(int col, int row)
    {
        var a = new CellAddr(col, row);
        var b = new CellAddr(col, row);

        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Theory]
    [InlineData(1, 1, 1, 3)]   // same col, row of one equals col of the other
    [InlineData(0, 1, 1, 0)]   // transposed
    [InlineData(2, 5, 2, 7)]
    [InlineData(2, 5, 4, 5)]
    public void DifferentAddressesCompareUnequal(int col1, int row1, int col2, int row2)
    {
        var a = new CellAddr(col1, row1);
        var b = new CellAddr(col2, row2);

        Assert.False(a == b);
        Assert.True(a != b);
        Assert.False(a.Equals(b));
    }

    [Fact]
    public void OperatorsAgreeWithEqualsAcrossAGrid()
    {
        for (int c1 = 0; c1 < 6; c1++)
            for (int r1 = 0; r1 < 6; r1++)
                for (int c2 = 0; c2 < 6; c2++)
                    for (int r2 = 0; r2 < 6; r2++)
                    {
                        var a = new CellAddr(c1, r1);
                        var b = new CellAddr(c2, r2);
                        Assert.Equal(a.Equals(b), a == b);
                        Assert.Equal(!a.Equals(b), a != b);
                    }
    }

    [Fact]
    public void FullCellAddrEqualityFollowsCellAddr()
    {
        var wb = new Workbook();
        var s1 = new Sheet(wb, "S1", false);
        var s2 = new Sheet(wb, "S2", false);

        Assert.True(new FullCellAddr(s1, 0, 1) == new FullCellAddr(s1, 0, 1));
        Assert.False(new FullCellAddr(s1, 0, 1) != new FullCellAddr(s1, 0, 1));

        // Same address, different sheet
        Assert.False(new FullCellAddr(s1, 0, 1) == new FullCellAddr(s2, 0, 1));
        Assert.True(new FullCellAddr(s1, 0, 1) != new FullCellAddr(s2, 0, 1));

        // Same sheet, transposed address
        Assert.False(new FullCellAddr(s1, 0, 1) == new FullCellAddr(s1, 1, 0));
    }
}
