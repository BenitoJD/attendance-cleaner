using System.Globalization;
using ClosedXML.Excel;

namespace AttendanceCleaner.Core;

/// <summary>Preserves identifier text when Excel numeric storage would change its value.</summary>
internal static class SpreadsheetValues
{
    // Excel retains only 15 significant digits in numeric cells.
    private const int MaximumNumericIdDigits = 15;

    public static XLCellValue EmployeeId(string value) =>
        value.Length <= MaximumNumericIdDigits
        && !(value.Length > 1 && value[0] == '0')
        && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? XLCellValue.FromObject(id)
            : value;
}
