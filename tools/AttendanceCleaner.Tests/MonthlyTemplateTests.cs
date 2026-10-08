using AttendanceCleaner.Core;
using ClosedXML.Excel;
using Xunit;

namespace AttendanceCleaner.Tests;

public sealed class MonthlyTemplateTests
{
    private static readonly DateOnly Month = new(2026, 9, 1);

    [Fact]
    public void Monthly_block_parser_keeps_attended_overtime_and_status_values()
    {
        var html = """
            <html><body>
            <table><tr><td>From: 01-09-2026 To: 03-09-2026</td></tr></table>
            <table>
            <tr><td>Person ID</td><td>7</td><td>Employee Name</td><td>Example Employee</td></tr>
            <tr><td>Date</td><td>1</td><td>2</td><td>3</td></tr>
            <tr><td>Check-in1</td><td>06:51</td><td>-</td><td>-</td></tr>
            <tr><td>Check-out1</td><td>19:00</td><td>-</td><td>-</td></tr>
            <tr><td>OT</td><td>2.5</td><td>0</td><td>0</td></tr>
            <tr><td>Attended</td><td>11.5</td><td>0</td><td>0</td></tr>
            <tr><td>Status</td><td>P-#</td><td>A-#</td><td>W</td></tr>
            </table></body></html>
            """;

        var report = AttendanceParser.Parse(html);

        Assert.Equal("11.5", report.Records[0].Attended);
        Assert.Equal("2.5", report.Records[0].Overtime);
        Assert.Equal("P-#", report.Records[0].Status);
        Assert.Equal("A-#", report.Records[1].Status);
        Assert.Equal("W", report.Records[2].Status);
    }

    [Fact]
    public void Duty_overtime_template_omits_designation_and_includes_holiday_sheet()
    {
        var records = SampleRecords();
        var holidays = HolidayCalendarStore.Seed2026();
        using var stream = new MemoryStream();
        Assert.NotNull(typeof(MonthlyTemplateWriter).Assembly.GetManifestResourceStream(
            "AttendanceCleaner.Core.DakshinakTemplateLogo.png"));

        MonthlyTemplateWriter.Write(records, holidays, MonthlyTemplateKind.DutyAndOvertime, stream);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var attendance = workbook.Worksheet("Attendance");

        Assert.Contains("DAKSHINAK MINERAL", attendance.Cell(1, 2).GetString());
        Assert.Equal("Staffs", attendance.Cell(2, 1).GetString());
        Assert.Single(attendance.Pictures);
        Assert.Equal("Name", attendance.Cell(3, 2).GetString());
        Assert.Equal("Roll.No", attendance.Cell(3, 3).GetString());
        Assert.Equal("Days", attendance.Cell(3, 4).GetString());
        Assert.DoesNotContain("Designation", attendance.RangeUsed()!.Cells().Select(cell => cell.GetString()));
        Assert.Equal("Duty", attendance.Cell(5, 5).GetString());
        Assert.Equal("OT", attendance.Cell(5, 6).GetString());
        Assert.Equal(9m, attendance.Cell(6, 5).GetValue<decimal>());
        Assert.Equal(3m, attendance.Cell(6, 6).GetValue<decimal>());
        Assert.Equal("Holiday Details", workbook.Worksheet("Holiday Details").Name);
        Assert.Equal(32, workbook.Worksheet("Holiday Details").LastRowUsed()!.RowNumber() - 3);
    }

    [Fact]
    public void In_out_template_keeps_punches_and_has_the_second_monthly_layout()
    {
        var records = SampleRecords();
        using var stream = new MemoryStream();

        MonthlyTemplateWriter.Write(records, HolidayCalendarStore.Seed2026(), MonthlyTemplateKind.InAndOut, stream);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var attendance = workbook.Worksheet("Attendance");

        Assert.Equal("IN", attendance.Cell(5, 5).GetString());
        Assert.Equal("OUT", attendance.Cell(5, 6).GetString());
        Assert.Equal("06:51", attendance.Cell(6, 5).GetString());
        Assert.Equal("19:00", attendance.Cell(6, 6).GetString());
        Assert.Equal("Total Hours", attendance.Cell(3, 65).GetString());
        Assert.Equal("No.Of.Days", attendance.Cell(3, 67).GetString());
        Assert.Equal("Present", attendance.Cell(5, 67).GetString());
    }

    [Fact]
    public void Summary_splits_a_0700_to_1900_day_into_nine_duty_and_three_ot_hours()
    {
        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries(
            SampleRecords(), Array.Empty<HolidayEntry>(), Month));

