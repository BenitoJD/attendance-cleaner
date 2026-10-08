using System.Globalization;
using AttendanceCleaner.Core;
using Xunit;

namespace AttendanceCleaner.Tests;

public class DashboardAnalyticsTests
{
    private static DateOnly Sep(int day) => new(2026, 9, day);

    private static AttendanceRecord R(string id, string name, int day, string? tin, string? tout) =>
        new(id, name, "", Sep(day), tin, tout, 0);

    private static readonly AttendanceRecord[] Sample =
    {
        R("3", "AJITH", 1, "06:51", "19:00"),
        R("3", "AJITH", 2, "07:11", "18:30"),
        R("3", "AJITH", 3, null, null),
        R("4", "MICHEAL", 1, null, null),
        R("4", "MICHEAL", 2, "08:00", "17:00"),
        R("4", "MICHEAL", 3, "07:30", null),          // in-punch only
        R("5", "SUDER", 1, "06:30", "18:00"),
        R("5", "SUDER", 2, "06:30", "18:00"),
        R("5", "SUDER", 3, "06:30", "18:00"),
    };

    private static Dashboard Build() => AttendanceAnalytics.Build(Sample);

    [Fact]
    public void Counts_rows_employees_and_range()
    {
        var d = Build();
        Assert.Equal(9, d.TotalRows);
        Assert.Equal(3, d.EmployeeCount);
        Assert.Equal(Sep(1), d.From);
        Assert.Equal(Sep(3), d.To);
    }

    [Fact]
    public void Present_absent_and_partial_counts_are_exact()
    {
        var d = Build();
        Assert.Equal(6, d.Present);
        Assert.Equal(2, d.Absent);
        Assert.Equal(1, d.InPunchOnly);
        Assert.Equal(100.0 * 6 / 9, d.PresentRate);
        Assert.Equal(100.0 * 2 / 9, d.AbsentRate);
    }

    [Fact]
    public void Overall_averages_cover_only_rows_with_that_value()
    {
        var d = Build();
        // avg in over 7 punches (present + in-only): 411+431+480+450+390+390+390 = 2942/7 = 420.3 -> 07:00
        Assert.Equal("07:00", d.AverageIn);
        // avg out over the 6 present rows: 1140+1110+1020+1080*3 = 6510/6 = 1085 -> 18:05
        Assert.Equal("18:05", d.AverageOut);
        // avg hours over present rows: 729+679+540+690*3 = 4018/6 = 669.7 -> 11:10
        Assert.Equal("11:10", d.AverageHours);
    }

    [Fact]
    public void Per_employee_summary_is_exact()
    {
        var d = Build();
        var ajith = d.Employees.Single(e => e.Name == "AJITH");
        Assert.Equal(2, ajith.PresentDays);
        Assert.Equal(1, ajith.AbsentDays);
        Assert.Equal(0, ajith.InPunchOnlyDays);
        Assert.Equal(100.0 * 2 / 3, ajith.AttendanceRate);

        var Micheal = d.Employees.Single(e => e.Name == "MICHEAL");
        Assert.Equal(1, Micheal.PresentDays);
        Assert.Equal(1, Micheal.AbsentDays);
        Assert.Equal(1, Micheal.InPunchOnlyDays);
    }

    [Fact]
    public void Employees_are_listed_alphabetically()
    {
        var names = Build().Employees.Select(e => e.Name).ToList();
        Assert.Equal(new[] { "AJITH", "MICHEAL", "SUDER" }, names);
    }

    [Fact]
    public void Day_trend_matches_each_days_counts()
    {
        var d = Build();
        Assert.Equal(3, d.Days.Count);
        Assert.All(d.Days, day => Assert.Equal(3, day.Present + day.Absent + day.InPunchOnly));
        Assert.Equal(1, d.Days[0].Absent);   // day 1: AJITH+SUDER present, MICHEAL absent
        Assert.Equal(1, d.Days[2].InPunchOnly); // day 3: MICHEAL in-only
    }

    [Fact]
    public void Overnight_shifts_count_positively_toward_hours()
    {
        var d = AttendanceAnalytics.Build(new[] { R("9", "NIGHT", 1, "23:30", "00:15") });
        Assert.Equal("00:45", d.AverageHours);
    }

    [Fact]
    public void Empty_input_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => AttendanceAnalytics.Build(Array.Empty<AttendanceRecord>()));
    }

    [Fact]
    public void Fully_absent_employees_get_null_averages_not_zeroes()
    {
        var d = AttendanceAnalytics.Build(new[] { R("1", "GHOST", 1, null, null) });
        Assert.Null(d.AverageIn);
        Assert.Null(d.AverageOut);
        Assert.Null(d.AverageHours);
        Assert.Equal(0, d.Employees.Single().AttendanceRate);
    }
}

public class DashboardPdfTests
{
    [Fact]
    public void Pdf_is_generated_and_has_the_pdf_signature()
    {
        var records = new[]
        {
            new AttendanceRecord("3", "AJITH.S.S", "Male", new DateOnly(2026, 9, 1), "06:51", "19:00", 0),
            new AttendanceRecord("4", "MICHEAL ANTONY", "Male", new DateOnly(2026, 9, 1), null, null, 1),
        };
        var pdf = DashboardPdf.Build(AttendanceAnalytics.Build(records), "Sep monthly Report.xls");

        Assert.True(pdf.Length > 1000, "suspiciously small pdf");
        Assert.Equal((byte)0x25, pdf[0]); // '%'
        Assert.Equal((byte)0x50, pdf[1]); // 'P'
        Assert.Equal((byte)0x44, pdf[2]); // 'D'
        Assert.Equal((byte)0x46, pdf[3]); // 'F'
    }

    [Fact]
    public void Pdf_for_a_large_month_generates()
    {
        var records = Enumerable.Range(1, 30).SelectMany(d =>
            Enumerable.Range(1, 24).Select(e =>
                new AttendanceRecord(
                    $"{e}", $"EMPLOYEE {e:00}", "", new DateOnly(2026, 9, d),
                    e % 3 == 0 ? "06:51" : null, e % 3 == 0 ? "19:00" : null, e)));

        var pdf = DashboardPdf.Build(AttendanceAnalytics.Build(records), "big.xls");
        Assert.True(pdf.Length > 10_000);
    }
}
