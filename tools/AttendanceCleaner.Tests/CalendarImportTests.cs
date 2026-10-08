using System.Globalization;
using AttendanceCleaner.Core;
using Xunit;

namespace AttendanceCleaner.Tests;

/// <summary>Calendar and range regressions for the attendance software's HTML-as-.xls exports.</summary>
public sealed class CalendarImportTests
{
    public static IEnumerable<object[]> CalendarMonths()
    {
        foreach (var format in Enum.GetValues<AttendanceExportFormat>())
            foreach (var year in CalendarValidationRange.Years)
                foreach (var month in Enumerable.Range(1, 12))
                    yield return [format, year, month];
    }

    [Theory]
    [MemberData(nameof(CalendarMonths))]
    public void Every_month_preserves_exact_dates_and_punches(
        AttendanceExportFormat format, int year, int month)
    {
        var dates = Enumerable.Range(1, DateTime.DaysInMonth(year, month))
            .Select(day => new DateOnly(year, month, day)).ToArray();

        var report = AttendanceParser.Parse(Report(format, dates));

        Assert.Equal(format, report.Format);
        AssertRecords(report, dates);
    }

    [Theory]
    [InlineData("6-10-2026", 2026, 10, 6)]
    [InlineData("6/10/2026", 2026, 10, 6)]
    [InlineData("10/6/2026", 2026, 6, 10)]
    [InlineData("1.2.2028", 2028, 2, 1)]
    [InlineData("29-2-2028", 2028, 2, 29)]
    [InlineData("6-10-2026 00:00", 2026, 10, 6)]
    [InlineData("2026-10-06T00:00:00", 2026, 10, 6)]
    public void Daily_dates_accept_unpadded_day_first_and_iso_values(
        string dateText, int year, int month, int day)
    {
        var report = AttendanceParser.Parse(Daily([dateText]));

        AssertRecords(report, [new DateOnly(year, month, day)]);
    }

