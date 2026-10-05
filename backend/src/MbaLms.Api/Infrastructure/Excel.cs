using ClosedXML.Excel;

namespace MbaLms.Api.Infrastructure;

public static class SpreadsheetSanitizer
{
    private static readonly char[] FormulaPrefixes = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>True if Excel could interpret the text as a formula (formula injection risk).</summary>
    public static bool IsFormulaLike(string? value) =>
        !string.IsNullOrEmpty(value) && Array.IndexOf(FormulaPrefixes, value[0]) >= 0;

    /// <summary>Escaping for plain-text formats (CSV): prefix with an apostrophe.</summary>
    public static string EscapeForCsv(string? value) =>
        IsFormulaLike(value) ? "'" + value : value ?? string.Empty;
}

public record ExcelColumn<T>(string Header, Func<T, object?> Value, string? Format = null);

public static class ExcelExporter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static byte[] Build<T>(string sheetName, IReadOnlyList<ExcelColumn<T>> columns, IEnumerable<T> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(sheetName);

        for (var c = 0; c < columns.Count; c++)
        {
            var cell = ws.Cell(1, c + 1);
            WriteText(cell, columns[c].Header);
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.LightGray;
        }

        var r = 2;
        foreach (var row in rows)
        {
            for (var c = 0; c < columns.Count; c++)
            {
                var cell = ws.Cell(r, c + 1);
                var value = columns[c].Value(row);
                switch (value)
                {
                    case null:
                        break;
                    case int i:
                        cell.Value = i;
                        break;
                    case DateTime dt:
                        cell.Value = dt;
                        cell.Style.DateFormat.Format = columns[c].Format ?? "dd.MM.yyyy HH:mm";
                        break;
                    case DateOnly d:
                        cell.Value = d.ToDateTime(TimeOnly.MinValue);
                        cell.Style.DateFormat.Format = columns[c].Format ?? "dd.MM.yyyy";
                        break;
                    case TimeOnly time:
                        cell.Value = time.ToTimeSpan();
                        cell.Style.NumberFormat.Format = columns[c].Format ?? "HH:mm";
                        break;
                    default:
                        WriteText(cell, value.ToString() ?? string.Empty);
                        break;
                }
            }
            r++;
        }

        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents(1, Math.Min(r, 200), 8, 60);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Strings are always written as text cells, never as formulas. Formula-like text additionally gets
    /// Excel's quote prefix, so it stays literal even after the user edits the cell.
    /// </summary>
    private static void WriteText(IXLCell cell, string text)
    {
        cell.Value = text;
        cell.Style.NumberFormat.Format = "@";
        if (SpreadsheetSanitizer.IsFormulaLike(text)) cell.Style.IncludeQuotePrefix = true;
    }
}
