using AttendanceCleaner.Core;
using ClosedXML.Excel;
using Xunit;

namespace AttendanceCleaner.Tests;

public sealed class MetricEdgeCaseTests
{
    private static readonly DateOnly Date = new(2026, 10, 5);

    [Theory]
    [InlineData("9", "09:00")]
    [InlineData(" 9.5 ", "09:30")]
    [InlineData("+9.5", "09:30")]
    [InlineData(".5", "00:30")]
    [InlineData("9:30", "09:30")]
    [InlineData("09:30:00", "09:30")]
    [InlineData("09:30:30", "09:31")]
    [InlineData("09:30:29", "09:30")]
    [InlineData("09:30:31", "09:31")]
    [InlineData("0:01:30", "00:02")]
    [InlineData("0:00:29", "00:00")]
    [InlineData("0:00:30", "00:01")]
    [InlineData("0.001", "00:00")]
    [InlineData("23:59:30", "24:00")]
    [InlineData("25:00", "25:00")]
    public void Decimal_and_hour_minute_durations_share_daily_and_dashboard_totals(string attended, string expected)
    {
        var record = Record() with { Attended = attended };
        Assert.Equal(expected, Assert.Single(TemplateWriter.BuildRows([record])).TotalHours);
        Assert.Equal(expected, AttendanceAnalytics.Build([record]).AverageHours);
    }

    [Theory]
    [InlineData("1,5")]
    [InlineData("1,000")]
    [InlineData("1.00:00")]
    [InlineData("9:60")]
    [InlineData("9:30:60")]
    [InlineData("9:3")]
    [InlineData("9:30:")]
    [InlineData("9:30:00:00")]
    [InlineData("79228162514264337593543950335:59")]
    public void Direct_records_never_misinterpret_ambiguous_or_malformed_duration_strings(string attended)
    {
        // The parser rejects invalid source metrics; direct callers retain the punch fallback.
        var record = Record() with { Attended = attended };
        Assert.Equal("09:00", Assert.Single(TemplateWriter.BuildRows([record])).TotalHours);
        Assert.Equal("09:00", AttendanceAnalytics.Build([record]).AverageHours);
    }

    public static IEnumerable<object[]> DecadeMonthBoundaries()
    {
        foreach (var year in CalendarValidationRange.Years)
            for (var month = 1; month <= 12; month++)
            {
                yield return [year, month, 1];
                yield return [year, month, DateTime.DaysInMonth(year, month)];
            }
    }

    [Theory]
    [MemberData(nameof(DecadeMonthBoundaries))]
    public void Overnight_hours_match_across_daily_dashboard_and_monthly_reports_at_month_boundaries(
        int year, int month, int day)
    {
        var record = Record() with
        {
            Date = new DateOnly(year, month, day),
            InPunch = "23:45",
            OutPunch = "00:15",
        };
        Assert.Equal("00:30", Assert.Single(TemplateWriter.BuildRows([record])).TotalHours);
        Assert.Equal("00:30", AttendanceAnalytics.Build([record]).AverageHours);
        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries([record], [], record.Date));
        Assert.Equal(0.5m, summary.TotalDutyHours);
        Assert.Equal(0m, summary.TotalOvertimeHours);
        Assert.Equal(0.5m, summary.TotalHours);
        Assert.Equal(1m, summary.PresentDays);
    }

    [Fact]
    public void Overnight_shift_on_the_last_supported_date_does_not_overflow()
    {
        var record = Record() with
        {
            Date = DateOnly.MaxValue,
            InPunch = "23:30",
            OutPunch = "00:30",
        };
        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries([record], [], record.Date));
        Assert.Equal(1m, summary.TotalDutyHours);
        Assert.Equal(0m, summary.TotalOvertimeHours);
        Assert.Equal(1m, summary.TotalHours);
        Assert.Equal("01:00", AttendanceAnalytics.Build([record]).AverageHours);
    }

    [Theory]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime)]
    [InlineData(MonthlyTemplateKind.InAndOut)]
    public void Both_monthly_workbooks_export_an_overnight_shift_on_the_last_supported_date(MonthlyTemplateKind kind)
    {
        var record = Record() with
        {
            Date = DateOnly.MaxValue,
            InPunch = "23:30",
            OutPunch = "00:30",
        };
        using var output = new MemoryStream();
        MonthlyTemplateWriter.Write([record], [], kind, output);
        output.Position = 0;
        using var workbook = new XLWorkbook(output);
        var worksheet = workbook.Worksheet(TemplateSpec.SheetName);
        Assert.Contains("December-9999", worksheet.Cell(1, 2).GetString());
        if (kind == MonthlyTemplateKind.DutyAndOvertime)
        {
            Assert.Equal(1m, worksheet.Cell(6, 65).GetValue<decimal>());
            Assert.Equal(0m, worksheet.Cell(6, 66).GetValue<decimal>());
        }
        else
        {
            Assert.Equal("23:30", worksheet.Cell(6, 65).GetString());
            Assert.Equal("00:30", worksheet.Cell(6, 66).GetString());
        }
    }

    private static AttendanceRecord Record() => new("007", "Example Employee", "-", Date, "07:00", "16:00", 0);
}
