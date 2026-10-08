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
                    TotalHours: TotalHours(record.InPunch, record.OutPunch),
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
            // IDs with leading zeros (e.g. "0013") must stay text or the zeros are lost
            ws.Cell(row, 2).Value =
                long.TryParse(r.IdNo, out var id) && !KeepsLeadingZeros(r.IdNo)
                    ? XLCellValue.FromObject(id)
                    : r.IdNo;
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

    private static string? TotalHours(string? inPunch, string? outPunch)
    {
        if (inPunch == null || outPunch == null) return null;
        if (!TimeOnly.TryParseExact(inPunch, TemplateSpec.TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var tin)
            || !TimeOnly.TryParseExact(outPunch, TemplateSpec.TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var tout))
        {
            return null;
        }
        var minutes = (int)(tout - tin).TotalMinutes;
        if (minutes < 0) minutes += 24 * 60; // punches crossing midnight
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    private static bool KeepsLeadingZeros(string id) =>
        id.Length > 1 && id[0] == '0' && id.All(char.IsDigit);
}
