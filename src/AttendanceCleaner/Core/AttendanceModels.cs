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
    IReadOnlyList<AttendanceRecord> Records);

public enum AttendanceExportFormat
{
    DailyReport,
    MonthlyBlocks,
    MonthlyRows,
}
