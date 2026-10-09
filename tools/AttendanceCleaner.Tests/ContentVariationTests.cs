using AttendanceCleaner.Core;
using ClosedXML.Excel;
using Xunit;

namespace AttendanceCleaner.Tests;

/// <summary>
/// Content-variation coverage: the exports always share the same structure, but the
/// content (months, day counts, ranges, punch values, names, ids) differs per file.
/// </summary>
public class ContentVariationTests
{
    private static DateOnly[] DatesOf(ParsedReport report, string name) =>
        report.Records.Where(r => r.Name == name).OrderBy(r => r.Date).Select(r => r.Date).ToArray();

    [Fact]
    public void Range_crossing_into_the_next_month_maps_both_months()
    {
        var report = AttendanceParser.Parse(TestReports.MonthlyBlocksFlexible(
            "28-09-2026",
            new[] { 28, 29, 30, 1, 2, 3 },
            new[] { new TestReports.BlockEmployee("3", "AJITH.S.S",
                new[] { 28, 29, 30, 1, 2, 3 },
                new string?[] { "06:51", null, null, "08:00", null, null },
                new string?[] { "19:00", null, null, "17:00", null, null }) }));

        Assert.Equal(6, report.Records.Count);
        var dates = DatesOf(report, "AJITH.S.S");
        Assert.Equal(new DateOnly(2026, 9, 28), dates[0]);
        Assert.Equal(new DateOnly(2026, 9, 30), dates[2]);
        Assert.Equal(new DateOnly(2026, 10, 1), dates[3]);
        Assert.Equal(new DateOnly(2026, 10, 3), dates[5]);

        var oct1 = report.Records.First(r => r.Date == new DateOnly(2026, 10, 1));
        Assert.Equal("08:00", oct1.InPunch);
        Assert.Equal("17:00", oct1.OutPunch);
    }

    [Fact]
    public void Month_with_31_days_is_fully_parsed()
    {
        var days = Enumerable.Range(1, 31).ToArray();
        var report = AttendanceParser.Parse(TestReports.MonthlyBlocksFlexible(
            "01-10-2026", days,
            new[] { new TestReports.BlockEmployee("3", "AJITH.S.S",
                days,
                Enumerable.Repeat<string?>(null, 31).Select((v, i) => i == 30 ? "07:00" : v).ToArray(),
                Enumerable.Repeat<string?>(null, 31).Select((v, i) => i == 30 ? "16:00" : v).ToArray()) }));

        var ajith = report.Records.Where(r => r.Name == "AJITH.S.S").ToList();
        Assert.Equal(31, ajith.Count);
        Assert.Contains(ajith, r => r.Date == new DateOnly(2026, 10, 31) && r.InPunch == "07:00");
    }

    [Fact]
    public void Non_leap_february_has_28_days()
    {
        var days = Enumerable.Range(1, 28).ToArray();
        var report = AttendanceParser.Parse(TestReports.MonthlyBlocksFlexible(
            "01-02-2027", days,
            new[] { new TestReports.BlockEmployee("3", "AJITH.S.S", days,
                Enumerable.Repeat<string?>(null, 28).ToArray(),
                Enumerable.Repeat<string?>(null, 28).ToArray()) }));

        Assert.Equal(28, report.Records.Count);
        Assert.DoesNotContain(report.Records, r => r.Date.Month == 2 && r.Date.Day == 29);
    }

    [Fact]
    public void Leap_year_february_has_29_days()
    {
        var days = Enumerable.Range(1, 29).ToArray();
        var report = AttendanceParser.Parse(TestReports.MonthlyBlocksFlexible(
            "01-02-2024", days,
            new[] { new TestReports.BlockEmployee("3", "AJITH.S.S", days,
                Enumerable.Repeat<string?>(null, 29).ToArray(),
                Enumerable.Repeat<string?>(null, 29).ToArray()) }));

        Assert.Equal(29, report.Records.Count);
        Assert.Contains(report.Records, r => r.Date == new DateOnly(2024, 2, 29));
    }

    [Fact]
    public void Partial_month_range_starts_and_ends_on_the_report_days()
    {
        var days = Enumerable.Range(5, 16).ToArray(); // 5..20
        var report = AttendanceParser.Parse(TestReports.MonthlyBlocksFlexible(
            "05-09-2026", days,
            new[] { new TestReports.BlockEmployee("3", "AJITH.S.S", days,
                days.Select(d => d == 10 ? "06:00" : (string?)null).ToArray(),
                days.Select(d => d == 10 ? "18:00" : (string?)null).ToArray()) }));

        var dates = DatesOf(report, "AJITH.S.S");
        Assert.Equal(16, dates.Length);
        Assert.Equal(new DateOnly(2026, 9, 5), dates[0]);
        Assert.Equal(new DateOnly(2026, 9, 20), dates[^1]);
    }

    [Fact]
    public void Labels_in_any_letter_case_are_recognised()
    {
        var report = AttendanceParser.Parse(TestReports.MonthlyBlocksFlexible(
            "01-09-2026",
            new[] { 1, 2 },
            new[] { new TestReports.BlockEmployee("3", "AJITH.S.S",
                new[] { 1, 2 }, new string?[] { "06:51", null }, new string?[] { "19:00", null }) },
            lowercaseLabels: true));

        var day1 = report.Records.First(r => r.Date.Day == 1);
        Assert.Equal("06:51", day1.InPunch);
        Assert.Equal("19:00", day1.OutPunch);
    }

