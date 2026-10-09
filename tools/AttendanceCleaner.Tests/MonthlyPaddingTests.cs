using System.Globalization;
using AttendanceCleaner.Core;
using Xunit;

namespace AttendanceCleaner.Tests;

/// <summary>Regressions for the fixed-width empty day slots in monthly block exports.</summary>
public sealed class MonthlyPaddingTests
{
    public static IEnumerable<object[]> CalendarMonths()
    {
        foreach (var year in CalendarValidationRange.Years)
            foreach (var month in Enumerable.Range(1, 12))
                foreach (var padding in new[] { "-", "" })
                    yield return [year, month, padding];
    }

    [Theory]
    [MemberData(nameof(CalendarMonths))]
    public void Fixed_width_monthly_blocks_preserve_only_real_dates_through_2036(
        int year, int month, string padding)
    {
        var first = new DateOnly(year, month, 1);
        var dayCount = DateTime.DaysInMonth(year, month);
        var dates = Enumerable.Range(0, dayCount).Select(first.AddDays).ToArray();
        var header = dates.Select(date => DayText(date.Day))
            .Concat(Enumerable.Repeat(padding, 31 - dayCount)).ToArray();

        var report = AttendanceParser.Parse(MonthlyBlock(first, dates[^1], header, dayCount));

        AssertRecords(report, dates);
    }

    [Fact]
    public void September_31_slot_export_keeps_thirty_days_and_ignores_the_final_dash_slot()
    {
        var from = new DateOnly(2026, 9, 1);
        var dates = Enumerable.Range(0, 30).Select(from.AddDays).ToArray();
        var header = Enumerable.Range(1, 30).Select(DayText).Append("-").ToArray();

        var report = AttendanceParser.Parse(MonthlyBlock(from, dates[^1], header, dates.Length));

        AssertRecords(report, dates);
        Assert.Equal(new DateOnly(2026, 9, 30), report.Records[^1].Date);
        Assert.DoesNotContain(report.Records, record => record.Date.Month != 9);
    }

    [Theory]
    [InlineData("-")]
    [InlineData("")]
    public void Partial_month_padding_creates_no_records_for_unexported_days(string padding)
    {
        var first = new DateOnly(2026, 10, 5);
        var dates = Enumerable.Range(0, 16).Select(first.AddDays).ToArray();
        var header = dates.Select(date => DayText(date.Day))
            .Concat(Enumerable.Repeat(padding, 31 - dates.Length)).ToArray();

        var report = AttendanceParser.Parse(MonthlyBlock(first, dates[^1], header, dates.Length));

        AssertRecords(report, dates);
        Assert.All(report.Records, record => Assert.InRange(record.Date, first, dates[^1]));
    }

    [Theory]
    [InlineData("-")]
    [InlineData("")]
    public void Skipped_exported_days_remain_skipped_before_trailing_padding(string padding)
    {
        DateOnly[] dates = [new(2026, 10, 5), new(2026, 10, 7), new(2026, 10, 20)];
        var header = dates.Select(date => DayText(date.Day))
            .Concat(Enumerable.Repeat(padding, 31 - dates.Length)).ToArray();

        var report = AttendanceParser.Parse(MonthlyBlock(dates[0], dates[^1], header, dates.Length));

        AssertRecords(report, dates);
    }

    [Fact]
    public void Mixed_empty_and_dash_padding_is_allowed_after_the_final_numeric_day()
    {
        DateOnly[] dates = [new(2026, 10, 5), new(2026, 10, 6)];
        string[] header = ["5", "6", "", "-", "", "-"];

        var report = AttendanceParser.Parse(MonthlyBlock(dates[0], dates[^1], header, dates.Length));

        AssertRecords(report, dates);
    }

