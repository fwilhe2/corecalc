namespace Corecalc;
public class Program
{
    public static void Main()
    {
        Workbook wb = new();
        Sheet sheet = new(wb, "Sheet 1", false);
        sheet.SetCell(new TextCell("abc"), 0, 0);
        sheet.SetCell(new NumberCell(33.33), 0, 1);
        Expr[] args = { new NumberConst(1), new NumberConst(1), new NumberConst(1) };
        sheet.SetCell(new Formula(wb, FunCall.Make("SUM", args)), 0, 2);
        wb.AddSheet(sheet);
        var x = 0;
        foreach (var s in wb)
        {
            var y = 0;
            foreach (var c in s)
            {
                if (c is Formula) {
                    Console.WriteLine(c.Eval(sheet, x, y));
                }
                Console.WriteLine(c.Show(sheet, x, y));

                y++;
            }
            x++;
        }
    }
}