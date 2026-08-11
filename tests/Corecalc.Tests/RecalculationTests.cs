using Corecalc;
using Xunit;
using static Corecalc.Tests.SheetBuilder;

namespace Corecalc.Tests;

public class RecalculationTests
{
    /// <summary>
    /// A1=2, A2=3, A3=A1+A2, A4=A3*10, A5=SUM(A1:A2).
    /// </summary>
    private static Sheet BuildSheet(out Workbook wb)
    {
        wb = new Workbook();
        Sheet sheet = new Sheet(wb, "S1", false);
        sheet.SetCell(new NumberCell(2), 0, 0);
        sheet.SetCell(new NumberCell(3), 0, 1);
        sheet.SetFormula(0, 2, Call("+", Ref(sheet, 0, 0), Ref(sheet, 0, 1)));
        sheet.SetFormula(0, 3, Call("*", Ref(sheet, 0, 2), Num(10)));
        sheet.SetFormula(0, 4, Call("SUM", Area(sheet, 0, 0, 0, 1)));
        return sheet;
    }

    [Fact]
    public void EvaluatesFormulasAndRanges()
    {
        Sheet sheet = BuildSheet(out Workbook wb);

        wb.RecalculateFullRebuild();

        Assert.Null(wb.Cyclic);
        Assert.Equal(5, sheet.Number(0, 2));
        Assert.Equal(50, sheet.Number(0, 3));
        Assert.Equal(5, sheet.Number(0, 4));
    }

    [Fact]
    public void MinimalRecalculationPropagatesThroughSupportSets()
    {
        Sheet sheet = BuildSheet(out Workbook wb);
        wb.RecalculateFullRebuild();

        sheet.SetCell(new NumberCell(40), 0, 0);
        wb.Recalculate();

        Assert.Null(wb.Cyclic);
        Assert.Equal(43, sheet.Number(0, 2));   // direct dependent
        Assert.Equal(430, sheet.Number(0, 3));  // transitive dependent
        Assert.Equal(43, sheet.Number(0, 4));   // dependent via a range
    }

    [Fact]
    public void MinimalAndFullRecalculationAgree()
    {
        Sheet sheet = BuildSheet(out Workbook wb);
        wb.RecalculateFullRebuild();

        sheet.SetCell(new NumberCell(40), 0, 0);
        wb.Recalculate();
        double[] minimal = { sheet.Number(0, 2), sheet.Number(0, 3), sheet.Number(0, 4) };

        wb.RecalculateFullRebuild();
        double[] full = { sheet.Number(0, 2), sheet.Number(0, 3), sheet.Number(0, 4) };

        Assert.Equal(full, minimal);
    }

    [Fact]
    public void DetectsSelfReferentialCycle()
    {
        Sheet sheet = BuildSheet(out Workbook wb);

        // A6 = A6 + 1
        sheet.SetFormula(0, 5, Call("+", Ref(sheet, 0, 5), Num(1)));
        wb.RecalculateFullRebuild();

        Assert.NotNull(wb.Cyclic);
        Assert.Contains("CYCLE", wb.Cyclic!.Message);
    }

    [Fact]
    public void DetectsMutualCycle()
    {
        Sheet sheet = BuildSheet(out Workbook wb);

        // A6 = A7 + 1, A7 = A6 + 1
        sheet.SetFormula(0, 5, Call("+", Ref(sheet, 0, 6), Num(1)));
        sheet.SetFormula(0, 6, Call("+", Ref(sheet, 0, 5), Num(1)));
        wb.RecalculateFullRebuild();

        Assert.NotNull(wb.Cyclic);
    }

    [Fact]
    public void NonStrictIfDoesNotEvaluateTheUntakenBranch()
    {
        var wb = new Workbook();
        Sheet sheet = new Sheet(wb, "S1", false);
        sheet.SetCell(new NumberCell(0), 0, 0);

        // A2 = IF(A1 = 0; 42; 1/A1)  -- the else branch would divide by zero
        sheet.SetFormula(0, 1, Call("IF",
            Call("=", Ref(sheet, 0, 0), Num(0)),
            Num(42),
            Call("/", Num(1), Ref(sheet, 0, 0))));
        wb.RecalculateFullRebuild();

        Assert.Equal(42, sheet.Number(0, 1));
    }
}
