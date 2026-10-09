namespace AttendanceCleaner.Core;

public enum DashboardPeriod
{
    Daily,
    Monthly,
}

/// <summary>Retains uploaded records and applies one scope to dashboard UI and PDF exports.</summary>
public sealed class DashboardSelection
{
    private readonly IReadOnlyList<AttendanceRecord> _records;
    private readonly Dashboard _monthly;

    public DashboardSelection(ParsedReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        SupportsMonthly = report.Format switch
        {
            AttendanceExportFormat.DailyReport => false,
            AttendanceExportFormat.MonthlyBlocks or AttendanceExportFormat.MonthlyRows => true,
            _ => throw new ArgumentOutOfRangeException(nameof(report), "Unknown report format."),
        };
        _records = Array.AsReadOnly(TemplateWriter.DistinctEmployeeDates(report.Records).ToArray());
        _monthly = AttendanceAnalytics.Build(_records);
        AvailableDates = Array.AsReadOnly(_records.Select(record => record.Date).Distinct().Order().ToArray());
        if (!SupportsMonthly && AvailableDates.Count != 1)
            throw new InvalidDataException("Daily dashboards require a report containing one date.");
        if (SupportsMonthly && AvailableDates.Any(date => date.Year != _monthly.From.Year || date.Month != _monthly.From.Month))
            throw new InvalidDataException("Monthly dashboards require a report containing one calendar month.");

        SelectedDate = AvailableDates[^1];
        Period = SupportsMonthly ? DashboardPeriod.Monthly : DashboardPeriod.Daily;
        Current = _monthly;
    }

    public bool SupportsMonthly { get; }
    public IReadOnlyList<DateOnly> AvailableDates { get; }
    public DateOnly SelectedDate { get; private set; }
    public DashboardPeriod Period { get; private set; }
    public Dashboard Current { get; private set; }

    public void SelectPeriod(DashboardPeriod period)
    {
        if (period is not (DashboardPeriod.Daily or DashboardPeriod.Monthly))
            throw new ArgumentOutOfRangeException(nameof(period));
        if (period == DashboardPeriod.Monthly && !SupportsMonthly)
            throw new InvalidOperationException("A daily upload cannot show a monthly dashboard.");

        var next = period == DashboardPeriod.Monthly
            ? _monthly
            : AttendanceAnalytics.Build(_records.Where(record => record.Date == SelectedDate));
        Period = period;
        Current = next;
    }

    public void SelectDate(DateOnly date)
    {
        if (!AvailableDates.Contains(date))
            throw new ArgumentOutOfRangeException(nameof(date), "Select an exported date from this report.");
        var next = AttendanceAnalytics.Build(_records.Where(record => record.Date == date));
        SelectedDate = date;
        Period = DashboardPeriod.Daily;
        Current = next;
    }
}
