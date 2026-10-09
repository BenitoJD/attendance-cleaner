using System.Globalization;
using AttendanceCleaner.Core;
using ClosedXML.Excel;
using Xunit;

namespace AttendanceCleaner.Tests;

public sealed class MinutePrecisionTests
{
    private static readonly DateOnly Month = new(2026, 9, 1);

    [Theory]
    [InlineData("06:51", "19:00", "12", "12:09")]
    [InlineData("08:10", "16:45", "8", "08:35")]
    [InlineData("08:10", "16:45", "8.5", "08:35")]
    [InlineData("23:47", "00:13", "1", "00:26")]
    [InlineData("08:00", "08:00", "8", "00:00")]
    public void Daily_workbook_and_dashboard_use_punch_minutes(string start, string end, string attended, string expected)
    {
        var record = Record(1, start, end) with { Attended = attended };
        using var stream = new MemoryStream();
        TemplateWriter.Write([record], stream);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        Assert.Equal(expected, workbook.Worksheet(1).Cell(TemplateSpec.FirstDataRow, 9).GetString());
        var dashboard = AttendanceAnalytics.Build([record]);
        Assert.Equal(expected, dashboard.AverageHours);
        Assert.Equal(expected, Assert.Single(dashboard.Employees).AverageHours);
    }

    [Fact]
    public void Uploaded_daily_file_preserves_minutes_instead_of_rounded_attended_hours()
    {
        var report = AttendanceParser.Parse(TestReports.Daily());
        Assert.Equal("12", report.Records[0].Attended);
        using var stream = new MemoryStream();
        TemplateWriter.Write(report.Records, stream);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        Assert.Equal("12:09", workbook.Worksheet(1).Cell(TemplateSpec.FirstDataRow, 9).GetString());
    }

