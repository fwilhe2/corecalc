using Corecalc;
using Xunit;

namespace Corecalc.Tests;

/// <summary>
/// Helpers for building sheets programmatically. The port has no formula parser
/// (Cell.Parse always returns null), so formulas must be assembled as Expr trees.
/// </summary>
internal static class SheetBuilder
{
    /// <summary>An absolute reference to (col, row) on the given sheet.</summary>
    public static Expr Ref(Sheet sheet, int col, int row)
        => new CellRef(sheet, new RARef(true, col, true, row));

    /// <summary>An absolute reference to the rectangle (c1,r1)..(c2,r2).</summary>
    public static Expr Area(Sheet sheet, int c1, int r1, int c2, int r2)
        => new CellArea(sheet, new RARef(true, c1, true, r1), new RARef(true, c2, true, r2));

    public static Expr Num(double d) => Const.Make(NumberValue.Make(d));

    public static Expr Call(string function, params Expr[] args) => FunCall.Make(function, args);

    public static void SetFormula(this Sheet sheet, int col, int row, Expr e)
        => sheet.SetCell(Formula.Make(sheet.workbook, e), col, row);

    /// <summary>The numeric value of the cell at (col, row); fails if it is not a number.</summary>
    public static double Number(this Sheet sheet, int col, int row)
    {
        Cell cell = sheet[col, row];
        Assert.NotNull(cell);
        Value v = cell!.Eval(sheet, col, row);
        NumberValue? nv = v as NumberValue;
        Assert.True(nv != null, $"cell ({col},{row}) evaluated to {v}, not a number");
        return nv!.value;
    }
}
