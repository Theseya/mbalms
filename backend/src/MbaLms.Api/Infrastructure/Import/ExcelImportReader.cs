using ClosedXML.Excel;

namespace MbaLms.Api.Infrastructure.Import;

public sealed record ImportColumnSpec(string Key, IReadOnlyList<string> Headers, bool Required = false);

public sealed class ExcelImportTable
{
    public required string SheetName { get; init; }
    public required IReadOnlyList<ExcelImportDataRow> Rows { get; init; }
    public required IReadOnlyList<ImportCellError> CellErrors { get; init; }
}

public sealed class ExcelImportDataRow
{
    public required int RowNumber { get; init; }
    public required Dictionary<string, string?> Values { get; init; }
}

/// <summary>Reads the first worksheet of an .xlsx for import. Formula cells are rejected (no cached values).</summary>
public static class ExcelImportReader
{
    public const int DefaultMaxRows = 5000;

    /// <summary>Header names that must never appear in any import file (passwords live only in the confirm UI).</summary>
    public static readonly string[] PasswordHeaderNames =
    [
        "пароль", "password", "passwd", "pwd", "pass", "пароль студента", "student password"
    ];

    public static ExcelImportTable Read(
        Stream stream,
        IReadOnlyList<ImportColumnSpec> columns,
        IReadOnlyList<string>? extraForbiddenHeaders = null,
        int maxRows = DefaultMaxRows)
    {
        using var wb = new XLWorkbook(stream);
        if (wb.Worksheets.Count == 0)
            throw AppException.BadRequest(ErrorCodes.ImportInvalidFile);

        var ws = wb.Worksheet(1);
        var sheetName = ws.Name;
        var cellErrors = new List<ImportCellError>();

        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        if (lastCol == 0 || lastRow < 1)
            throw AppException.BadRequest(ErrorCodes.ImportInvalidFile);

        var headerByCol = new Dictionary<int, string>();
        for (var c = 1; c <= lastCol; c++)
        {
            var cell = ws.Cell(1, c);
            if (cell.HasFormula)
            {
                cellErrors.Add(new ImportCellError(sheetName, 1, ColumnLetter(c), FieldCodes.FormulaNotAllowed));
                continue;
            }
            var header = NormalizeHeader(CellText(cell));
            if (header.Length == 0) continue;
            headerByCol[c] = header;
        }

        if (cellErrors.Count > 0)
            return new ExcelImportTable { SheetName = sheetName, Rows = [], CellErrors = cellErrors };

        var forbidden = PasswordHeaderNames
            .Concat(extraForbiddenHeaders ?? [])
            .Select(NormalizeHeader)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var header in headerByCol.Values)
        {
            if (forbidden.Contains(header))
                throw AppException.BadRequest(ErrorCodes.ImportPasswordColumn);
        }

        var colByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var spec in columns)
        {
            var match = headerByCol.FirstOrDefault(kv =>
                spec.Headers.Any(h => HeaderMatches(NormalizeHeader(h), kv.Value)));
            if (match.Key == 0)
            {
                if (spec.Required)
                    throw AppException.BadRequest(ErrorCodes.ImportInvalidFile);
                continue;
            }
            colByKey[spec.Key] = match.Key;
        }

        if (columns.Any(c => c.Required) && !columns.Where(c => c.Required).All(c => colByKey.ContainsKey(c.Key)))
            throw AppException.BadRequest(ErrorCodes.ImportInvalidFile);

        if (lastRow - 1 > maxRows)
            throw AppException.BadRequest(ErrorCodes.ImportInvalidFile);

        var rows = new List<ExcelImportDataRow>();
        for (var r = 2; r <= lastRow; r++)
        {
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            var rowHasFormula = false;
            var rowEmpty = true;

            foreach (var (key, col) in colByKey)
            {
                var cell = ws.Cell(r, col);
                if (cell.HasFormula)
                {
                    cellErrors.Add(new ImportCellError(sheetName, r, ColumnLetter(col), FieldCodes.FormulaNotAllowed, key));
                    rowHasFormula = true;
                    continue;
                }
                var text = CellText(cell);
                if (!string.IsNullOrWhiteSpace(text)) rowEmpty = false;
                values[key] = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }

            // Also reject formulas in non-mapped used cells of this row (e.g. extra columns).
            for (var c = 1; c <= lastCol; c++)
            {
                if (colByKey.ContainsValue(c)) continue;
                var cell = ws.Cell(r, c);
                if (!cell.HasFormula) continue;
                if (cell.IsEmpty() && string.IsNullOrWhiteSpace(cell.GetString())) continue;
                cellErrors.Add(new ImportCellError(sheetName, r, ColumnLetter(c), FieldCodes.FormulaNotAllowed));
                rowHasFormula = true;
            }

            if (rowEmpty && !rowHasFormula) continue;
            if (rowHasFormula) continue;

            rows.Add(new ExcelImportDataRow { RowNumber = r, Values = values });
        }

        return new ExcelImportTable { SheetName = sheetName, Rows = rows, CellErrors = cellErrors };
    }

    private static string NormalizeHeader(string? value) =>
        string.Join(' ', (value ?? string.Empty).Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Exact match, or export headers with a time-zone suffix like "Начало (Europe/Moscow)".</summary>
    private static bool HeaderMatches(string expected, string actual) =>
        string.Equals(expected, actual, StringComparison.Ordinal)
        || actual.StartsWith(expected + " (", StringComparison.Ordinal);

    /// <summary>Plain text only. Must not be called for formula cells.</summary>
    private static string CellText(IXLCell cell)
    {
        if (cell.IsEmpty()) return string.Empty;
        return cell.DataType switch
        {
            XLDataType.Text => cell.GetString(),
            XLDataType.Number => cell.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            XLDataType.Boolean => cell.GetBoolean() ? "true" : "false",
            XLDataType.DateTime => cell.GetDateTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            XLDataType.TimeSpan => FormatTime(cell.GetTimeSpan()),
            _ => cell.GetFormattedString()
        };
    }

    private static string FormatTime(TimeSpan ts) =>
        $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}{(ts.Seconds != 0 ? $":{ts.Seconds:D2}" : "")}";

    private static string ColumnLetter(int columnNumber)
    {
        var n = columnNumber;
        var s = string.Empty;
        while (n > 0)
        {
            n--;
            s = (char)('A' + n % 26) + s;
            n /= 26;
        }
        return s;
    }
}
