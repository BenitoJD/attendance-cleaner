namespace AttendanceCleaner.Core;

/// <summary>One employee's attendance for one day, as extracted from a source report.</summary>
public sealed record AttendanceRecord(
    string Id,
    string Name,
    string Gender,
    DateOnly Date,
    string? InPunch,
    string? OutPunch,
    int Order,
    string? Attended = null,
    string? Overtime = null,
    string? Status = null);

/// <summary>The result of parsing an attendance export.</summary>
public sealed record ParsedReport(
    AttendanceExportFormat Format,
    IReadOnlyList<AttendanceRecord> Records,
    IReadOnlyList<MonthlyEmployeeTotals>? EmployeeTotals = null);

/// <summary>
/// Employee-level summary figures supplied by monthly export layouts that do not
/// include a per-day status row. Leave totals are aggregate-only and have no date mapping.
/// </summary>
public sealed record MonthlyEmployeeTotals(
    string Id,
    string Name,
    int Order,
    decimal? AbsentDays,
    decimal? AttendedDays,
    decimal? LeaveDays);

public enum AttendanceExportFormat
{
    DailyReport,
    MonthlyBlocks,
    MonthlyRows,
}
