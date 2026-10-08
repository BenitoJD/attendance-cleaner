using System.Globalization;

namespace AttendanceCleaner.Core;

/// <summary>One employee's attendance summary for the dashboard.</summary>
public sealed record EmployeeSummary(
    string Id,
    string Name,
    int PresentDays,
    int AbsentDays,
    int InPunchOnlyDays,
    string? AverageIn,
    string? AverageOut,
    string? AverageHours,
    double AttendanceRate);

/// <summary>Attendance counts for one calendar day, for the daily trend chart.</summary>
public sealed record DayTrend(DateOnly Date, int Present, int Absent, int InPunchOnly);

/// <summary>Everything the dashboard and the PDF report show, derived from the converted rows.</summary>
public sealed record Dashboard(
    int TotalRows,
    int EmployeeCount,
    DateOnly From,
    DateOnly To,
    int Present,
    int Absent,
    int InPunchOnly,
    double PresentRate,
    double AbsentRate,
    string? AverageIn,
    string? AverageOut,
    string? AverageHours,
    IReadOnlyList<EmployeeSummary> Employees,
    IReadOnlyList<DayTrend> Days);

/// <summary>
/// Derives dashboard summaries from parsed attendance records. Every figure is computed
/// from the data — no schedule or work rules are assumed.
/// </summary>
public static class AttendanceAnalytics
{
    public static Dashboard Build(IEnumerable<AttendanceRecord> records)
    {
        var all = records as IReadOnlyList<AttendanceRecord> ?? records.ToList();
        if (all.Count == 0)
        {
            throw new InvalidDataException("No attendance rows were found in this file.");
        }

        var from = all.Min(r => r.Date);
        var to = all.Max(r => r.Date);
        var present = all.Count(r => r.InPunch != null && r.OutPunch != null);
        var absent = all.Count(r => r.InPunch == null && r.OutPunch == null);
        var inOnly = all.Count - present - absent;

        var days = all
            .GroupBy(r => r.Date)
            .OrderBy(g => g.Key)
            .Select(g => new DayTrend(
                g.Key,
                g.Count(r => r.InPunch != null && r.OutPunch != null),
                g.Count(r => r.InPunch == null && r.OutPunch == null),
                g.Count(r => (r.InPunch != null) != (r.OutPunch != null))))
            .ToList();

        var employees = all
            .GroupBy(r => (r.Id, r.Name))
            .OrderBy(g => g.Key.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var empPresent = g.Count(r => r.InPunch != null && r.OutPunch != null);
                var empAbsent = g.Count(r => r.InPunch == null && r.OutPunch == null);
                var empInOnly = g.Count() - empPresent - empAbsent;
                return new EmployeeSummary(
                    g.Key.Id,
                    g.Key.Name,
                    empPresent,
                    empAbsent,
                    empInOnly,
                    AveragePunch(g, r => r.InPunch),
                    AveragePunch(g, r => r.OutPunch),
                    AverageHours(g),
                    g.Count() == 0 ? 0 : 100.0 * empPresent / g.Count());
            })
            .ToList();

        return new Dashboard(
            TotalRows: all.Count,
            EmployeeCount: employees.Count,
            From: from,
            To: to,
            Present: present,
            Absent: absent,
            InPunchOnly: inOnly,
            PresentRate: 100.0 * present / all.Count,
            AbsentRate: 100.0 * absent / all.Count,
            AverageIn: AveragePunch(all, r => r.InPunch),
            AverageOut: AveragePunch(all, r => r.OutPunch),
            AverageHours: AverageHours(all),
            Employees: employees,
            Days: days);
    }

    /// <summary>Average punch time as "HH:mm" (over the rows that have that punch), rounded to the minute.</summary>
    private static string? AveragePunch(IEnumerable<AttendanceRecord> rows, Func<AttendanceRecord, string?> punch)
    {
        var minutes = rows.Select(punch).Where(p => p != null)
            .Select(p => TimeOnly.ParseExact(p!, TemplateSpec.TimeFormat, CultureInfo.InvariantCulture))
            .Select(t => t.Hour * 60 + t.Minute)
            .ToList();
        if (minutes.Count == 0) return null;
        var avg = (int)Math.Round(minutes.Average());
        return $"{avg / 60:00}:{avg % 60:00}";
    }

    /// <summary>Average worked hours as "HH:mm" (over fully present rows only).</summary>
    private static string? AverageHours(IEnumerable<AttendanceRecord> rows)
    {
        var spans = rows.Where(r => r.InPunch != null && r.OutPunch != null)
            .Select(r =>
            {
                var tin = TimeOnly.ParseExact(r.InPunch!, TemplateSpec.TimeFormat, CultureInfo.InvariantCulture);
                var tout = TimeOnly.ParseExact(r.OutPunch!, TemplateSpec.TimeFormat, CultureInfo.InvariantCulture);
                var m = (int)(tout - tin).TotalMinutes;
                return m < 0 ? m + 24 * 60 : m;
            })
            .ToList();
        if (spans.Count == 0) return null;
        var avg = (int)Math.Round(spans.Average());
        return $"{avg / 60:00}:{avg % 60:00}";
    }
}
