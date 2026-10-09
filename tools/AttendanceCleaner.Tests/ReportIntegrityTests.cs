using System.Text;
using System.Text.RegularExpressions;
using AttendanceCleaner.Core;
using Xunit;

namespace AttendanceCleaner.Tests;

public sealed class ReportIntegrityTests
{
    private static readonly DateOnly Month = new(2026, 9, 1);

    [Theory]
    [InlineData("12", "12:09")]
    [InlineData("11:30", "12:09")]
    [InlineData("79228162514264337593543950335", "12:09")]
    public void Daily_and_dashboard_preserve_punch_minutes_despite_source_duration(string attended, string expected)
    {
        var record = Record() with { InPunch = "06:51", OutPunch = "19:00", Attended = attended };
        var rows = TemplateWriter.BuildRows([record]);
        var dashboard = AttendanceAnalytics.Build([record]);

        Assert.Equal(expected, Assert.Single(rows).TotalHours);
        Assert.Equal(expected, dashboard.AverageHours);
        Assert.Equal(expected, Assert.Single(dashboard.Employees).AverageHours);
    }

    [Fact]
    public void Monthly_duration_overflow_falls_back_to_punches_and_preserves_payroll_caps()
    {
        var record = Record() with { Attended = "79228162514264337593543950335" };
        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries([record], [], Month));
        Assert.Equal(9m, summary.TotalHours);

