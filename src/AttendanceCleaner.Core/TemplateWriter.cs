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
        foreach (var group in DistinctEmployeeDates(records).GroupBy(r => r.Date).OrderBy(g => g.Key))
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
            var header = ws.Cell(TemplateSpec.HeaderRow, c + 1);
            header.Value = TemplateSpec.Headers[c];
            header.Style.Font.Bold = true;
        }
        ws.SheetView.FreezeRows(TemplateSpec.HeaderRow);

        var row = TemplateSpec.FirstDataRow;
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
            ws.Cell(row, 10).Style.Fill.BackgroundColor = XLColor.FromHtml(r.Remark switch
            {
                TemplateSpec.RemarkPresent => "#E2EFDA",
                TemplateSpec.RemarkAbsent => "#B4C7E7",
                _ => "#FCE4D6",
            });
            row++;
        }

        ws.Columns().AdjustToContents();
        foreach (var (column, width) in TemplateSpec.ColumnWidths)
        {
            ws.Column(column).Width = width;
        }

        // Match the monthly workbook palette without changing the daily layout or values.
        var headerColors = new[]
        {
            "#FFF2CC", "#FFF2CC", "#FFF2CC", "#FFF2CC", "#E2EFDA",
            "#DDEBF7", "#FFF2CC", "#FCE4D6", "#FFF2CC", "#B4C7E7",
        };
        for (var column = 1; column <= headerColors.Length; column++)
            ws.Cell(TemplateSpec.HeaderRow, column).Style.Fill.BackgroundColor = XLColor.FromHtml(headerColors[column - 1]);
        ws.Range(TemplateSpec.HeaderRow, 1, row - 1, TemplateSpec.Headers.Length).Style.Font.FontColor = XLColor.Black;
        if (row > TemplateSpec.FirstDataRow)
        {
            ws.Range(TemplateSpec.FirstDataRow, 7, row - 1, 7).Style.Fill.BackgroundColor = XLColor.FromHtml("#E2EFDA");
            ws.Range(TemplateSpec.FirstDataRow, 8, row - 1, 8).Style.Fill.BackgroundColor = XLColor.FromHtml("#DDEBF7");
            ws.Range(TemplateSpec.FirstDataRow, 9, row - 1, 9).Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF2CC");
        }

        // filter dropdown on every column, covering exactly the header and the data rows
        if (row > TemplateSpec.FirstDataRow)
        {
            ws.Range(TemplateSpec.HeaderRow, 1, row - 1, TemplateSpec.Headers.Length).SetAutoFilter();
        }

        // Add branding after auto-fitting so the banner does not widen the attendance columns.
        var banner = ws.Range(1, 2, 1, TemplateSpec.Headers.Length).Merge();
        banner.Value = $"{MonthlyTemplateSpec.CompanyName} - Daily Attendance Report";
        banner.Style.Fill.BackgroundColor = XLColor.FromHtml("#FFFF00");
        banner.Style.Font.FontColor = XLColor.Black;
        banner.Style.Font.Bold = true;
        banner.Style.Font.FontSize = 12;
        banner.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Cell(1, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#E2EFDA");
        ws.Row(1).Height = 30;
        using (var logo = typeof(TemplateWriter).Assembly.GetManifestResourceStream(
            "AttendanceCleaner.Core.DakshinakTemplateLogo.png"))
        {
            if (logo is not null)
                ws.AddPicture(logo).MoveTo(ws.Cell(1, 1)).WithSize(47, 30);
        }

        workbook.SaveAs(output);
    }

    public static void WriteToFile(IEnumerable<AttendanceRecord> records, string path)
    {
        ReportFileWriter.Write(path, stream => Write(records, stream));
    }

    private static string Remark(AttendanceRecord record) =>
        record.InPunch != null && record.OutPunch != null ? TemplateSpec.RemarkPresent
        : record.InPunch != null || record.OutPunch != null ? TemplateSpec.RemarkInPunchOnly
        : TemplateSpec.RemarkAbsent;

    private static string? TotalHours(AttendanceRecord record) =>
        WorkedMinutes(record) is { } minutes ? AttendanceTime.FormatMinutes(minutes) : null;

    /// <summary>Prefer elapsed punch time; use reported attendance only when a punch pair is unavailable.</summary>
    internal static int? WorkedMinutes(AttendanceRecord record)
    {
        if (AttendanceTime.PunchDurationMinutes(record.InPunch, record.OutPunch) is { } elapsed)
            return elapsed;

        return AttendanceTime.TryParseWorkedMinutes(record.Attended, out var minutes) ? minutes : null;
    }

    /// <summary>Use stable employee IDs, reconcile harmless name variations, and count each employee/date once.</summary>
    internal static IReadOnlyList<AttendanceRecord> DistinctEmployeeDates(IEnumerable<AttendanceRecord> records)
    {
        var namesById = new Dictionary<string, string>(StringComparer.Ordinal);
        var normalized = new List<AttendanceRecord>();
        foreach (var record in records)
        {
            var name = string.Join(" ", record.Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (namesById.TryGetValue(record.Id, out var existingName))
            {
                if (!string.Equals(existingName, name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        $"Conflicting employee names for ID '{record.Id}': '{existingName}' and '{name}'. "
                        + "Verify the employee identity in the source report.");
                name = existingName;
            }
            else namesById.Add(record.Id, name);
            normalized.Add(record with { Name = name });
        }

        var unique = new List<AttendanceRecord>();
        foreach (var group in normalized.GroupBy(record => (record.Id, record.Date)))
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
        SameGender(first.Gender, other.Gender)
        && first.InPunch == other.InPunch
        && first.OutPunch == other.OutPunch
        && SameMetric(first.Attended, other.Attended)
        && SameMetric(first.Overtime, other.Overtime)
        && string.Equals(first.Status?.Trim(), other.Status?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SameGender(string? first, string? other)
    {
        static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) || value.Trim() == "-"
            ? "" : value.Trim();
        return string.Equals(Normalize(first), Normalize(other), StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameMetric(string? first, string? other)
    {
        if (AttendanceTime.TryParseHours(first, out var firstHours)
            && AttendanceTime.TryParseHours(other, out var otherHours))
            return firstHours == otherHours;
        return string.Equals(first?.Trim(), other?.Trim(), StringComparison.Ordinal);
    }
}