    [Theory]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime, 1)]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime, 2)]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime, 29)]
    [InlineData(MonthlyTemplateKind.InAndOut, 1)]
    [InlineData(MonthlyTemplateKind.InAndOut, 2)]
    [InlineData(MonthlyTemplateKind.InAndOut, 29)]
    public void Monthly_workbooks_sum_minutes_before_converting_to_decimal_hours(MonthlyTemplateKind kind, int extraMinutes)
    {
        var records = Enumerable.Range(1, 30).Select(day =>
            Record(day, "08:00", $"16:{extraMinutes:00}") with { Attended = "8" }).ToArray();
        var expectedMinutes = 30 * (480 + extraMinutes);
        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries(records, [], Month));
        Assert.Equal(expectedMinutes / 60m, summary.TotalHours);
        Assert.Equal(240m, summary.TotalDutyHours);
        Assert.Equal(30 * extraMinutes / 60m, summary.TotalOvertimeHours);

        using var stream = new MemoryStream();
        MonthlyTemplateWriter.Write(records, [], kind, stream);
        stream.Position = 0;
        using (var workbook = new XLWorkbook(stream))
        {
            var sheet = workbook.Worksheet(1);
            if (kind == MonthlyTemplateKind.DutyAndOvertime)
            {
                Assert.Equal(240m, sheet.Cell(6, 65).GetValue<decimal>());
                Assert.Equal(30 * extraMinutes / 60m, sheet.Cell(6, 66).GetValue<decimal>());
                // Excel stores the full fraction; the two-decimal display must not feed aggregation.
                Assert.InRange(Math.Abs(sheet.Cell(6, 6).GetDouble() * 60 - extraMinutes), 0, 0.0000001);
            }
            else
            {
                Assert.Equal(expectedMinutes / 60m, sheet.Cell(6, 65).GetValue<decimal>());
                Assert.Equal("08:00", sheet.Cell(6, 5).GetString());
                Assert.Equal($"16:{extraMinutes:00}", sheet.Cell(6, 6).GetString());
            }
        }
        stream.Position = 0;
        var preview = MonthlyTemplateWriter.ReadPreview(stream);
        var expectedText = (kind == MonthlyTemplateKind.InAndOut ? expectedMinutes / 60m : 240m)
            .ToString("0.00", CultureInfo.InvariantCulture);
        Assert.Contains(preview.Sheets[0].Cells, cell => cell.Row == 6 && cell.Column == 65 && cell.Text == expectedText);
    }

    [Theory]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime)]
    [InlineData(MonthlyTemplateKind.InAndOut)]
    public void Uploaded_monthly_report_preserves_minutes_and_overnight_attendance(MonthlyTemplateKind kind)
    {
        var report = AttendanceParser.Parse("""
            <html><body>
            <table><tr><td>From: 01-09-2026 To: 02-09-2026</td></tr></table>
            <table>
            <tr><td>Person ID</td><td>007</td><td>Employee Name</td><td>Example</td></tr>
            <tr><td>Date</td><td>1</td><td>2</td></tr>
            <tr><td>Check-in1</td><td>08:10</td><td>23:47</td></tr>
            <tr><td>Check-out1</td><td>16:45</td><td>00:13</td></tr>
            <tr><td>Attended</td><td>8</td><td>1</td></tr>
            <tr><td>OT</td><td>0</td><td>0</td></tr>
            </table></body></html>
            """);
        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries(report.Records, [], Month));
        Assert.Equal(541, decimal.Round(summary.TotalHours * 60));
        Assert.Equal(496, decimal.Round(summary.TotalDutyHours * 60));
        Assert.Equal(45, decimal.Round(summary.TotalOvertimeHours * 60));
        using var stream = new MemoryStream();
        MonthlyTemplateWriter.Write(report.Records, [], kind, stream);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(1);
        if (kind == MonthlyTemplateKind.InAndOut)
        {
            Assert.InRange(Math.Abs(sheet.Cell(6, 65).GetDouble() * 60 - 541), 0, 0.0000001);
            Assert.Equal("23:47", sheet.Cell(6, 7).GetString());
            Assert.Equal("00:13", sheet.Cell(6, 8).GetString());
        }
        else
        {
            Assert.InRange(Math.Abs(sheet.Cell(6, 65).GetDouble() * 60 - 496), 0, 0.0000001);
            Assert.Equal(0.75m, sheet.Cell(6, 66).GetValue<decimal>());
        }
    }

    [Fact]
    public void Monthly_elapsed_total_is_not_capped_by_payroll_limits()
    {
        var record = Record(1, "06:51", "19:00") with { Attended = "12" };
        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries([record], [], Month));
        Assert.Equal(12.15m, summary.TotalHours);
        Assert.Equal(9m, summary.TotalDutyHours);
        Assert.Equal(3m, summary.TotalOvertimeHours);
    }

    [Theory]
    [InlineData(null, "16:00", "1:35", "01:35")]
    [InlineData("08:00", null, "1.5", "01:30")]
    [InlineData(null, null, "0", null)]
    [InlineData("08:00", null, null, null)]
    public void Missing_punch_pairs_use_reported_duration_without_inventing_elapsed_time(
        string? start, string? end, string? attended, string? expected)
    {
        var record = Record(1, start, end) with { Attended = attended };
        Assert.Equal(expected, Assert.Single(TemplateWriter.BuildRows([record])).TotalHours);
    }

    [Fact]
    public void Every_minute_duration_is_preserved_across_daytime_and_midnight_start_times()
    {
        foreach (var startMinute in new[] { 0, 1, 479, 720, 1019, 1439 })
        {
            var start = new TimeOnly(startMinute / 60, startMinute % 60);
            for (var duration = 0; duration < 1440; duration++)
            {
                var end = start.AddMinutes(duration);
                var record = Record(1, start.ToString("HH:mm", CultureInfo.InvariantCulture),
                    end.ToString("HH:mm", CultureInfo.InvariantCulture)) with
                { Attended = "8" };
                var expected = $"{duration / 60:00}:{duration % 60:00}";
                Assert.Equal(expected, Assert.Single(TemplateWriter.BuildRows([record])).TotalHours);
            }
        }
    }

    [Theory]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime, 2026, 2)]
    [InlineData(MonthlyTemplateKind.InAndOut, 2026, 2)]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime, 2028, 2)]
    [InlineData(MonthlyTemplateKind.InAndOut, 2028, 2)]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime, 2026, 9)]
    [InlineData(MonthlyTemplateKind.InAndOut, 2026, 9)]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime, 2026, 10)]
    [InlineData(MonthlyTemplateKind.InAndOut, 2026, 10)]
    public void Mixed_minutes_and_employees_remain_exact_in_28_29_30_and_31_day_workbooks(
        MonthlyTemplateKind kind, int year, int month)
    {
        var days = DateTime.DaysInMonth(year, month);
        var records = Enumerable.Range(1, 3).SelectMany(employee =>
            Enumerable.Range(1, days).Select(day => Record(day, "08:00", $"16:{(day * 17 + employee * 7) % 60:00}") with
            {
                Id = employee.ToString(CultureInfo.InvariantCulture),
                Name = $"Employee {employee}",
                Date = new DateOnly(year, month, day),
                Order = employee,
                Attended = "8",
            })).ToArray();
        var summaries = MonthlyTemplateWriter.BuildSummaries(records, [], records[0].Date);
        using var stream = new MemoryStream();
        MonthlyTemplateWriter.Write(records, [], kind, stream);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(1);
        var totalColumn = 5 + days * 2;
        for (var employee = 1; employee <= 3; employee++)
        {
            var extraMinutes = Enumerable.Range(1, days).Sum(day => (day * 17 + employee * 7) % 60);
            var expectedTotal = days * 480 + extraMinutes;
            var summary = Assert.Single(summaries, item => item.Id == employee.ToString(CultureInfo.InvariantCulture));
            Assert.Equal(expectedTotal / 60m, summary.TotalHours);
            Assert.Equal(days * 8m, summary.TotalDutyHours);
            Assert.Equal(extraMinutes / 60m, summary.TotalOvertimeHours);
            var row = 5 + employee;
            var cell = sheet.Cell(row, kind == MonthlyTemplateKind.InAndOut ? totalColumn : totalColumn + 1);
            var expectedCellMinutes = kind == MonthlyTemplateKind.InAndOut ? expectedTotal : extraMinutes;
            Assert.InRange(Math.Abs(cell.GetDouble() * 60 - expectedCellMinutes), 0, 0.0000001);
        }
    }

    [Theory]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime)]
    [InlineData(MonthlyTemplateKind.InAndOut)]
    public void Alternate_monthly_rows_upload_does_not_override_punch_minutes(MonthlyTemplateKind kind)
    {
        var source = TestReports.MonthlyRows().Replace("<td>Attended</td><td>-</td>", "<td>Attended</td><td>12</td>");
        var report = AttendanceParser.Parse(source);
        Assert.Equal(AttendanceExportFormat.MonthlyRows, report.Format);
        Assert.Equal("12", report.Records[0].Attended);
        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries(report.Records, [], Month), item => item.Id == "3");
        Assert.Equal(12.15m, summary.TotalHours);
        using var stream = new MemoryStream();
        MonthlyTemplateWriter.Write(report.Records, [], kind, stream);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(1);
        if (kind == MonthlyTemplateKind.InAndOut)
            Assert.Equal(12.15m, sheet.Cell(6, 65).GetValue<decimal>());
        else
        {
            Assert.Equal(9m, sheet.Cell(6, 65).GetValue<decimal>());
            Assert.Equal(3m, sheet.Cell(6, 66).GetValue<decimal>());
        }
    }

    [Theory]
    [InlineData(null, "16:00", "1:35", 95)]
    [InlineData("08:00", null, "1.5", 90)]
    [InlineData(null, null, "1:35", 95)]
    [InlineData(null, null, "0", 0)]
    [InlineData("08:00", null, null, 0)]
    public void Monthly_missing_punch_pairs_preserve_reported_minutes_or_zero(
        string? start, string? end, string? attended, int expectedMinutes)
    {
        var record = Record(1, start, end) with { Attended = attended };
        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries([record], [], Month));
        Assert.Equal(expectedMinutes, decimal.Round(summary.TotalHours * 60));
        Assert.Equal(expectedMinutes, decimal.Round(summary.TotalDutyHours * 60));
        Assert.Equal(0m, summary.TotalOvertimeHours);
        foreach (var kind in Enum.GetValues<MonthlyTemplateKind>())
        {
            using var stream = new MemoryStream();
            MonthlyTemplateWriter.Write([record], [], kind, stream);
            stream.Position = 0;
            using var workbook = new XLWorkbook(stream);
            Assert.InRange(Math.Abs(workbook.Worksheet(1).Cell(6, 65).GetDouble() * 60 - expectedMinutes), 0, 0.0000001);
        }
    }

    private static AttendanceRecord Record(int day, string? start, string? end) =>
        new("007", "Example", "Male", Month.AddDays(day - 1), start, end, 0);
}
