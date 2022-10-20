namespace Corecalc
{
    public class Program
    {
        public static void Main()
        {
            Workbook wb = new Workbook();
            Sheet sheet = new Sheet(wb, "Sheet 1", false);
            sheet.SetCell(new TextCell("abc"), 0, 0);
            sheet.SetCell(new NumberCell(33.33), 0, 1);
            wb.AddSheet(sheet);
            var x = 0;
            foreach (var s in wb)
            {
                var y = 0;
                foreach (var c in s)
                {
                    Console.WriteLine(c.ShowValue(sheet, x, y));
                    y++;
                }
                x++;
            }
        }
    }
}