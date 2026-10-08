using System.Globalization;
using ClosedXML.Excel;

namespace AttendanceCleaner.Core;

/// <summary>One fully formatted output row, as written to the Excel and shown in the app.</summary>
public sealed record TemplateRow(
    int SlNo,
    string IdNo,
    string Name,
    string Gender,
    string DateText,
    string Day,
    string? InPunch,
    string? OutPunch,
    string? TotalHours,
    string Remark)
{
    /// <summary>Cell values in TemplateSpec.Headers order (empty string where the sheet is blank).</summary>
    public string[] Cells => new[]
    {
        SlNo.ToString(CultureInfo.InvariantCulture),
        IdNo,
        Name,
        Gender,
        DateText,
        Day,
        InPunch ?? "",
        OutPunch ?? "",
        TotalHours ?? "",
        Remark,
    };
}

/// <summary>
/// Writes parsed attendance records into the template defined by <see cref="TemplateSpec"/>
/// and exposes the same rows for in-app display.
/// </summary>
public static class TemplateWriter
{
    public static IReadOnlyList<TemplateRow> BuildRows(IEnumerable<AttendanceRecord> records)
    {
        var rows = new List<TemplateRow>();
        foreach (var group in records.GroupBy(r => r.Date).OrderBy(g => g.Key))
        {
            var slNo = 1;
            foreach (var record in group.OrderBy(r => r.Order))
            {
                rows.Add(new TemplateRow(
                    SlNo: slNo++,
                    IdNo: record.Id,
                    Name: record.Name,
                    Gender: string.IsNullOrEmpty(record.Gender) ? TemplateSpec.UnknownGender : record.Gender,
                    DateText: record.Date.ToString(TemplateSpec.DateFormat, CultureInfo.InvariantCulture),
                    Day: record.Date.ToString("ddd", CultureInfo.InvariantCulture),
                    InPunch: record.InPunch,
                    OutPunch: record.OutPunch,
                    TotalHours: TotalHours(record),
                    Remark: Remark(record)));
            }
        }
        return rows;
    }

    public static void Write(IEnumerable<AttendanceRecord> records, Stream output)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add(TemplateSpec.SheetName);

        for (var c = 0; c < TemplateSpec.Headers.Length; c++)
        {
            var header = ws.Cell(1, c + 1);
            header.Value = TemplateSpec.Headers[c];
            header.Style.Font.Bold = true;
        }
        ws.SheetView.FreezeRows(1);

        var row = 2;
        foreach (var r in BuildRows(records))
        {
            ws.Cell(row, 1).Value = r.SlNo;
            ws.Cell(row, 2).Value = SpreadsheetValues.EmployeeId(r.IdNo);
            ws.Cell(row, 3).Value = r.Name;
            ws.Cell(row, 4).Value = r.Gender;
            ws.Cell(row, 5).Value = r.DateText;
            ws.Cell(row, 6).Value = r.Day;
            if (r.InPunch != null) ws.Cell(row, 7).Value = r.InPunch;
            if (r.OutPunch != null) ws.Cell(row, 8).Value = r.OutPunch;
            if (r.TotalHours != null) ws.Cell(row, 9).Value = r.TotalHours;
            ws.Cell(row, 10).Value = r.Remark;
            row++;
        }

        ws.Columns().AdjustToContents();
        foreach (var (column, width) in TemplateSpec.ColumnWidths)
        {
            ws.Column(column).Width = width;
        }

        // filter dropdown on every column, covering exactly the header and the data rows
        if (row > 2)
        {
            ws.Range(1, 1, row - 1, TemplateSpec.Headers.Length).SetAutoFilter();
        }

        workbook.SaveAs(output);
    }

    public static void WriteToFile(IEnumerable<AttendanceRecord> records, string path)
    {
        using var stream = File.Create(path);
        Write(records, stream);
    }

    private static string Remark(AttendanceRecord record) =>
        record.InPunch != null && record.OutPunch != null ? TemplateSpec.RemarkPresent
        : record.InPunch != null || record.OutPunch != null ? TemplateSpec.RemarkInPunchOnly
        : TemplateSpec.RemarkAbsent;

    private static string? TotalHours(AttendanceRecord record) =>
        WorkedMinutes(record) is { } minutes ? AttendanceTime.FormatMinutes(minutes) : null;

    /// <summary>Use the export's attended duration consistently before falling back to punches.</summary>
    internal static int? WorkedMinutes(AttendanceRecord record)
    {
        if (AttendanceTime.TryParseHours(record.Attended, out var hours) && hours > 0)
        {
            // Invalid source values must not overflow the integer minute representation.
            if (hours <= int.MaxValue / 60m)
            {
                var reportedMinutes = (int)decimal.Round(hours * 60m, 0, MidpointRounding.AwayFromZero);
                if (reportedMinutes > 0) return reportedMinutes;
            }
        }

        return AttendanceTime.PunchDurationMinutes(record.InPunch, record.OutPunch);
    }

    /// <summary>Count repeated observations once and refuse contradictory employee/day records.</summary>
    internal static IReadOnlyList<AttendanceRecord> DistinctEmployeeDates(IEnumerable<AttendanceRecord> records)
    {
        var unique = new List<AttendanceRecord>();
        foreach (var group in records.GroupBy(record => (record.Id, record.Name, record.Date)))
        {
            var first = group.First();
            if (group.Skip(1).Any(record => !SameAttendance(first, record)))
                throw new InvalidDataException(
                    $"Conflicting attendance rows for employee '{first.Id}' ({first.Name}) on "
                    + $"{first.Date.ToString(TemplateSpec.DateFormat, CultureInfo.InvariantCulture)}. "
                    + "Export one attendance observation per employee and date, or resolve the duplicate rows.");
            unique.Add(first);
        }
        return unique;
    }

    private static bool SameAttendance(AttendanceRecord first, AttendanceRecord other) =>
        first.InPunch == other.InPunch
        && first.OutPunch == other.OutPunch
        && SameMetric(first.Attended, other.Attended)
        && SameMetric(first.Overtime, other.Overtime)
        && string.Equals(first.Status?.Trim(), other.Status?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SameMetric(string? first, string? other)
    {
        if (AttendanceTime.TryParseHours(first, out var firstHours)
            && AttendanceTime.TryParseHours(other, out var otherHours))
            return firstHours == otherHours;
        return string.Equals(first?.Trim(), other?.Trim(), StringComparison.Ordinal);
    }
}
