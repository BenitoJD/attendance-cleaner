using System.Text;
using System.Text.RegularExpressions;
using AttendanceCleaner.Core;
using Xunit;

namespace AttendanceCleaner.Tests;

public sealed class TemplatePdfTests
{
    [Fact]
    public void Daily_pdf_handles_multiple_pages_and_long_identifiers()
    {
        var records = Enumerable.Range(1, 180).Select(index => new AttendanceRecord(
            $"001234567890123456{index}", $"Employee {index} with a longer name", "Male",
            new DateOnly(2026, 10, 9), index % 3 == 0 ? null : "08:00", "17:00", index));
        using var workbook = new MemoryStream();
        TemplateWriter.Write(records, workbook);
        workbook.Position = 0;
        AssertPdf(TemplatePdf.Build(workbook, monthly: false));
    }

    [Theory]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime, 2)]
    [InlineData(MonthlyTemplateKind.InAndOut, 2)]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime, 9)]
    [InlineData(MonthlyTemplateKind.InAndOut, 10)]
    public void Monthly_pdf_handles_all_days_totals_holidays_and_vertical_pagination(MonthlyTemplateKind kind, int month)
    {
        var records = Enumerable.Range(1, DateTime.DaysInMonth(2026, month)).SelectMany(day =>
            Enumerable.Range(1, 45).Select(employee => new AttendanceRecord(
                $"{employee}", $"Employee {employee}", "", new DateOnly(2026, month, day),
                "08:00", "19:00", employee)));
        using var workbook = new MemoryStream();
        MonthlyTemplateWriter.Write(records, HolidayCalendarStore.Seed2026(), kind, workbook);
        workbook.Position = 0;
        AssertPdf(TemplatePdf.Build(workbook, monthly: true));
    }

    [Theory]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime, 2026, 2)]
    [InlineData(MonthlyTemplateKind.InAndOut, 2026, 2)]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime, 2028, 2)]
    [InlineData(MonthlyTemplateKind.InAndOut, 2028, 2)]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime, 2026, 9)]
    [InlineData(MonthlyTemplateKind.InAndOut, 2026, 9)]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime, 2026, 10)]
    [InlineData(MonthlyTemplateKind.InAndOut, 2026, 10)]
    public void Full_month_keeps_all_dates_on_one_large_attendance_page_and_holidays_on_a4(
        MonthlyTemplateKind kind, int year, int month)
    {
        using var workbook = MonthlyWorkbook(kind, year, month, 45);
        var full = TemplatePdf.Build(workbook, true, MonthlyPdfLayout.FullMonth);
        AssertPdf(full);
        var fullPages = PageBoxes(full);
        Assert.Equal((2384, 1684), fullPages[0]);
        Assert.Single(fullPages, size => size == (2384, 1684));
        Assert.Contains((842, 595), fullPages);
        Assert.All(fullPages.Skip(1), size => Assert.Equal((842, 595), size));

        workbook.Position = 0;
        var weekly = TemplatePdf.Build(workbook, true, MonthlyPdfLayout.Weekly);
        var weeklyPages = PageBoxes(weekly);
        Assert.All(weeklyPages, size => Assert.Equal((842, 595), size));
        Assert.True(weeklyPages.Length > fullPages.Length);
    }

    [Theory]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime)]
    [InlineData(MonthlyTemplateKind.InAndOut)]
    public void Large_employee_lists_continue_vertically_on_full_width_pages(MonthlyTemplateKind kind)
    {
        using var workbook = MonthlyWorkbook(kind, 2026, 10, 160);
        var pdf = TemplatePdf.Build(workbook, true, MonthlyPdfLayout.FullMonth);
        AssertPdf(pdf);
        Assert.True(PageBoxes(pdf).Count(size => size == (2384, 1684)) > 1);
    }

    [Fact]
    public void Monthly_layout_selection_does_not_enlarge_daily_reports()
    {
        using var workbook = new MemoryStream();
        TemplateWriter.Write([new AttendanceRecord("007", "Example", "Male", new DateOnly(2026, 10, 9), "06:51", "19:00", 0)], workbook);
        workbook.Position = 0;
        var pdf = TemplatePdf.Build(workbook, false, MonthlyPdfLayout.FullMonth);
        Assert.Equal((842, 595), Assert.Single(PageBoxes(pdf)));
    }

    private static MemoryStream MonthlyWorkbook(MonthlyTemplateKind kind, int year, int month, int employees)
    {
        var records = Enumerable.Range(1, DateTime.DaysInMonth(year, month)).SelectMany(day =>
            Enumerable.Range(1, employees).Select(employee => new AttendanceRecord(
                $"001{employee}", $"Employee {employee} with longer name", "Male", new DateOnly(year, month, day),
                "08:00", "19:00", employee)));
        var stream = new MemoryStream();
        MonthlyTemplateWriter.Write(records, HolidayCalendarStore.Seed2026(), kind, stream);
        stream.Position = 0;
        return stream;
    }

    private static (int Width, int Height)[] PageBoxes(byte[] pdf) =>
        Regex.Matches(Encoding.Latin1.GetString(pdf), @"/MediaBox\s*\[0 0 (\d+) (\d+)\]")
            .Select(match => (int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))).ToArray();

    private static void AssertPdf(byte[] bytes)
    {
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.True(bytes.Length > 1000);
    }
}
