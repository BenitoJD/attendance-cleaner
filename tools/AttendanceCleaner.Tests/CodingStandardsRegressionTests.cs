using System.Globalization;
using AttendanceCleaner.Core;
using ClosedXML.Excel;
using Xunit;

namespace AttendanceCleaner.Tests;

public sealed class CodingStandardsRegressionTests
{
    private static readonly DateOnly Month = new(2026, 9, 1);

    [Theory]
    [InlineData("0013", XLDataType.Text)]
    [InlineData("1234567890123456", XLDataType.Text)]
    [InlineData("9223372036854775807", XLDataType.Text)]
    [InlineData("EMP-13", XLDataType.Text)]
    [InlineData("13", XLDataType.Number)]
    [InlineData("123456789012345", XLDataType.Number)]
    public void Both_workbook_layouts_preserve_employee_identifiers(string id, XLDataType expectedType)
    {
        var records = new[] { Record(id) };
        using var daily = new MemoryStream();
        TemplateWriter.Write(records, daily);
        daily.Position = 0;
        using var dailyWorkbook = new XLWorkbook(daily);
        AssertIdentifier(dailyWorkbook.Worksheet(TemplateSpec.SheetName).Cell(2, 2));

        foreach (var kind in Enum.GetValues<MonthlyTemplateKind>())
        {
            using var monthly = new MemoryStream();
            MonthlyTemplateWriter.Write(records, [], kind, monthly);
            monthly.Position = 0;
            using var monthlyWorkbook = new XLWorkbook(monthly);
            AssertIdentifier(monthlyWorkbook.Worksheet(TemplateSpec.SheetName).Cell(6, 3));
        }

        void AssertIdentifier(IXLCell cell)
        {
            Assert.Equal(expectedType, cell.DataType);
            Assert.Equal(id, cell.GetString());
        }
    }

    [Theory]
    [InlineData("23:30", "00:15", "00:45", 0.75)]
    [InlineData("08:00", "12:30", "04:30", 4.5)]
    [InlineData("08:00", "08:00", "00:00", 0)]
    public void Daily_dashboard_and_monthly_duration_arithmetic_agree(
        string start, string end, string expectedText, double expectedHours)
    {
        var records = new[] { Record("13") with { InPunch = start, OutPunch = end } };
        Assert.Equal(expectedText, Assert.Single(TemplateWriter.BuildRows(records)).TotalHours);
        Assert.Equal(expectedText, AttendanceAnalytics.Build(records).AverageHours);
        Assert.Equal((decimal)expectedHours,
            Assert.Single(MonthlyTemplateWriter.BuildSummaries(records, [], Month)).TotalHours);
    }

    [Theory]
    [InlineData("1.5", "01:30")]
    [InlineData("01:30", "01:30")]
    [InlineData("-", "04:00")]
    [InlineData("79228162514264337593543950335", "04:00")]
    public void Reported_duration_is_culture_independent_and_cannot_overflow(string attended, string expected)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var record = Record("13") with { InPunch = "08:00", OutPunch = "12:00", Attended = attended };
            Assert.Equal(expected, Assert.Single(TemplateWriter.BuildRows([record])).TotalHours);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Employee_grouping_does_not_collide_on_delimiters_in_source_text()
    {
        var records = new[]
        {
            Record("A\u001fB") with { Name = "C" },
            Record("A") with { Name = "B\u001fC" },
        };
        Assert.Equal(2, MonthlyTemplateWriter.BuildSummaries(records, [], Month).Count);
    }

    [Fact]
    public void Bundled_calendar_has_complete_entries_and_fresh_identifiers()
    {
        var first = HolidayCalendarStore.Seed2026();
        var second = HolidayCalendarStore.Seed2026();
        Assert.Equal(32, first.Count);
        Assert.All(first, entry =>
        {
            Assert.Equal(2026, entry.Date.Year);
            Assert.False(string.IsNullOrWhiteSpace(entry.Name));
            Assert.False(string.IsNullOrWhiteSpace(entry.Category));
        });
        Assert.Equal(first.Select(entry => (entry.Date, entry.Name, entry.Category)),
            second.Select(entry => (entry.Date, entry.Name, entry.Category)));
        Assert.Empty(first.Select(entry => entry.Id).Intersect(second.Select(entry => entry.Id)));
    }

    private static AttendanceRecord Record(string id) => new(id, "Example", "-", Month, "08:00", "12:00", 0);
}