    [Theory]
    [InlineData("31-09-2026")]
    [InlineData("29-02-2027")]
    [InlineData("10/31/2026")]
    [InlineData("01-01-0000")]
    [InlineData("not-a-date")]
    [InlineData("")]
    public void Invalid_daily_date_rejects_the_report_instead_of_dropping_the_employee_row(string dateText)
    {
        var html = Daily(["06-10-2026", dateText, "08-10-2026"]);

        var exception = Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html));

        Assert.Contains("date", exception.Message, StringComparison.OrdinalIgnoreCase);
        if (dateText.Length > 0) Assert.Contains(dateText, exception.Message);
    }

    public static IEnumerable<object[]> MonthlyDateFormats()
    {
        foreach (var format in MonthlyFormats())
        {
            yield return [format, "2026-10-06", "2026-10-09"];
            yield return [format, "6-10-2026", "9-10-2026"];
            yield return [format, "6/10/2026", "9/10/2026"];
            yield return [format, "6.10.2026", "9.10.2026"];
            yield return [format, "2026-10-06 00:00", "2026-10-09 23:59"];
        }
    }

    [Theory]
    [MemberData(nameof(MonthlyDateFormats))]
    public void Monthly_ranges_accept_iso_and_unpadded_day_first_dates(
        AttendanceExportFormat format, string from, string to)
    {
        var dates = new[] { new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 7), new DateOnly(2026, 10, 9) };

        var report = AttendanceParser.Parse(Monthly(format, from, to, dates.Select(date => date.Day).ToArray()));

        Assert.Equal(format, report.Format);
        AssertRecords(report, dates);
    }

    public static IEnumerable<object[]> ShortMonthlyRanges()
    {
        foreach (var format in MonthlyFormats())
            foreach (var year in CalendarValidationRange.Years)
                foreach (var month in Enumerable.Range(1, 12))
                {
                    var lastDay = DateTime.DaysInMonth(year, month);
                    yield return [format, new DateOnly[] { new(year, month, lastDay) }];
                    yield return [format, new DateOnly[] { new(year, month, 5), new(year, month, 7), new(year, month, lastDay) }];
                }
    }

    [Theory]
    [MemberData(nameof(ShortMonthlyRanges))]
    public void Monthly_reports_accept_one_day_and_short_non_first_day_ranges(
        AttendanceExportFormat format, DateOnly[] dates)
    {
        var report = AttendanceParser.Parse(Report(format, dates));

        Assert.Equal(format, report.Format);
        AssertRecords(report, dates);
    }

    public static IEnumerable<object[]> CrossMonthRanges()
    {
        foreach (var format in Enum.GetValues<AttendanceExportFormat>())
            foreach (var year in CalendarValidationRange.Years)
                foreach (var month in Enumerable.Range(1, 12))
                {
                    var lastDay = DateTime.DaysInMonth(year, month);
                    var nextMonth = new DateOnly(year, month, 1).AddMonths(1);
                    yield return [format, new DateOnly[]
                    {
                        new(year, month, lastDay - 2), new(year, month, lastDay),
                        nextMonth, nextMonth.AddDays(3),
                    }];
                }
    }

    [Theory]
    [MemberData(nameof(CrossMonthRanges))]
    public void Skipped_day_columns_follow_the_calendar_across_month_and_year_boundaries(
        AttendanceExportFormat format, DateOnly[] dates)
    {
        var report = AttendanceParser.Parse(Report(format, dates));

        Assert.Equal(format, report.Format);
        AssertRecords(report, dates);
    }

    public static IEnumerable<object[]> FebruaryCalendars()
    {
        foreach (var format in Enum.GetValues<AttendanceExportFormat>())
            foreach (var year in CalendarValidationRange.Years)
                yield return [format, year, year is 2028 or 2032 or 2036];
    }

    [Theory]
    [MemberData(nameof(FebruaryCalendars))]
    public void February_29_is_kept_only_in_the_expected_leap_years(
        AttendanceExportFormat format, int year, bool leapYear)
    {
        var lastDay = leapYear ? 29 : 28;
        var dates = new[] { new DateOnly(year, 2, lastDay) };
        var report = AttendanceParser.Parse(Report(format, dates));

        AssertRecords(report, dates);
        if (leapYear) return;
        var invalidDate = $"29-02-{year}";
        var invalidReport = format == AttendanceExportFormat.DailyReport
            ? Daily([invalidDate])
            : Monthly(format, $"01-02-{year}", $"28-02-{year}", [28, 29]);

        Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(invalidReport));
    }

    [Theory]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime)]
    [InlineData(MonthlyTemplateKind.InAndOut)]
    public void Monthly_templates_explain_that_a_cross_month_import_requires_separate_months(
        MonthlyTemplateKind kind)
    {
        var dates = new[] { new DateOnly(2026, 12, 31), new DateOnly(2027, 1, 1) };
        var report = AttendanceParser.Parse(Report(AttendanceExportFormat.MonthlyRows, dates));
        AssertRecords(report, dates);
        using var stream = new MemoryStream();

        var exception = Assert.Throws<InvalidDataException>(() =>
            MonthlyTemplateWriter.Write(report.Records, Array.Empty<HolidayEntry>(), kind, stream));

        Assert.Contains("one calendar month", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(AttendanceExportFormat.MonthlyBlocks)]
    [InlineData(AttendanceExportFormat.MonthlyRows)]
    public void Labelled_from_date_overrides_unrelated_dates_before_the_report_range(AttendanceExportFormat format)
    {
        var dates = new[] { new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 12) };
        var html = Monthly(format, "10-11-2026", "12-11-2026", [10, 11, 12],
            beforeRange: "<tr><td>Archive created: 04-05-2024; generated on 01-08-2026</td></tr>");

        var report = AttendanceParser.Parse(html);

        AssertRecords(report, dates);
    }

    [Theory]
    [InlineData(AttendanceExportFormat.DailyReport)]
    [InlineData(AttendanceExportFormat.MonthlyBlocks)]
    [InlineData(AttendanceExportFormat.MonthlyRows)]
    public void Explicit_tbody_wrappers_preserve_the_report_rows(AttendanceExportFormat format)
    {
        var dates = new[] { new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 7), new DateOnly(2026, 10, 8) };

        var report = AttendanceParser.Parse(Report(format, dates, tbody: true));

        Assert.Equal(format, report.Format);
        AssertRecords(report, dates);
    }

    [Theory]
    [InlineData(AttendanceExportFormat.MonthlyBlocks, false)]
    [InlineData(AttendanceExportFormat.MonthlyBlocks, true)]
    [InlineData(AttendanceExportFormat.MonthlyRows, false)]
    [InlineData(AttendanceExportFormat.MonthlyRows, true)]
    public void Range_labels_and_dates_can_be_split_across_html_tags_and_cells(
        AttendanceExportFormat format, bool separateCells)
    {
        var range = separateCells
            ? "<tr><td><b>From:</b></td><td><i>6-10-2026</i></td><td><b>To:</b></td><td><span>9-10-2026</span></td></tr>"
            : "<tr><td>Fr<b>om:</b> <i>6-10-2026</i> T<span>o:</span> <b>9-10-2026</b></td></tr>";
        var dates = new[] { new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 7), new DateOnly(2026, 10, 9) };
        var html = Monthly(format, "6-10-2026", "9-10-2026", [6, 7, 9], tbody: true, rangeRow: range);

        var report = AttendanceParser.Parse(html);

        AssertRecords(report, dates);
    }

    public static IEnumerable<object[]> InvalidMonthlyRanges()
    {
        foreach (var format in MonthlyFormats())
        {
            yield return [format, "01-09-2026", "30-09-2026", new[] { 30, 31 }];
            yield return [format, "01-02-2027", "28-02-2027", new[] { 28, 29 }];
            yield return [format, "10-10-2026", "12-10-2026", new[] { 10, 10, 12 }];
            yield return [format, "06-10-2026", "08-10-2026", new[] { 5, 6, 7 }];
            yield return [format, "06-10-2026", "08-10-2026", new[] { 6, 7, 9 }];
            yield return [format, "10-10-2026", "06-10-2026", new[] { 10 }];
            yield return [format, "01-01-0000", "02-01-0000", new[] { 1, 2 }];
            yield return [format, "01-10-2026", "03-10-2026", new[] { 0, 1, 2 }];
            yield return [format, "01-10-2026", "31-10-2026", new[] { 1, 32 }];
            yield return [format, "10/31/2026", "11/01/2026", new[] { 31, 1 }];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidMonthlyRanges))]
    public void Impossible_duplicate_or_out_of_range_monthly_days_are_rejected(
        AttendanceExportFormat format, string from, string to, int[] days)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            AttendanceParser.Parse(Monthly(format, from, to, days)));

        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    [Theory]
    [InlineData(AttendanceExportFormat.MonthlyBlocks, "seven")]
    [InlineData(AttendanceExportFormat.MonthlyBlocks, "")]
    [InlineData(AttendanceExportFormat.MonthlyRows, "seven")]
    [InlineData(AttendanceExportFormat.MonthlyRows, "")]
    public void Corrupt_internal_day_column_rejects_the_report_instead_of_dropping_dates(
        AttendanceExportFormat format, string invalidDay)
    {
        var html = Monthly(format, "06-10-2026", "08-10-2026", [6, 7, 8])
            .Replace("<td>7</td>", $"<td>{invalidDay}</td>", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html));
    }

    public static IEnumerable<object[]> MonthlyMetrics()
    {
        foreach (var format in MonthlyFormats())
            foreach (var metric in new[] { "Check-in1", "Check-out1", "Attended", "OT", "Status" })
                yield return [format, metric];
    }

    [Theory]
    [MemberData(nameof(MonthlyMetrics))]
    public void Truncated_monthly_metric_row_rejects_the_report_instead_of_inventing_missing_punches(
        AttendanceExportFormat format, string metric)
    {
        var completeRow = MetricRow(format, metric, MetricValues(metric));
        var truncatedRow = MetricRow(format, metric, MetricValues(metric)[..2], includeSummary: false);
        var html = DetailedMonthly(format);
        Assert.Contains(completeRow, html);
        html = html.Replace(completeRow, truncatedRow, StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html));

        Assert.Contains(metric, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(AttendanceExportFormat.MonthlyBlocks, "Check-in1")]
    [InlineData(AttendanceExportFormat.MonthlyBlocks, "Check-out1")]
    [InlineData(AttendanceExportFormat.MonthlyRows, "Check-in1")]
    [InlineData(AttendanceExportFormat.MonthlyRows, "Check-out1")]
    public void Each_employee_requires_both_punch_metric_rows_even_when_another_employee_is_complete(
        AttendanceExportFormat format, string missingMetric)
    {
        var completeRow = MetricRow(format, missingMetric, MetricValues(missingMetric));
        var html = DetailedMonthly(format, includeSecondEmployee: true);
        Assert.Contains(completeRow, html);
        html = html.Replace(completeRow, "", StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html));

        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    [Fact]
    public void Monthly_block_identity_without_a_date_row_rejects_the_report_instead_of_dropping_the_employee()
    {
        var html = DetailedMonthly(AttendanceExportFormat.MonthlyBlocks).Replace("</body>",
            Table("<tr><td>Person ID</td><td>008</td><td>Employee Name</td><td>Missing Dates Employee</td></tr>", false)
            + "</body>", StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html));

        Assert.Contains("008", exception.Message);
    }

    [Fact]
    public void Monthly_rows_preserve_pipe_characters_in_employee_id_and_name()
    {
        const string id = "team|007";
        const string name = "Calendar | Employee";
        var html = Monthly(AttendanceExportFormat.MonthlyRows, "06-10-2026", "08-10-2026", [6, 7, 8])
            .Replace("<td>007</td>", $"<td>{id}</td>", StringComparison.Ordinal)
            .Replace("Calendar Employee", name, StringComparison.Ordinal);

        var report = AttendanceParser.Parse(html);

        Assert.Equal(3, report.Records.Count);
        for (var index = 0; index < report.Records.Count; index++)
        {
            Assert.Equal(id, report.Records[index].Id);
            Assert.Equal(name, report.Records[index].Name);
            Assert.Equal(new DateOnly(2026, 10, 6 + index), report.Records[index].Date);
            Assert.Equal(InPunch(index), report.Records[index].InPunch);
            Assert.Equal(OutPunch(index), report.Records[index].OutPunch);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ParseFile_checks_the_workbook_signature_before_reading_it_as_html(bool zipWorkbook)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".xls");
        byte[] signature = zipWorkbook
            ? [0x50, 0x4B, 0x03, 0x04, 0x00, 0x00, 0x00, 0x00]
            : [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        try
        {
            File.WriteAllBytes(path, signature);

            var exception = Assert.Throws<InvalidDataException>(() => AttendanceParser.ParseFile(path));

            Assert.Contains(zipWorkbook ? "Excel workbook" : "binary Excel", exception.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(AttendanceExportFormat.MonthlyBlocks)]
    [InlineData(AttendanceExportFormat.MonthlyRows)]
    public void Monthly_title_can_declare_an_unlabelled_start_date_followed_by_to(AttendanceExportFormat format)
    {
        var html = Monthly(format, "06-10-2026", "08-10-2026", [6, 7, 8],
            rangeRow: "<tr><td>06-10-2026 00:00 To 08-10-2026 23:59</td></tr>");

        var report = AttendanceParser.Parse(html);

        AssertRecords(report, [new(2026, 10, 6), new(2026, 10, 7), new(2026, 10, 8)]);
    }

    [Theory]
    [InlineData(AttendanceExportFormat.MonthlyBlocks, "From: 06-10-2026")]
    [InlineData(AttendanceExportFormat.MonthlyBlocks, "To: 08-10-2026")]
    [InlineData(AttendanceExportFormat.MonthlyBlocks, "October report")]
    [InlineData(AttendanceExportFormat.MonthlyRows, "From: 06-10-2026")]
    [InlineData(AttendanceExportFormat.MonthlyRows, "To: 08-10-2026")]
    [InlineData(AttendanceExportFormat.MonthlyRows, "October report")]
    public void Monthly_reports_require_a_complete_declared_date_range(
        AttendanceExportFormat format, string title)
    {
        var html = Monthly(format, "06-10-2026", "08-10-2026", [6, 7, 8],
            rangeRow: $"<tr><td>{title}</td></tr>");

        var exception = Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html));

        Assert.Contains("From/To", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(AttendanceExportFormat.MonthlyBlocks, 6)]
    [InlineData(AttendanceExportFormat.MonthlyBlocks, 8)]
    [InlineData(AttendanceExportFormat.MonthlyRows, 6)]
    [InlineData(AttendanceExportFormat.MonthlyRows, 8)]
    public void Blank_first_or_last_day_headers_cannot_discard_punches(AttendanceExportFormat format, int day)
    {
        var html = Monthly(format, "06-10-2026", "08-10-2026", [6, 7, 8])
            .Replace($"<td>{day}</td>", "<td></td>", StringComparison.Ordinal);

        Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html));
    }

    private static IEnumerable<AttendanceExportFormat> MonthlyFormats() =>
        [AttendanceExportFormat.MonthlyBlocks, AttendanceExportFormat.MonthlyRows];

    private static void AssertRecords(ParsedReport report, IReadOnlyList<DateOnly> dates)
    {
        Assert.Equal(dates.Count, report.Records.Count);
        for (var index = 0; index < dates.Count; index++)
        {
            var record = report.Records[index];
            Assert.Equal("007", record.Id);
            Assert.Equal("Calendar Employee", record.Name);
            Assert.Equal(dates[index], record.Date);
            Assert.Equal(InPunch(index), record.InPunch);
            Assert.Equal(OutPunch(index), record.OutPunch);
        }
    }

    // Each column has a distinguishable punch, including absent and single-punch days.
    private static string? InPunch(int index) => index % 5 == 1 ? null : $"06:{index:00}";
    private static string? OutPunch(int index) => index % 5 is 1 or 2 ? null : $"17:{index:00}";
    private static string DateText(DateOnly date) => date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
    private static string Cells(IEnumerable<string?> values) =>
        string.Concat(values.Select(value => $"<td>{value ?? "-"}</td>"));

    private static string Table(string rows, bool tbody) =>
        tbody ? $"<table><tbody>{rows}</tbody></table>" : $"<table>{rows}</table>";

    private static string?[] MetricValues(string metric) => metric switch
    {
        "Check-in1" => Enumerable.Range(0, 3).Select(InPunch).ToArray(),
        "Check-out1" => Enumerable.Range(0, 3).Select(OutPunch).ToArray(),
        "Attended" => ["9", "0", "8"],
        "OT" => ["2", "0", "1"],
        "Status" => ["P", "A", "P"],
        _ => throw new ArgumentException("Unknown test metric.", nameof(metric)),
    };

    private static string MetricRow(AttendanceExportFormat format, string metric, string?[] values,
        bool includeSummary = true, int no = 1, string id = "007", string name = "Calendar Employee")
    {
        var prefix = format == AttendanceExportFormat.MonthlyBlocks
            ? Cells([metric])
            : Cells([no.ToString(CultureInfo.InvariantCulture), id, name, metric]);
        var suffix = format == AttendanceExportFormat.MonthlyRows && includeSummary
            ? "<td>*</td><td>0</td>" : "";
        return $"<tr>{prefix}{Cells(values)}{suffix}</tr>";
    }

    private static string DetailedMonthly(AttendanceExportFormat format, bool includeSecondEmployee = false)
    {
        var html = Monthly(format, "06-10-2026", "08-10-2026", [6, 7, 8]);
        var extraRows = string.Concat(new[] { "Attended", "OT", "Status" }
            .Select(metric => MetricRow(format, metric, MetricValues(metric))));
        if (includeSecondEmployee)
        {
            if (format == AttendanceExportFormat.MonthlyBlocks)
                extraRows += "<tr><td>Person ID</td><td>008</td><td>Employee Name</td><td>Second Employee</td></tr>"
                    + "<tr><td>Date</td><td>6</td><td>7</td><td>8</td></tr>";
            extraRows += string.Concat(new[] { "Check-in1", "Check-out1", "Attended", "OT", "Status" }
                .Select(metric => MetricRow(format, metric, MetricValues(metric), no: 2,
                    id: "008", name: "Second Employee")));
        }
        return html.Replace("</table></body>", extraRows + "</table></body>", StringComparison.Ordinal);
    }

    private static string Report(AttendanceExportFormat format, DateOnly[] dates, bool tbody = false) =>
        format == AttendanceExportFormat.DailyReport
            ? Daily(dates.Select(DateText).ToArray(), tbody)
            : Monthly(format, DateText(dates[0]), DateText(dates[^1]),
                dates.Select(date => date.Day).ToArray(), tbody);

    private static string Daily(string[] dates, bool tbody = false)
    {
        var header = "<tr>" + Cells(["No.", "Person ID", "Name", "Gender", "Date", "Check-in", "Check-out"]) + "</tr>";
        var rows = string.Concat(dates.Select((date, index) => "<tr>" + Cells(
            [(index + 1).ToString(CultureInfo.InvariantCulture), "007", "Calendar Employee", "Male", date,
                InPunch(index), OutPunch(index)]) + "</tr>"));
        return $"<html><body>{Table(header, tbody)}{Table(rows, tbody)}</body></html>";
    }

    private static string Monthly(AttendanceExportFormat format, string from, string to, int[] days,
        bool tbody = false, string beforeRange = "", string? rangeRow = null)
    {
        var range = beforeRange + (rangeRow ?? $"<tr><td>From: {from} To: {to}</td></tr>");
        var dayCells = Cells(days.Select(day => day.ToString(CultureInfo.InvariantCulture)));
        var ins = Cells(Enumerable.Range(0, days.Length).Select(InPunch));
        var outs = Cells(Enumerable.Range(0, days.Length).Select(OutPunch));
        string headers, rows;
        if (format == AttendanceExportFormat.MonthlyBlocks)
        {
            headers = "";
            rows = $"""
                <tr><td>Person ID</td><td>007</td><td>Employee Name</td><td>Calendar Employee</td><td>Joining Date</td><td>01-01-2024</td></tr>
                <tr><td>Date</td>{dayCells}</tr>
                <tr><td>Check-in1</td>{ins}</tr>
                <tr><td>Check-out1</td>{outs}</tr>
                """;
        }
        else
        {
            headers = $"<tr><td>No.</td><td>Person ID</td><td>Name</td><td></td>{dayCells}<td>*</td><td>Work(Day(s))</td></tr>";
            rows = $"""
                <tr><td>1</td><td>007</td><td>Calendar Employee</td><td>Check-in1</td>{ins}<td>*</td><td>0</td></tr>
                <tr><td>1</td><td>007</td><td>Calendar Employee</td><td>Check-out1</td>{outs}<td>*</td><td>0</td></tr>
                """;
        }
        return $"<html><body>{Table(range + headers, tbody)}{Table(rows, tbody)}</body></html>";
    }
}
