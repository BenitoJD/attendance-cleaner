using AttendanceCleaner.Core;
using Xunit;

namespace AttendanceCleaner.Tests;

public class DashboardSelectionTests
{
    private static readonly DateOnly First = new(2026, 9, 1);
    private static readonly DateOnly Last = new(2026, 9, 30);

    private static AttendanceRecord Row(string id, DateOnly date, string? start, string? end) =>
        new(id, $"Employee {id}", "", date, start, end, 0);

    private static ParsedReport Monthly(AttendanceExportFormat format = AttendanceExportFormat.MonthlyRows) =>
        new(format, new[]
        {
            Row("A", Last, "23:30", "00:15"),
            Row("A", First, "08:10", "16:45"),
            Row("B", First, null, "17:00"),
        });

    [Fact]
    public void Daily_upload_stays_daily_and_rejects_monthly_without_changing_scope()
    {
        var selection = new DashboardSelection(new(AttendanceExportFormat.DailyReport,
            new[] { Row("A", First, "08:10", "16:45") }));
        Assert.False(selection.SupportsMonthly);
        Assert.Equal(DashboardPeriod.Daily, selection.Period);
        Assert.Equal(new[] { First }, selection.AvailableDates);
        var original = selection.Current;
        Assert.Throws<InvalidOperationException>(() => selection.SelectPeriod(DashboardPeriod.Monthly));
        Assert.Same(original, selection.Current);
        Assert.Equal(DashboardPeriod.Daily, selection.Period);
    }

    [Theory]
    [InlineData(AttendanceExportFormat.MonthlyRows)]
    [InlineData(AttendanceExportFormat.MonthlyBlocks)]
    public void Monthly_upload_defaults_to_month_and_filters_every_summary_by_exported_date(AttendanceExportFormat format)
    {
        var selection = new DashboardSelection(Monthly(format));
        Assert.True(selection.SupportsMonthly);
        Assert.Equal(DashboardPeriod.Monthly, selection.Period);
        Assert.Equal(new[] { First, Last }, selection.AvailableDates);
        Assert.Equal(Last, selection.SelectedDate);
        Assert.Equal(3, selection.Current.TotalRows);
        Assert.Equal(2, selection.Current.EmployeeCount);

        selection.SelectPeriod(DashboardPeriod.Daily);
        var lastDay = selection.Current;
        Assert.Equal(Last, lastDay.From);
        Assert.Equal(Last, lastDay.To);
        Assert.Equal(1, lastDay.EmployeeCount);
        Assert.Equal(1, lastDay.TotalRows);
        Assert.Equal("00:45", lastDay.AverageHours);
        Assert.Single(lastDay.Days);
        Assert.Equal("A", Assert.Single(lastDay.Employees).Id);

        selection.SelectDate(First);
        var firstDay = selection.Current;
        Assert.Equal(2, firstDay.TotalRows);
        Assert.Equal(2, firstDay.EmployeeCount);
        Assert.Equal(1, firstDay.Present);
        Assert.Equal(1, firstDay.InPunchOnly);
        Assert.Equal(50, firstDay.PresentRate);
        Assert.Equal("08:35", firstDay.AverageHours);
        Assert.Equal(First, Assert.Single(firstDay.Days).Date);
        Assert.Equal(First, firstDay.From);
        Assert.Equal(First, firstDay.To);
    }

    [Fact]
    public void Returning_to_month_restores_all_records_and_retains_daily_date()
    {
        var selection = new DashboardSelection(Monthly());
        var wholeMonth = selection.Current;
        selection.SelectDate(First);
        selection.SelectPeriod(DashboardPeriod.Monthly);
        Assert.Same(wholeMonth, selection.Current);
        Assert.Equal(First, selection.SelectedDate);
        selection.SelectPeriod(DashboardPeriod.Daily);
        Assert.Equal(First, selection.Current.From);
    }

    [Fact]
    public void Unreported_dates_are_rejected_without_changing_the_selected_date_or_dashboard()
    {
        var selection = new DashboardSelection(Monthly());
        var original = selection.Current;
        Assert.Throws<ArgumentOutOfRangeException>(() => selection.SelectDate(First.AddDays(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => selection.SelectDate(Last.AddMonths(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => selection.SelectPeriod((DashboardPeriod)99));
        Assert.Same(original, selection.Current);
        Assert.Equal(DashboardPeriod.Monthly, selection.Period);
        Assert.Equal(Last, selection.SelectedDate);
    }

    [Fact]
    public void Monthly_source_with_one_exported_day_still_offers_both_views()
    {
        var selection = new DashboardSelection(new(AttendanceExportFormat.MonthlyBlocks,
            new[] { Row("A", First, null, null) }));
        Assert.True(selection.SupportsMonthly);
        Assert.Equal(DashboardPeriod.Monthly, selection.Period);
        selection.SelectDate(First);
        Assert.Equal(1, selection.Current.Absent);
        Assert.Null(selection.Current.AverageHours);
        selection.SelectPeriod(DashboardPeriod.Monthly);
        Assert.Equal(1, selection.Current.TotalRows);
    }

    [Fact]
    public void Input_records_are_snapshotted_and_identical_duplicates_count_once()
    {
        var rows = Monthly().Records.ToList();
        rows.Add(rows[0]);
        var selection = new DashboardSelection(new(AttendanceExportFormat.MonthlyRows, rows));
        rows.Clear();
        Assert.Equal(3, selection.Current.TotalRows);
        selection.SelectDate(Last);
        Assert.Equal(1, selection.Current.TotalRows);
    }

    [Fact]
    public void Conflicting_duplicate_records_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => new DashboardSelection(new(AttendanceExportFormat.MonthlyRows,
            new[] { Row("A", First, "08:00", "17:00"), Row("A", First, "09:00", "17:00") })));
    }

    [Fact]
    public void Invalid_report_ranges_and_empty_reports_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => new DashboardSelection(new(AttendanceExportFormat.DailyReport, Monthly().Records)));
        Assert.Throws<InvalidDataException>(() => new DashboardSelection(new(AttendanceExportFormat.MonthlyRows,
            new[] { Row("A", First, null, null), Row("A", First.AddMonths(1), null, null) })));
        Assert.Throws<InvalidDataException>(() => new DashboardSelection(new(AttendanceExportFormat.MonthlyRows,
            new[] { Row("A", First, null, null), Row("A", First.AddYears(1), null, null) })));
        Assert.Throws<InvalidDataException>(() => new DashboardSelection(new(AttendanceExportFormat.MonthlyRows, Array.Empty<AttendanceRecord>())));
    }

    [Fact]
    public void Leap_day_remains_an_available_daily_scope()
    {
        var date = new DateOnly(2028, 2, 29);
        var selection = new DashboardSelection(new(AttendanceExportFormat.MonthlyRows, new[] { Row("A", date, "08:00", "16:59") }));
        selection.SelectDate(date);
        Assert.Equal(date, selection.Current.From);
        Assert.Equal("08:59", selection.Current.AverageHours);
    }
}
