using AttendanceCleaner.Core;
using Xunit;

namespace AttendanceCleaner.Tests;

public class DashboardChartTests
{
    private static readonly DateOnly Date = new(2026, 10, 1);
    private static AttendanceRecord Row(string id, string? start, string? end, DateOnly? date = null) =>
        new(id, $"Employee {id}", "", date ?? Date, start, end, 0);

    [Theory]
    [InlineData("00:00", 0)]
    [InlineData("03:59", 0)]
    [InlineData("04:00", 1)]
    [InlineData("07:59", 1)]
    [InlineData("08:00", 2)]
    [InlineData("11:59", 2)]
    [InlineData("12:00", 3)]
    [InlineData("23:59", 3)]
    public void Duration_bands_have_exact_boundaries(string end, int bucket)
    {
        var dashboard = AttendanceAnalytics.Build(new[] { Row("1", "00:00", end) });
        Assert.Equal(1, dashboard.DurationBuckets[bucket].Count);
        Assert.Equal(1, dashboard.DurationBuckets.Sum(band => band.Count));
    }

    [Fact]
    public void Overnight_spans_use_minutes_and_duplicate_records_count_once()
    {
        var night = Row("1", "23:30", "00:15");
        var dashboard = AttendanceAnalytics.Build(new[] { night, night, Row("2", "08:00", null), Row("3", null, "18:00"), Row("4", null, null) });
        Assert.Equal(1, dashboard.DurationBuckets[0].Count);
        Assert.Equal(1, dashboard.DurationBuckets.Sum(band => band.Count));
        Assert.Equal(45, dashboard.Days[0].AverageMinutes);
        Assert.Equal("00:45", dashboard.Days[0].AverageHours);
        var employee = Assert.Single(DashboardCharts.LongestEmployeeSpans(dashboard));
        Assert.Equal(45, employee.Minutes);
        Assert.Equal(1, employee.CompletePairs);
    }

    [Fact]
    public void Days_without_complete_pairs_have_no_duration_instead_of_zero()
    {
        var dashboard = AttendanceAnalytics.Build(new[] { Row("1", "08:00", null), Row("2", null, null) });
        Assert.Null(dashboard.Days[0].AverageMinutes);
        Assert.Null(dashboard.Days[0].AverageHours);
        Assert.All(dashboard.DurationBuckets, bucket => Assert.Equal(0, bucket.Count));
        Assert.Empty(DashboardCharts.LongestEmployeeSpans(dashboard));
    }

    [Fact]
    public void Per_date_average_uses_complete_pairs_and_does_not_mix_dates()
    {
        var dashboard = AttendanceAnalytics.Build(new[]
        {
            Row("1", "08:00", "16:00"), Row("2", "08:00", "20:00"), Row("3", "08:00", null),
            Row("1", "08:00", "17:00", Date.AddDays(1)),
        });
        Assert.Equal(600, dashboard.Days[0].AverageMinutes);
        Assert.Equal(540, dashboard.Days[1].AverageMinutes);
    }

    [Fact]
    public void Employee_comparison_is_limited_sorted_and_excludes_missing_pairs()
    {
        var rows = Enumerable.Range(1, 12).Select(i => Row(i.ToString(), "00:00", $"{i:00}:01")).Append(Row("missing", null, null));
        var employees = DashboardCharts.LongestEmployeeSpans(AttendanceAnalytics.Build(rows));
        Assert.Equal(8, employees.Count);
        Assert.Equal("12", employees[0].Id);
        Assert.Equal(721, employees[0].Minutes);
        Assert.Equal("5", employees[^1].Id);
        Assert.DoesNotContain(employees, employee => employee.Id == "missing");
    }

    [Fact]
    public void Selecting_a_daily_scope_updates_all_chart_data()
    {
        var rows = new[] { Row("1", "08:00", "20:00"), Row("1", "08:00", "09:00", Date.AddDays(1)) };
        var selection = new DashboardSelection(new ParsedReport(AttendanceExportFormat.MonthlyRows, rows));
        Assert.Equal(1, selection.Current.DurationBuckets[3].Count);
        selection.SelectDate(Date.AddDays(1));
        Assert.Equal(1, selection.Current.DurationBuckets[0].Count);
        Assert.Equal(0, selection.Current.DurationBuckets[3].Count);
        Assert.Equal(60, Assert.Single(DashboardCharts.LongestEmployeeSpans(selection.Current)).Minutes);
        Assert.Equal(60, Assert.Single(selection.Current.Days).AverageMinutes);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("08:00", null)]
    [InlineData("08:00", "08:00")]
    [InlineData("23:30", "00:15")]
    public void Chart_pdf_handles_missing_zero_and_overnight_spans(string? start, string? end)
    {
        var pdf = DashboardPdf.Build(AttendanceAnalytics.Build(new[] { Row("1", start, end) }), "edge.xls", DashboardPeriod.Daily);
        Assert.True(pdf.Length > 1000);
    }
}