    [Fact]
    public void Daily_file_can_contain_more_than_one_date()
    {
        var report = AttendanceParser.Parse(TestReports.DailyFlexible(
            new TestReports.DailyRow("1", "3", "AJITH.S.S", "06-10-2026", "06:51", "19:00"),
            new TestReports.DailyRow("2", "3", "AJITH.S.S", "07-10-2026", "06:49", "19:05")));

        Assert.Equal(2, report.Records.Count);
        Assert.Equal(new DateOnly(2026, 10, 6), report.Records[0].Date);
        Assert.Equal(new DateOnly(2026, 10, 7), report.Records[1].Date);
    }

    [Fact]
    public void Single_digit_hours_are_normalised()
    {
        var report = AttendanceParser.Parse(TestReports.DailyFlexible(
            new TestReports.DailyRow("1", "3", "AJITH.S.S", "06-10-2026", "6:51", "19:00")));

        Assert.Equal("06:51", report.Records[0].InPunch);
    }

    [Theory]
    [InlineData("24:00")]
    [InlineData("06:99")]
    [InlineData("abc")]
    public void Impossible_punch_values_are_rejected_instead_of_becoming_missing(string invalidPunch)
    {
        var html = TestReports.DailyFlexible(new TestReports.DailyRow("1", "3", "A", "06-10-2026", invalidPunch, "19:00"));
        var exception = Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html));

        Assert.Contains(invalidPunch, exception.Message);
        Assert.Contains("employee '3'", exception.Message);
        Assert.Contains("06-10-2026", exception.Message);
    }

    [Fact]
    public void Names_with_nbsp_and_html_entities_are_cleaned()
    {
        var report = AttendanceParser.Parse(TestReports.DailyFlexible(
            new TestReports.DailyRow("1", "3", "R&amp;D&nbsp;GUY&nbsp;", "06-10-2026", "06:51", "19:00")));

        Assert.Equal("R&D GUY", report.Records[0].Name);
    }

    [Fact]
    public void Timestamp_style_dates_in_daily_rows_are_accepted()
    {
        var report = AttendanceParser.Parse(TestReports.DailyFlexible(
            new TestReports.DailyRow("1", "3", "AJITH.S.S", "06-10-2026 00:00", "06:51", "19:00")));

        Assert.Equal(new DateOnly(2026, 10, 6), report.Records[0].Date);
    }

    [Fact]
    public void One_employee_with_no_punches_at_all_is_fully_absent()
    {
        var days = new[] { 1, 2, 3 };
        var report = AttendanceParser.Parse(TestReports.MonthlyBlocksFlexible(
            "01-09-2026", days,
            new[] { new TestReports.BlockEmployee("3", "AJITH.S.S", days,
                new string?[] { null, null, null }, new string?[] { null, null, null }) }));

        Assert.Equal(3, report.Records.Count);
        Assert.All(report.Records, r =>
        {
            Assert.Null(r.InPunch);
            Assert.Null(r.OutPunch);
        });
    }
}

public class IdAndFormatRobustnessTests
{
    [Fact]
    public void Leading_zero_ids_are_preserved_as_text_in_the_excel()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xlsx");
        try
        {
            TemplateWriter.WriteToFile(new[]
            {
                new AttendanceRecord("0013", "PRESERVED", "Male", new DateOnly(2026, 9, 1), "06:51", "19:00", Order: 0),
            }, path);

            using var workbook = new XLWorkbook(path);
            var ws = workbook.Worksheets.Single();
            Assert.Equal("0013", ws.Cell(TemplateSpec.FirstDataRow, 2).GetString());
            Assert.True(ws.Cell(TemplateSpec.FirstDataRow, 2).DataType == XLDataType.Text);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Normal_ids_are_still_written_as_numbers()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xlsx");
        try
        {
            TemplateWriter.WriteToFile(new[]
            {
                new AttendanceRecord("13", "NORMAL", "Male", new DateOnly(2026, 9, 1), "06:51", "19:00", Order: 0),
            }, path);

            using var workbook = new XLWorkbook(path);
            var ws = workbook.Worksheets.Single();
            Assert.True(ws.Cell(TemplateSpec.FirstDataRow, 2).DataType == XLDataType.Number);
            Assert.Equal(13, ws.Cell(TemplateSpec.FirstDataRow, 2).GetDouble());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Slashed_and_iso_dates_are_accepted_in_daily_rows()
    {
        var report = AttendanceParser.Parse(TestReports.DailyFlexible(
            new TestReports.DailyRow("1", "3", "A", "06/10/2026", "06:51", "19:00"),
            new TestReports.DailyRow("2", "4", "B", "2026-10-07", "06:51", "19:00")));

        Assert.Equal(new DateOnly(2026, 10, 6), report.Records[0].Date);
        Assert.Equal(new DateOnly(2026, 10, 7), report.Records[1].Date);
    }
}
