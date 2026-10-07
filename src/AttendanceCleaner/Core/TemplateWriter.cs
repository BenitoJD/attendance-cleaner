using System.Globalization;
using ClosedXML.Excel;

namespace AttendanceCleaner.Core;

/// <summary>
/// Writes parsed attendance records into the template defined by <see cref="TemplateSpec"/>.
/// </summary>
public static class TemplateWriter
{
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
        foreach (var group in records.GroupBy(r => r.Date).OrderBy(g => g.Key))
        {
            var slNo = 1;
            foreach (var record in group.OrderBy(r => r.Order))
            {
                WriteRow(ws, row++, slNo++, record);
            }
        }

        ws.Columns().AdjustToContents();
        foreach (var (column, width) in TemplateSpec.ColumnWidths)
        {
            ws.Column(column).Width = width;
        }

        workbook.SaveAs(output);
    }

    public static void WriteToFile(IEnumerable<AttendanceRecord> records, string path)
    {
        using var stream = File.Create(path);
        Write(records, stream);
    }

    private static void WriteRow(IXLWorksheet ws, int row, int slNo, AttendanceRecord record)
    {
        ws.Cell(row, 1).Value = slNo;
        ws.Cell(row, 2).Value =
            long.TryParse(record.Id, out var id) ? XLCellValue.FromObject(id) : record.Id;
        ws.Cell(row, 3).Value = record.Name;
        ws.Cell(row, 4).Value = string.IsNullOrEmpty(record.Gender) ? TemplateSpec.UnknownGender : record.Gender;
        ws.Cell(row, 5).Value = record.Date.ToString(TemplateSpec.DateFormat, CultureInfo.InvariantCulture);
        ws.Cell(row, 6).Value = record.Date.ToString("ddd", CultureInfo.InvariantCulture);
        if (record.InPunch != null) ws.Cell(row, 7).Value = record.InPunch;
        if (record.OutPunch != null) ws.Cell(row, 8).Value = record.OutPunch;
        var total = TotalHours(record.InPunch, record.OutPunch);
        if (total != null) ws.Cell(row, 9).Value = total;
        ws.Cell(row, 10).Value = Remark(record);
    }

    private static string Remark(AttendanceRecord record) =>
        record.InPunch != null && record.OutPunch != null ? TemplateSpec.RemarkPresent
        : record.InPunch != null || record.OutPunch != null ? TemplateSpec.RemarkInPunchOnly
        : TemplateSpec.RemarkAbsent;

    private static string? TotalHours(string? inPunch, string? outPunch)
    {
        if (inPunch == null || outPunch == null) return null;
        var tin = TimeOnly.ParseExact(inPunch, TemplateSpec.TimeFormat, CultureInfo.InvariantCulture);
        var tout = TimeOnly.ParseExact(outPunch, TemplateSpec.TimeFormat, CultureInfo.InvariantCulture);
        var minutes = (int)(tout - tin).TotalMinutes;
        if (minutes < 0) minutes += 24 * 60; // punches crossing midnight
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }
}