    [Theory]
    [InlineData("-")]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("0.00")]
    public void Empty_dash_and_zero_metric_placeholders_are_ignored_in_undated_padding(string placeholder)
    {
        DateOnly[] dates = [new(2026, 10, 5), new(2026, 10, 6)];
        string[] header = ["5", "6", "-", "-"];

        var report = AttendanceParser.Parse(MonthlyBlock(dates[0], dates[^1], header,
            dates.Length, paddingValue: placeholder, numericPaddingValue: placeholder));

        AssertRecords(report, dates);
    }

    [Theory]
    [InlineData("-")]
    [InlineData("")]
    public void Empty_or_dash_headers_before_a_later_numeric_day_reject_the_export(string missingDay)
    {
        var from = new DateOnly(2026, 10, 5);
        var to = new DateOnly(2026, 10, 7);
        string[] header = ["5", missingDay, "7", "-"];

        Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(MonthlyBlock(from, to, header, 3)));
    }

    [Theory]
    [InlineData("-")]
    [InlineData("")]
    public void Leading_undated_slots_cannot_be_treated_as_trailing_padding(string missingDay)
    {
        var from = new DateOnly(2026, 10, 5);
        var to = new DateOnly(2026, 10, 6);
        string[] header = [missingDay, "5", "6", "-"];

        Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(MonthlyBlock(from, to, header, 3)));
    }

    [Theory]
    [InlineData("Check-in1", "07:30")]
    [InlineData("Check-in1", "00:00")]
    [InlineData("Check-out1", "16:30")]
    [InlineData("Check-out1", "00:00")]
    [InlineData("Attended", "9")]
    [InlineData("Attended", "0.5")]
    [InlineData("OT", "1")]
    [InlineData("OT", "0.25")]
    [InlineData("Status", "P")]
    [InlineData("Status", "A")]
    [InlineData("Status", "W")]
    public void Actual_attendance_in_a_trailing_undated_slot_is_rejected(string metric, string value)
    {
        var from = new DateOnly(2026, 10, 5);
        var to = new DateOnly(2026, 10, 6);
        string[] header = ["5", "6", "-", "-"];

        var error = Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(MonthlyBlock(
            from, to, header, 2, paddedMetric: metric, paddedValue: value)));

        Assert.Contains("without a date", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("0")]
    [InlineData("32")]
    [InlineData("31")]
    public void Arbitrary_text_zero_and_invalid_calendar_days_do_not_become_padding(string invalidHeader)
    {
        var from = new DateOnly(2026, 9, 1);
        var to = new DateOnly(2026, 9, 30);
        var header = Enumerable.Range(1, 30).Select(DayText).Append(invalidHeader).ToArray();

        Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(MonthlyBlock(from, to, header, 30)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    public void A_header_containing_only_padding_has_no_valid_attendance_dates(string padding)
    {
        var date = new DateOnly(2026, 10, 5);

        Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(
            MonthlyBlock(date, date, [padding, padding, padding], 0)));
    }

    private static string MonthlyBlock(DateOnly from, DateOnly to, IReadOnlyList<string> header, int realColumns,
        string paddingValue = "-", string numericPaddingValue = "0", string? paddedMetric = null, string? paddedValue = null)
    {
        string MetricRow(string label, Func<int, string> realValue, bool numeric = false)
        {
            var values = Enumerable.Range(0, header.Count).Select(index => index < realColumns
                ? realValue(index)
                : index == realColumns && label == paddedMetric ? paddedValue!
                : numeric ? numericPaddingValue : paddingValue);
            return $"<tr><td>{label}</td>{Cells(values)}</tr>";
        }

        return $"""
            <html><body>
            <table><tr><td colspan="32,">Attendance Monthly Report</td></tr>
            <tr><td colspan="32,">From: {DateText(from)} 00:00 To: {DateText(to)} 23:59</td></tr></table>
            <table>
            <tr><td>Person ID</td><td>007</td><td>Employee Name</td><td>Padding Test Employee</td></tr>
            <tr><td>Date</td>{Cells(header)}</tr>
            {MetricRow("Check-in1", index => $"07:{index:00}")}
            {MetricRow("Check-out1", index => $"16:{index:00}")}
            {MetricRow("OT", _ => "0", numeric: true)}
            {MetricRow("Attended", _ => "9", numeric: true)}
            {MetricRow("Late", _ => "0", numeric: true)}
            {MetricRow("Early", _ => "0", numeric: true)}
            {MetricRow("Absent", _ => "0", numeric: true)}
            {MetricRow("Leave", _ => "0", numeric: true)}
            {MetricRow("Status", _ => "P")}
            </table></body></html>
            """;
    }

    private static void AssertRecords(ParsedReport report, IReadOnlyList<DateOnly> dates)
    {
        Assert.Equal(AttendanceExportFormat.MonthlyBlocks, report.Format);
        Assert.Equal(dates, report.Records.Select(record => record.Date));
        Assert.Equal(dates.Count, report.Records.Count);
        for (var index = 0; index < dates.Count; index++)
        {
            var record = report.Records[index];
            Assert.Equal("007", record.Id);
            Assert.Equal("Padding Test Employee", record.Name);
            Assert.Equal($"07:{index:00}", record.InPunch);
            Assert.Equal($"16:{index:00}", record.OutPunch);
            Assert.Equal("9", record.Attended);
            Assert.Equal("0", record.Overtime);
            Assert.Equal("P", record.Status);
        }
    }

    private static string DayText(int day) => day.ToString(CultureInfo.InvariantCulture);
    private static string DateText(DateOnly date) => date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
    private static string Cells(IEnumerable<string> values) =>
        string.Concat(values.Select(value => $"<td>{value}</td>"));
}