        var longShift = record with { InPunch = "05:00", OutPunch = "20:00" };
        var capped = Assert.Single(MonthlyTemplateWriter.BuildSummaries([longShift], [], Month));
        Assert.Equal(9m, capped.TotalDutyHours);
        Assert.Equal(3m, capped.TotalOvertimeHours);
        Assert.Equal(15m, capped.TotalHours);
    }

    [Theory]
    [InlineData("2", "3", "07:00", "09:00", 2)]
    [InlineData("0.5", "3", "18:00", "18:30", 0.5)]
    [InlineData("1", "0", "16:00", "19:00", 3)]
    public void Overtime_cannot_create_more_hours_than_the_punch_duration(
        string attended, string overtime, string start, string end, double expected)
    {
        var record = Record() with
        {
            InPunch = start,
            OutPunch = end,
            Attended = attended,
            Overtime = overtime,
        };
        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries([record], [], Month));
        Assert.Equal((decimal)expected, summary.TotalHours);
        Assert.InRange(summary.TotalOvertimeHours, 0m, summary.TotalHours);
    }

    [Fact]
    public void Overnight_departures_average_across_midnight()
    {
        var records = new[]
        {
            Record() with { InPunch = "18:00", OutPunch = "23:50" },
            Record() with { Date = Month.AddDays(1), InPunch = "18:00", OutPunch = "00:10" },
        };
        var dashboard = AttendanceAnalytics.Build(records);
        Assert.Equal("00:00", dashboard.AverageOut);
        Assert.Equal("00:00", Assert.Single(dashboard.Employees).AverageOut);
        Assert.Equal("06:00", dashboard.AverageHours);
    }

    [Fact]
    public void Check_ins_and_unpaired_departures_average_across_midnight()
    {
        var records = new[]
        {
            Record() with { InPunch = "23:50", OutPunch = null },
            Record() with { Date = Month.AddDays(1), InPunch = "00:10", OutPunch = null },
        };
        Assert.Equal("00:00", AttendanceAnalytics.Build(records).AverageIn);
        Assert.Equal("00:00", AttendanceAnalytics.Build(records.Select(record => record with
        {
            OutPunch = record.InPunch,
            InPunch = null,
        })).AverageOut);
    }

    [Fact]
    public void Overnight_and_after_midnight_shifts_share_the_departure_clock_interval()
    {
        var records = new[]
        {
            Record() with { InPunch = "18:00", OutPunch = "00:10" },
            Record() with { Date = Month.AddDays(1), InPunch = "00:00", OutPunch = "00:30" },
        };
        Assert.Equal("00:20", AttendanceAnalytics.Build(records).AverageOut);
    }

    [Fact]
    public void Repeated_identical_employee_days_count_once_in_summaries_and_preserve_daily_rows()
    {
        var record = Record();
        var records = new[] { record, record with { Order = 99 } };
        Assert.Equal(2, TemplateWriter.BuildRows(records).Count);
        var dashboard = AttendanceAnalytics.Build(records);
        Assert.Equal(1, dashboard.TotalRows);
        Assert.Equal(1, dashboard.Present);
        Assert.Equal(1, Assert.Single(dashboard.Employees).PresentDays);
        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries(records, [], Month));
        Assert.Equal(1m, summary.PresentDays);
        Assert.Equal(9m, summary.TotalHours);
    }

    [Fact]
    public void Conflicting_duplicate_daily_import_is_rejected_by_summaries_with_employee_and_date()
    {
        var report = AttendanceParser.Parse(TestReports.DailyFlexible(
            new("1", "007", "Example", "01-09-2026", "07:00", "16:00"),
            new("2", "007", "Example", "01-09-2026", "16:00", "19:00")));
        Assert.Equal(2, report.Records.Count);
        Assert.Equal(2, TemplateWriter.BuildRows(report.Records).Count);

        AssertDuplicateError(() => AttendanceAnalytics.Build(report.Records));
        AssertDuplicateError(() => MonthlyTemplateWriter.BuildSummaries(report.Records, [], Month));
        using var output = new MemoryStream();
        AssertDuplicateError(() => MonthlyTemplateWriter.Write(report.Records, [], MonthlyTemplateKind.InAndOut, output));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void Conflicting_status_or_reported_duration_is_not_discarded()
    {
        var first = Record();
        foreach (var other in new[]
        {
            first with { Status = "A" },
            first with { Attended = "2" },
            first with { Overtime = "3" },
        })
        {
            AssertDuplicateError(() => AttendanceAnalytics.Build([first, other]));
            AssertDuplicateError(() => MonthlyTemplateWriter.BuildSummaries([first, other], [], Month));
        }
    }

    [Fact]
    public void Invalid_direct_calendar_is_rejected_before_monthly_output_is_written()
    {
        var sunday = HolidayEntry.Create(new DateOnly(2026, 9, 6), "Invalid override", MonthlyTemplateSpec.WorkingSaturdayCategory);
        Assert.Throws<InvalidDataException>(() => MonthlyTemplateWriter.BuildSummaries([Record()], [sunday], Month));
        using var output = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => MonthlyTemplateWriter.Write([Record()], [sunday], MonthlyTemplateKind.InAndOut, output));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void Balanced_one_day_chart_and_employee_table_fit_on_one_pdf_page()
    {
        var records = new[] { Record(), Record() with { Id = "008", Name = "Absent", InPunch = null, OutPunch = null } };
        var pdf = DashboardPdf.Build(AttendanceAnalytics.Build(records), "balanced.xls");
        Assert.Equal(1, PdfPageCount(pdf));
    }

    [Fact]
    public void Year_of_daily_attendance_has_bounded_pdf_panels_and_generates_successfully()
    {
        var records = Enumerable.Range(0, 365).SelectMany(day => new[]
        {
            Record() with { Date = Month.AddDays(day) },
            Record() with { Id = "008", Name = "Absent", Date = Month.AddDays(day), InPunch = null, OutPunch = null },
        });
        var pdf = DashboardPdf.Build(AttendanceAnalytics.Build(records), "year.xls");
        Assert.InRange(PdfPageCount(pdf), 4, 8);
    }

    [Fact]
    public void Empty_public_dashboard_can_render_a_pdf_without_a_chart()
    {
        var dashboard = new Dashboard(0, 0, Month, Month, 0, 0, 0, 0, 0, null, null, null, [], []);
        Assert.Equal(1, PdfPageCount(DashboardPdf.Build(dashboard, "empty.xls")));
    }

    private static AttendanceRecord Record() => new("007", "Example", "-", Month, "07:00", "16:00", 0, Status: "P");

    private static void AssertDuplicateError(Action action)
    {
        var error = Assert.Throws<InvalidDataException>(action);
        Assert.Contains("007", error.Message);
        Assert.Contains("01-09-2026", error.Message);
    }

    private static int PdfPageCount(byte[] pdf)
    {
        // QuestPDF writes the page-tree count as an uncompressed PDF dictionary entry.
        var counts = Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Count\s+(\d+)\b");
        return int.Parse(Assert.Single(counts.Cast<Match>()).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
