using Corecalc;
using Corecalc.Funcalc;
using Xunit;
using static Corecalc.Tests.SheetBuilder;

namespace Corecalc.Tests;

/// <summary>
/// Funcalc: compiling a region of a function sheet to a .NET delegate.
/// These exercise the path that used to abort under Debug via the
/// Debug.Asserts in ProgramLines.CreateSdfDelegate -- see CellAddrTests.
/// </summary>
public class SdfTests
{
    /// <summary>
    /// Defines MYF(x) = IF(x > 10; x * 2; x + 100) on a function sheet, with the
    /// input at B1 and the output at B2 so that the output cell has col != row.
    /// </summary>
    private static SdfInfo DefineMyf(Workbook wb, string name)
    {
        Sheet fs = new Sheet(wb, "F1", true);
        fs.SetCell(new NumberCell(0), 0, 0);         // input placeholder
        Expr x = Ref(fs, 0, 0);
        fs.SetFormula(0, 1, Call("IF",
            Call(">", x, Num(10)),
            Call("*", x, Num(2)),
            Call("+", x, Num(100))));
        wb.RecalculateFullRebuild();

        SdfManager.CreateFunction(name,
                                  new FullCellAddr(fs, 0, 1),
                                  new List<FullCellAddr> { new FullCellAddr(fs, 0, 0) });
        return SdfManager.GetInfo(name);
    }

    [Theory]
    [InlineData(5, 105)]     // else branch
    [InlineData(10, 110)]    // boundary, else branch
    [InlineData(20, 40)]     // then branch
    [InlineData(-3, 97)]
    public void CompiledFunctionEvaluatesBothBranches(double arg, double expected)
    {
        var wb = new Workbook();
        SdfInfo info = DefineMyf(wb, "MYF");

        Value result = info.Call1(NumberValue.Make(arg));

        NumberValue? nv = result as NumberValue;
        Assert.True(nv != null, $"MYF({arg}) returned {result}, not a number");
        Assert.Equal(expected, nv!.value);
    }

    [Fact]
    public void CompiledFunctionHasExpectedMetadata()
    {
        var wb = new Workbook();
        SdfInfo info = DefineMyf(wb, "MYF");

        Assert.NotNull(info);
        Assert.Equal(1, info.arity);
        Assert.False(info.IsVolatile);
        Assert.Equal("MYF", info.name);
    }

    [Fact]
    public void CompiledFunctionIsCallableFromAnOrdinarySheet()
    {
        var wb = new Workbook();
        DefineMyf(wb, "MYF");

        Sheet sheet = new Sheet(wb, "S1", false);
        sheet.SetCell(new NumberCell(40), 0, 0);
        sheet.SetFormula(0, 1, Call("MYF", Ref(sheet, 0, 0)));
        wb.RecalculateFullRebuild();

        Assert.Null(wb.Cyclic);
        Assert.Equal(80, sheet.Number(0, 1));
    }

    [Fact]
    public void StaticCycleOnAFunctionSheetIsRejectedAtCompileTime()
    {
        var wb = new Workbook();
        Sheet fs = new Sheet(wb, "F1", true);
        fs.SetCell(new NumberCell(0), 0, 0);
        // B2 = B3 + 1, B3 = B2 + 1
        fs.SetFormula(0, 1, Call("+", Ref(fs, 0, 2), Num(1)));
        fs.SetFormula(0, 2, Call("+", Ref(fs, 0, 1), Num(1)));

        Assert.Throws<CyclicException>(() =>
            SdfManager.CreateFunction("CYC",
                                      new FullCellAddr(fs, 0, 1),
                                      new List<FullCellAddr> { new FullCellAddr(fs, 0, 0) }));
    }
}
