using System.Globalization;
using ClosedXML.Excel;

namespace AttendanceCleaner.Core;

/// <summary>
/// Writes parsed attendance records into the "Attendance" template:
/// Sl.No | ID No | Name | Genter | Date | Day | In punch | Out punch | Total hours | Remarks.
/// </summary>
public static class TemplateWriter
{
    private static readonly string[] Headers =
    {
        "Sl.No", "ID No", "Name", "Genter", "Date", "Day", "In punch", "Out punch", "Total hours", "Remarks",
    };

    public static void Write(IEnumerable<AttendanceRecord> records, Stream output)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Attendance");

        for (var c = 0; c < Headers.Length; c++)
        {
            var header = ws.Cell(1, c + 1);
            header.Value = Headers[c];
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
        ws.Column(1).Width = 8;
        ws.Column(2).Width = 10;
        ws.Column(3).Width = 26;
        ws.Column(4).Width = 10;
        ws.Column(5).Width = 14;
        ws.Column(6).Width = 10;
        ws.Column(7).Width = 12;
        ws.Column(9).Width = 13;
        ws.Column(10).Width = 18;

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
        ws.Cell(row, 4).Value = string.IsNullOrEmpty(record.Gender) ? "-" : record.Gender;
        ws.Cell(row, 5).Value = record.Date.ToString("dd-MM-yyyy");
        ws.Cell(row, 6).Value = DayName(record.Date);
        if (record.InPunch != null) ws.Cell(row, 7).Value = record.InPunch;
        if (record.OutPunch != null) ws.Cell(row, 8).Value = record.OutPunch;
        var total = TotalHours(record.InPunch, record.OutPunch);
        if (total != null) ws.Cell(row, 9).Value = total;
        ws.Cell(row, 10).Value = Remark(record);
    }

    private static string DayName(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Monday => "Mon",
        DayOfWeek.Tuesday => "Tue",
        DayOfWeek.Wednesday => "Wed",
        DayOfWeek.Thursday => "Thu",
        DayOfWeek.Friday => "Fri",
        DayOfWeek.Saturday => "Sat",
        _ => "Sun",
    };

    private static string Remark(AttendanceRecord record) =>
        record.InPunch != null && record.OutPunch != null ? "Present"
        : record.InPunch != null || record.OutPunch != null ? "In punch only"
        : "Absent";

    private static string? TotalHours(string? inPunch, string? outPunch)
    {
        if (inPunch == null || outPunch == null) return null;
        var tin = TimeOnly.ParseExact(inPunch, "HH:mm", CultureInfo.InvariantCulture);
        var tout = TimeOnly.ParseExact(outPunch, "HH:mm", CultureInfo.InvariantCulture);
        var minutes = (int)(tout - tin).TotalMinutes;
        if (minutes < 0) minutes += 24 * 60; // punches crossing midnight
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }
}
