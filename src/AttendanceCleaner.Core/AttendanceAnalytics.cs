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
        var all = TemplateWriter.DistinctEmployeeDates(records);
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
                    AveragePunch(g, outPunch: false),
                    AveragePunch(g, outPunch: true),
                    AverageHours(g),
                    100.0 * empPresent / g.Count());
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
            AverageIn: AveragePunch(all, outPunch: false),
            AverageOut: AveragePunch(all, outPunch: true),
            AverageHours: AverageHours(all),
            Employees: employees,
            Days: days);
    }

    /// <summary>Average punch time as "HH:mm" (over the rows that have that punch), rounded to the minute.</summary>
    private static string? AveragePunch(IEnumerable<AttendanceRecord> rows, bool outPunch)
    {
        const int minutesPerDay = 24 * 60;
        var minutes = new List<int>();
        foreach (var row in rows)
        {
            var punch = outPunch ? row.OutPunch : row.InPunch;
            if (punch is null) continue;
            if (!AttendanceTime.TryParsePunch(punch, out var time))
                throw new FormatException($"Invalid normalized punch: {punch}");
            var value = time.Hour * 60 + time.Minute;
            // Departures after an overnight shift belong after that shift's check-in.
            if (outPunch && AttendanceTime.TryParsePunch(row.InPunch, out var start) && time < start)
                value += minutesPerDay;
            minutes.Add(value);
        }
        if (minutes.Count == 0) return null;

        // Align anchored departures onto a shared clock interval when they cluster around midnight.
        {
            // Unwrap the shortest clock interval, so 23:50 and 00:10 average to midnight.
            var clockValues = minutes.Select(value => value % minutesPerDay).Order().ToArray();
            var largestGap = -1;
            var intervalStart = clockValues[0];
            for (var index = 0; index < clockValues.Length; index++)
            {
                var next = clockValues[(index + 1) % clockValues.Length];
                var gap = next + (index == clockValues.Length - 1 ? minutesPerDay : 0) - clockValues[index];
                if (gap <= largestGap) continue;
                largestGap = gap;
                intervalStart = next;
            }
            if (largestGap > minutesPerDay / 2)
                minutes = clockValues.Select(value => value < intervalStart ? value + minutesPerDay : value).ToList();
        }

        var avg = (int)Math.Round(minutes.Average()) % minutesPerDay;
        return AttendanceTime.FormatMinutes(avg);
    }

    /// <summary>Average worked hours as "HH:mm" (over fully present rows only).</summary>
    private static string? AverageHours(IEnumerable<AttendanceRecord> rows)
    {
        var spans = rows.Where(r => r.InPunch != null && r.OutPunch != null)
            .Select(r => TemplateWriter.WorkedMinutes(r)
                ?? throw new FormatException("Invalid normalized attendance punches."))
            .ToList();
        if (spans.Count == 0) return null;
        var avg = (int)Math.Round(spans.Average());
        return AttendanceTime.FormatMinutes(avg);
    }
}