        Assert.Equal(1, summary.PresentDays);
        Assert.Equal(9m, summary.TotalDutyHours);
        Assert.Equal(3m, summary.TotalOvertimeHours);
        Assert.Equal(12m, summary.TotalHours);
    }

    [Fact]
    public void Mandatory_holiday_without_attendance_is_credited_as_nine_hours()
    {
        var records = new[]
        {
            new AttendanceRecord("007", "Example Employee", "-", new DateOnly(2026, 9, 14), null, null, 0, Status: "A-#"),
        };
        var holidays = HolidayCalendarStore.Seed2026();
        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries(records, holidays, Month));

        Assert.Equal(1, summary.PresentDays);
        Assert.Equal(9m, summary.TotalDutyHours);
        Assert.Equal(0m, summary.TotalOvertimeHours);
        Assert.Equal(9m, summary.TotalHours);

        using var stream = new MemoryStream();
        MonthlyTemplateWriter.Write(records, holidays, MonthlyTemplateKind.DutyAndOvertime, stream);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var attendance = workbook.Worksheet("Attendance");
        const int september14FirstColumn = 31;
        Assert.Equal(9m, attendance.Cell(6, september14FirstColumn).GetValue<decimal>());
        Assert.Equal(0m, attendance.Cell(6, september14FirstColumn + 1).GetValue<decimal>());
        Assert.Equal(9m, attendance.Cell(6, 65).GetValue<decimal>());
        Assert.Equal(1, attendance.Cell(6, 67).GetValue<int>());
    }

    [Fact]
    public void In_out_holiday_without_attendance_uses_a_nine_hour_shift()
    {
        var records = new[]
        {
            new AttendanceRecord("007", "Example Employee", "-", new DateOnly(2026, 9, 14), null, null, 0, Status: "A-#"),
        };
        using var stream = new MemoryStream();

        MonthlyTemplateWriter.Write(records, HolidayCalendarStore.Seed2026(), MonthlyTemplateKind.InAndOut, stream);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var attendance = workbook.Worksheet("Attendance");
        const int september14FirstColumn = 31;

        Assert.Equal("07:00", attendance.Cell(6, september14FirstColumn).GetString());
        Assert.Equal("16:00", attendance.Cell(6, september14FirstColumn + 1).GetString());
        Assert.Equal(9m, attendance.Cell(6, 65).GetValue<decimal>());
        Assert.Equal(1, attendance.Cell(6, 67).GetValue<int>());
    }

    [Fact]
    public void Mandatory_holiday_uses_fixed_nine_hour_credit_even_with_source_punches()
    {
        var records = new[]
        {
            new AttendanceRecord("007", "Example Employee", "-", Month, null, null, 0, Status: "A-#"),
            new AttendanceRecord("007", "Example Employee", "-", new DateOnly(2026, 9, 14),
                "06:45", "19:03", 1, Status: "P"),
        };
        var holidays = HolidayCalendarStore.Seed2026();
        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries(records, holidays, Month));

        Assert.Equal(1, summary.PresentDays);
        Assert.Equal(9m, summary.TotalDutyHours);
        Assert.Equal(0m, summary.TotalOvertimeHours);
        Assert.Equal(9m, summary.TotalHours);

        using var stream = new MemoryStream();
        MonthlyTemplateWriter.Write(records, holidays, MonthlyTemplateKind.DutyAndOvertime, stream);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var attendance = workbook.Worksheet("Attendance");
        const int september14FirstColumn = 31;
        Assert.Equal(9m, attendance.Cell(6, september14FirstColumn).GetValue<decimal>());
        Assert.Equal(0m, attendance.Cell(6, september14FirstColumn + 1).GetValue<decimal>());
    }

    [Fact]
    public void In_out_holiday_uses_fixed_nine_hour_shift_even_with_source_punches()
    {
        var records = new[]
        {
            new AttendanceRecord("007", "Example Employee", "-", Month, null, null, 0, Status: "A-#"),
            new AttendanceRecord("007", "Example Employee", "-", new DateOnly(2026, 9, 14),
                "06:45", "19:03", 1, Status: "P"),
        };
        using var stream = new MemoryStream();

        MonthlyTemplateWriter.Write(records, HolidayCalendarStore.Seed2026(), MonthlyTemplateKind.InAndOut, stream);
        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var attendance = workbook.Worksheet("Attendance");
        const int september14FirstColumn = 31;

        Assert.Equal("07:00", attendance.Cell(6, september14FirstColumn).GetString());
        Assert.Equal("16:00", attendance.Cell(6, september14FirstColumn + 1).GetString());
        Assert.Equal(9m, attendance.Cell(6, 65).GetValue<decimal>());
        Assert.Equal(1, attendance.Cell(6, 67).GetValue<int>());
    }

    [Fact]
    public void Optional_holiday_without_attendance_is_not_auto_credited()
    {
        var records = new[]
        {
            new AttendanceRecord("007", "Example Employee", "-", new DateOnly(2026, 9, 14), null, null, 0, Status: "A-#"),
        };
        var optionalHoliday = HolidayEntry.Create(
            new DateOnly(2026, 9, 14), "Optional holiday", "Optional · Corporate Office");

        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries(
            records, new[] { optionalHoliday }, Month));

        Assert.Equal(0m, summary.TotalHours);
        Assert.Equal(0, summary.PresentDays);
    }

    [Fact]
    public void Seeded_holidays_survive_edit_and_reload()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "holidays.json");
        var store = new HolidayCalendarStore(path);
        try
        {
            var entries = store.LoadOrSeed().ToList();
            Assert.Equal(32, entries.Count);
            var index = entries.FindIndex(entry => entry.Name == "New Year Day" && entry.Category == "Tamil Nadu");
            Assert.True(index >= 0);
            entries[index] = entries[index] with { Name = "Edited New Year" };
            store.Save(entries);

            var reloaded = new HolidayCalendarStore(path).LoadOrSeed();
            Assert.Contains(reloaded, entry => entry.Name == "Edited New Year");
        }
        finally
        {
            var folder = Path.GetDirectoryName(path)!;
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Monthly_rows_aggregate_totals_are_used_and_holiday_credit_reclassifies_absence()
    {
        var report = AttendanceParser.Parse(TestReports.MonthlyRowsWithTotals());
        var holiday = HolidayEntry.Create(new DateOnly(2026, 9, 14), "Founder's Day", "Corporate Office");
        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries(
            report.Records, new[] { holiday }, new DateOnly(2026, 9, 1), report.EmployeeTotals));

        Assert.Equal(1m, summary.PresentDays);
        Assert.Equal(21m, summary.AbsentDays);
        Assert.Equal(11.5m, summary.LeaveDays);
    }

    private static AttendanceRecord[] SampleRecords() =>
    [
        new AttendanceRecord("007", "Example Employee", "-", Month, "06:51", "19:00", 0,
            Attended: "12", Overtime: "0", Status: "P-#"),
    ];
}
