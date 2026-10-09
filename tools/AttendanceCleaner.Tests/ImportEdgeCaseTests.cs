using System.Globalization;
using AttendanceCleaner.Core;
using Xunit;

namespace AttendanceCleaner.Tests;

public sealed class ImportEdgeCaseTests
{
    public static IEnumerable<object[]> PaddedPerformanceMonths()
    {
        for (var year = 2026; year <= 2036; year++)
            for (var month = 1; month <= 12; month++)
                foreach (var padding in new[] { "", "-" })
                    yield return [year, month, padding];
    }

    [Theory]
    [MemberData(nameof(PaddedPerformanceMonths))]
    public void Performance_fixed_width_padding_preserves_only_exported_dates(int year, int month, string padding)
    {
        var from = new DateOnly(year, month, 1);
        var count = DateTime.DaysInMonth(year, month);
        var header = Enumerable.Range(1, count).Select(day => day.ToString(CultureInfo.InvariantCulture))
            .Concat(Enumerable.Repeat(padding, 31 - count)).ToArray();
        var report = AttendanceParser.Parse(Performance(from, from.AddDays(count - 1), header));

        Assert.Equal(count, report.Records.Count);
        Assert.Equal(Enumerable.Range(0, count).Select(from.AddDays), report.Records.Select(record => record.Date));
        Assert.All(report.Records, record =>
        {
            Assert.Equal("06:30", record.InPunch);
            Assert.Equal("19:30", record.OutPunch);
            Assert.Equal("12.5", record.Attended);
            Assert.Equal("3", record.Overtime);
            Assert.Equal("P", record.Status);
        });
    }

    [Fact]
    public void Performance_padding_before_summary_preserves_summary_totals()
    {
        var date = new DateOnly(2026, 9, 30);
        var report = AttendanceParser.Parse(Performance(date, date, ["30", "-", ""], summaries: ["-", "1", "2", "0.5", "1"]));

        Assert.Single(report.Records);
        var totals = Assert.Single(report.EmployeeTotals!);
        Assert.Equal(1m, totals.AbsentDays);
        Assert.Equal(2m, totals.AttendedDays);
        Assert.Equal(1.5m, totals.LeaveDays);
    }

    [Theory]
    [InlineData("06:30")]
    [InlineData("00:00")]
    [InlineData("abc")]
    public void Performance_rejects_punches_in_undated_padding(string value)
    {
        var date = new DateOnly(2026, 9, 30);
        var html = Performance(date, date, ["30", "-"], replaceMetric: "Check-in1", replaceValues: ["06:30", value]);
        var exception = Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html));

        Assert.Contains("without a date column", exception.Message);
        Assert.Contains("employee '007'", exception.Message);
    }

    [Fact]
    public void Performance_rejects_extra_unheaded_attendance_cells()
    {
        var date = new DateOnly(2026, 9, 30);
        var html = Performance(date, date, ["30"], replaceMetric: "Check-out1", replaceValues: ["19:30", "20:00"]);

        Assert.Contains("without a date column", Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html)).Message);
    }

    [Fact]
    public void Performance_rejects_extra_unheaded_cells_after_summary_columns()
    {
        var date = new DateOnly(2026, 9, 30);
        var html = Performance(date, date, ["30"], summaries: ["-", "1", "2", "0.5", "1", "06:30"]);

        Assert.Contains("without a date column", Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html)).Message);
    }

    [Theory]
    [InlineData("-")]
    [InlineData("")]
    public void Performance_rejects_leading_and_internal_undated_headers(string missing)
    {
        var from = new DateOnly(2026, 9, 1);
        Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(Performance(from, from.AddDays(1), [missing, "1", "2"])));
        Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(Performance(from, from.AddDays(1), ["1", missing, "2"])));
    }

    [Fact]
    public void Performance_partial_and_cross_month_ranges_keep_physical_columns()
    {
        var from = new DateOnly(2026, 12, 30);
        var report = AttendanceParser.Parse(Performance(from, new DateOnly(2027, 1, 3), ["30", "31", "1", "3", "-"]));

        Assert.Equal(new DateOnly[] { from, from.AddDays(1), from.AddDays(2), from.AddDays(4) }, report.Records.Select(record => record.Date));
        Assert.Equal(4, report.Records.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Malformed_punches_are_rejected_with_employee_date_and_metric(int layout)
    {
        foreach (var punch in new[] { "24:00", "06:99", "07:00:99", "07:60:00", "abc", "7:3", "0" })
        {
            var html = Layout(layout, "Check-in1", punch);
            var exception = Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html));
            Assert.Contains(punch, exception.Message);
            Assert.Contains("007", exception.Message);
            Assert.Contains("Edge Employee", exception.Message);
            Assert.Contains("30-09-2026", exception.Message);
            Assert.Contains("Check-in", exception.Message);
        }
    }

    [Theory]
    [InlineData("6:30", "06:30")]
    [InlineData("06:30:59", "06:30")]
    [InlineData("12:00 AM", "00:00")]
    [InlineData("12:00 PM", "12:00")]
    [InlineData("7:30 pm", "19:30")]
    [InlineData("07:30:45 PM", "19:30")]
    [InlineData("7:30PM", "19:30")]
    [InlineData("00:00", "00:00")]
    [InlineData("-", null)]
    [InlineData("", null)]
    public void Valid_punch_formats_normalize_consistently_across_layouts(string punch, string? expected)
    {
        for (var layout = 0; layout < 3; layout++)
            Assert.Equal(expected, Assert.Single(AttendanceParser.Parse(Layout(layout, "Check-in1", punch)).Records).InPunch);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Invalid_source_durations_are_rejected_with_context(int layout)
    {
        foreach (var label in new[] { "Attended", "OT" })
            foreach (var value in new[] { "abc", "-1", "25", "24:01", "12:99", "1,5", "1.5:00" })
            {
                var exception = Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(Layout(layout, label, value)));
                Assert.Contains(label, exception.Message);
                Assert.Contains(value, exception.Message);
                Assert.Contains("007", exception.Message);
                Assert.Contains("30-09-2026", exception.Message);
            }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("12.5")]
    [InlineData("12:30")]
    [InlineData("12:30:00")]
    [InlineData("24")]
    [InlineData("-")]
    [InlineData("")]
    public void Valid_source_durations_survive_all_layouts(string hours)
    {
        for (var layout = 0; layout < 3; layout++)
        {
            var record = Assert.Single(AttendanceParser.Parse(Layout(layout, "Attended", hours)).Records);
            Assert.Equal(string.IsNullOrEmpty(hours) || hours == "-" ? null : hours, record.Attended);
        }
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1,5")]
    public void Invalid_aggregate_totals_are_not_silently_ignored_or_misread(string value)
    {
        var date = new DateOnly(2026, 9, 30);
        foreach (var column in Enumerable.Range(1, 4))
        {
            var totals = new[] { "-", "1", "2", "0.5", "1" };
            totals[column] = value;
            var exception = Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(Performance(date, date, ["30"], summaries: totals)));
            Assert.Contains("summary total", exception.Message);
            Assert.Contains(value, exception.Message);
            Assert.Contains("007", exception.Message);
        }
    }

    [Fact]
    public void Daily_reordered_and_repeated_headers_keep_identity_and_metrics_aligned()
    {
        var firstHeader = Row("Name", "Date", "Check-out", "No.", "Status", "OT", "Person ID", "Check-in", "Attended");
        var first = Row("Edge Employee", "30-09-2026", "19:30", "1", "LV", "3", "007", "06:30", "12.5");
        var secondHeader = Row("No.", "Person ID", "Name", "Check-in", "Check-out", "Date", "Attended", "OT", "Status");
        var second = Row("2", "008", "Second Employee", "08:00", "17:00", "01-10-2026", "9", "0", "P");
        var report = AttendanceParser.Parse(Wrap(firstHeader + first + secondHeader + second));

        Assert.Equal(2, report.Records.Count);
        Assert.Equal("007", report.Records[0].Id);
        Assert.Equal("LV", report.Records[0].Status);
        Assert.Equal("3", report.Records[0].Overtime);
        Assert.Equal("008", report.Records[1].Id);
        Assert.Equal(new DateOnly(2026, 10, 1), report.Records[1].Date);
        Assert.Equal("08:00", report.Records[1].InPunch);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Recognizable_attendance_rows_with_invalid_serial_numbers_are_rejected(int layout)
    {
        var html = Layout(layout, "Check-in1", "06:30").Replace("<td>1</td><td>007</td>", "<td>broken</td><td>007</td>", StringComparison.Ordinal);
        Assert.Contains("row number", Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html)).Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Employee_rows_with_missing_identity_are_rejected(int layout)
    {
        var html = Layout(layout, "Check-in1", "06:30").Replace("<td>007</td>", "<td></td>", StringComparison.Ordinal);
        Assert.Contains("Person ID", Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html)).Message);
    }

    [Fact]
    public void Monthly_metrics_before_date_columns_are_rejected()
    {
        var html = Layout(1, "Check-in1", "06:30").Replace(Row("Date", "30"), "", StringComparison.Ordinal)
            .Replace(Row("Status", "P"), Row("Status", "P") + Row("Date", "30"), StringComparison.Ordinal);

        Assert.Contains("before its date", Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html)).Message);
    }

    [Fact]
    public void Conflicting_repeated_performance_date_headers_are_rejected()
    {
        var from = new DateOnly(2026, 9, 1);
        var html = Performance(from, from.AddDays(1), ["1"])
            .Replace("</table></body>", Row("No.", "Person ID", "Name", "", "2") + "</table></body>", StringComparison.Ordinal);

        Assert.Contains("conflicting date", Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html)).Message);
    }

    [Fact]
    public void Equivalent_repeated_performance_headers_are_allowed()
    {
        var date = new DateOnly(2026, 9, 30);
        var html = Performance(date, date, ["30", "-"])
            .Replace("</table></body>", Row("No.", "Person ID", "Name", "", "30", "-") + "</table></body>", StringComparison.Ordinal);

        Assert.Single(AttendanceParser.Parse(html).Records);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Equivalent_repeated_duration_and_status_rows_are_allowed(int layout)
    {
        var html = Layout(layout, "Attended", "12.5");
        var extra = layout == 1 ? Row("Attended", "12:30") + Row("Status", "p")
            : Row("1", "007", "Edge Employee", "Attended", "12:30") + Row("1", "007", "Edge Employee", "Status", "p");
        html = html.Replace("</table></body>", extra + "</table></body>", StringComparison.Ordinal);
        var record = Assert.Single(AttendanceParser.Parse(html).Records);

        Assert.Equal("12:30", record.Attended);
        Assert.Equal("p", record.Status);
    }

    [Fact]
    public void Conflicting_report_ranges_are_rejected_before_dates_are_misattributed()
    {
        var html = Layout(1, "Check-in1", "06:30")
            .Replace("</table></body>", Row("From: 01-10-2026 To: 31-10-2026") + "</table></body>", StringComparison.Ordinal);

        Assert.Contains("conflicting From/To", Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html)).Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Date_range_text_in_employee_names_does_not_change_report_metadata(int layout)
    {
        const string name = "From: 01-10-2030 To: 31-10-2030";
        var html = Layout(layout, "Check-in1", "06:30").Replace("Edge Employee", name, StringComparison.Ordinal);
        var record = Assert.Single(AttendanceParser.Parse(html).Records);

        Assert.Equal(name, record.Name);
        Assert.Equal(new DateOnly(2026, 9, 30), record.Date);
    }

    [Fact]
    public void Daily_row_missing_a_declared_metric_cell_is_rejected()
    {
        var html = Wrap(Row("No.", "Person ID", "Name", "Date", "Check-in", "Check-out", "OT", "Status")
            + Row("1", "007", "Edge Employee", "30-09-2026", "06:30", "19:30"));

        Assert.Contains("incomplete", Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html)).Message);
    }

    [Fact]
    public void Department_metadata_can_share_the_date_range_title_row()
    {
        var html = Layout(2, "Check-in1", "06:30").Replace(Row("From: 30-09-2026 To: 30-09-2026"),
            Row("Department", "Assembly", "From: 30-09-2026 To: 30-09-2026"), StringComparison.Ordinal);

        Assert.Equal(new DateOnly(2026, 9, 30), Assert.Single(AttendanceParser.Parse(html).Records).Date);
    }

    [Fact]
    public void Daily_employee_row_with_bad_serial_and_date_cannot_be_silently_skipped()
    {
        var html = Layout(0, "Check-in1", "06:30").Replace("</table></body>",
            Row("broken", "008", "Second Employee", "not-a-date", "07:00", "17:00", "9", "0", "P")
            + "</table></body>", StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html));
        Assert.Contains("008", exception.Message);
        Assert.Contains("row number", exception.Message);
    }

    [Fact]
    public void Daily_unheaded_extra_attendance_is_rejected()
    {
        var html = Layout(0, "Check-in1", "06:30").Replace(Row("1", "007", "Edge Employee", "30-09-2026", "06:30", "19:30", "12.5", "3", "P"),
            Row("1", "007", "Edge Employee", "30-09-2026", "06:30", "19:30", "12.5", "3", "P", "20:00"), StringComparison.Ordinal);

        Assert.Contains("without a column heading", Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html)).Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Additional_populated_punch_pairs_are_explained_instead_of_silently_ignored(int layout)
    {
        var html = AdditionalPair(layout, "07:00", "17:00");
        var exception = Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html));

        Assert.Contains("additional Check-in/Check-out", exception.Message);
        Assert.Contains("007", exception.Message);
        Assert.Contains("Multiple shifts", exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Unused_additional_punch_pairs_are_allowed(int layout)
    {
        var record = Assert.Single(AttendanceParser.Parse(AdditionalPair(layout, "-", "")).Records);

        Assert.Equal("06:30", record.InPunch);
        Assert.Equal("19:30", record.OutPunch);
    }

    [Fact]
    public void Daily_second_pair_cannot_be_misidentified_as_first_pair()
    {
        var html = Wrap(Row("No.", "Person ID", "Name", "Date", "Check-in2", "Check-out2")
            + Row("1", "007", "Edge Employee", "30-09-2026", "07:00", "17:00"));

        Assert.Contains("missing", Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html)).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Conflicting_duplicate_employee_totals_are_rejected_across_serial_numbers(bool reverseSerialOrder)
    {
        var date = new DateOnly(2026, 9, 30);
        var html = Performance(date, date, ["30"], summaries: ["-", "0", "1", "0", "0"]);
        var duplicate = Row("2", "007", "Edge Employee", "Check-in1", "06:30", "-", "1", "0", "0", "0")
            + Row("2", "007", "Edge Employee", "Check-out1", "19:30");
        html = html.Replace("</table></body>", duplicate + "</table></body>", StringComparison.Ordinal);
        if (reverseSerialOrder)
            html = html.Replace("<td>1</td><td>007</td>", "<td>3</td><td>007</td>", StringComparison.Ordinal)
                .Replace("<td>2</td><td>007</td>", "<td>1</td><td>007</td>", StringComparison.Ordinal)
                .Replace("<td>3</td><td>007</td>", "<td>2</td><td>007</td>", StringComparison.Ordinal);

        Assert.Contains("Conflicting monthly summary totals", Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html)).Message);
    }

    [Theory]
    [InlineData("0", "1", "0")]
    [InlineData("-", "1", "-")]
    public void Compatible_duplicate_employee_totals_are_counted_once(string absent, string attended, string leave)
    {
        var date = new DateOnly(2026, 9, 30);
        var html = Performance(date, date, ["30"], summaries: ["-", "0", "1", "0", "0"]);
        var duplicate = Row("2", "007", "Edge Employee", "Check-in1", "06:30", "-", absent, attended, leave, "-")
            + Row("2", "007", "Edge Employee", "Check-out1", "19:30");
        html = html.Replace("</table></body>", duplicate + "</table></body>", StringComparison.Ordinal);
        var report = AttendanceParser.Parse(html);
        var total = Assert.Single(report.EmployeeTotals!);

        Assert.Equal(0m, total.AbsentDays);
        Assert.Equal(1m, total.AttendedDays);
        Assert.Equal(0m, total.LeaveDays);
        Assert.Equal(2, report.Records.Count);
    }

    [Theory]
    [InlineData("Person ID", "", "Employee  Name", "Edge Employee")]
    [InlineData("Person ID", "007", "Employee  Name", "")]
    [InlineData("Person\tID", "", "Employee\tName", "Edge Employee")]
    public void Identity_field_boundaries_respect_whitespace_normalization(string idLabel, string id, string nameLabel, string name)
    {
        var html = Layout(1, "Check-in1", "06:30").Replace(Row("Person ID", "007", "Employee Name", "Edge Employee"),
            Row(idLabel, id, nameLabel, name, "Joining  Date", "01-01-2026"), StringComparison.Ordinal);

        Assert.Contains("without a Person ID or Employee Name", Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html)).Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Nested_layout_tables_do_not_duplicate_employee_data_or_metadata(int layout)
    {
        const string name = "From: 01-10-2030 To: 31-10-2030";
        var html = Layout(layout, "Check-in1", "06:30").Replace("Edge Employee", name, StringComparison.Ordinal)
            .Replace("<body><table>", "<body><table><tr><td><table>", StringComparison.Ordinal)
            .Replace("</table></body>", "</table></td></tr></table></body>", StringComparison.Ordinal);
        var record = Assert.Single(AttendanceParser.Parse(html).Records);

        Assert.Equal(name, record.Name);
        Assert.Equal(new DateOnly(2026, 9, 30), record.Date);
        Assert.Equal("06:30", record.InPunch);
    }

    [Fact]
    public void Nested_table_parent_can_supply_report_date_range()
    {
        var html = Layout(1, "Check-in1", "06:30").Replace(Row("From: 30-09-2026 To: 30-09-2026"), "", StringComparison.Ordinal)
            .Replace("<body><table>", "<body><table><tr><td>From: 30-09-2026 To: 30-09-2026<table>", StringComparison.Ordinal)
            .Replace("</table></body>", "</table></td></tr></table></body>", StringComparison.Ordinal);

        Assert.Equal(new DateOnly(2026, 9, 30), Assert.Single(AttendanceParser.Parse(html).Records).Date);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void First_punch_pair_labels_allow_whitespace_before_the_pair_number(int layout)
    {
        var html = Layout(layout, "Check-in1", "06:30")
            .Replace("<td>Check-in</td>", "<td>Check-in 1</td>", StringComparison.Ordinal)
            .Replace("<td>Check-out</td>", "<td>Check-out 1</td>", StringComparison.Ordinal)
            .Replace("<td>Check-in1</td>", "<td>Check-in 1</td>", StringComparison.Ordinal)
            .Replace("<td>Check-out1</td>", "<td>Check-out 1</td>", StringComparison.Ordinal);
        var record = Assert.Single(AttendanceParser.Parse(html).Records);

        Assert.Equal("06:30", record.InPunch);
        Assert.Equal("19:30", record.OutPunch);
    }

    [Fact]
    public void Equivalent_repeated_block_date_mappings_are_allowed_without_duplicate_records()
    {
        var html = Layout(1, "Check-in1", "06:30").Replace("</table></body>",
            Row("Date", "030", "-", "") + "</table></body>", StringComparison.Ordinal);
        var record = Assert.Single(AttendanceParser.Parse(html).Records);

        Assert.Equal(new DateOnly(2026, 9, 30), record.Date);
        Assert.Equal("06:30", record.InPunch);
        Assert.Equal("19:30", record.OutPunch);
    }

    [Fact]
    public void Conflicting_repeated_block_date_mappings_are_rejected()
    {
        var html = Layout(1, "Check-in1", "06:30")
            .Replace("From: 30-09-2026", "From: 01-09-2026", StringComparison.Ordinal)
            .Replace("</table></body>", Row("Date", "29") + "</table></body>", StringComparison.Ordinal);

        Assert.Contains("Conflicting monthly date rows", Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html)).Message);
    }

    [Fact]
    public void Leading_zero_variants_in_repeated_performance_day_headers_are_equivalent()
    {
        var from = new DateOnly(2026, 9, 1);
        var html = Performance(from, from.AddDays(1), ["1", "2", "-"])
            .Replace("</table></body>", Row("No.", "Person ID", "Name", "", "01", "02", "")
                + "</table></body>", StringComparison.Ordinal);
        var records = AttendanceParser.Parse(html).Records;

        Assert.Equal(new[] { from, from.AddDays(1) }, records.Select(record => record.Date));
        Assert.All(records, record => Assert.Equal("06:30", record.InPunch));
    }

    private static string AdditionalPair(int layout, string checkIn, string checkOut)
    {
        var html = Layout(layout, "Check-in1", "06:30");
        if (layout == 0)
        {
            html = html.Replace("<td>Status</td></tr>", "<td>Status</td><td>Check-in2</td><td>Check-out2</td></tr>", StringComparison.Ordinal);
            return html.Replace("<td>P</td></tr>", $"<td>P</td><td>{checkIn}</td><td>{checkOut}</td></tr>", StringComparison.Ordinal);
        }
        var extra = layout == 1 ? Row("Check-in2", checkIn) + Row("Check-out2", checkOut)
            : Row("1", "007", "Edge Employee", "Check-in2", checkIn) + Row("1", "007", "Edge Employee", "Check-out2", checkOut);
        return html.Replace("</table></body>", extra + "</table></body>", StringComparison.Ordinal);
    }

    private static string Layout(int layout, string replaceMetric, string value)
    {
        var date = new DateOnly(2026, 9, 30);
        if (layout == 0)
        {
            var values = new Dictionary<string, string> { ["Check-in1"] = "06:30", ["Check-out1"] = "19:30", ["Attended"] = "12.5", ["OT"] = "3" };
            values[replaceMetric] = value;
            return Wrap(Row("No.", "Person ID", "Name", "Date", "Check-in", "Check-out", "Attended", "OT", "Status")
                + Row("1", "007", "Edge Employee", "30-09-2026", values["Check-in1"], values["Check-out1"], values["Attended"], values["OT"], "P"));
        }
        if (layout == 2) return Performance(date, date, ["30"], replaceMetric, [value]);
        var metrics = new Dictionary<string, string> { ["Check-in1"] = "06:30", ["Check-out1"] = "19:30", ["Attended"] = "12.5", ["OT"] = "3", ["Status"] = "P" };
        metrics[replaceMetric] = value;
        return Wrap(Row("Person ID", "007", "Employee Name", "Edge Employee") + Row("Date", "30")
            + string.Concat(metrics.Select(metric => Row(metric.Key, metric.Value))));
    }

    private static string Performance(DateOnly from, DateOnly to, string[] header, string? replaceMetric = null,
        string[]? replaceValues = null, string[]? summaries = null)
    {
        string Metric(string label, string value) => Row(new[] { "1", "007", "Edge Employee", label }
            .Concat(label == replaceMetric ? replaceValues! : header.Select(day => int.TryParse(day, out _) ? value : "-"))
            .Concat(label == "Check-in1" && summaries is not null ? summaries : Array.Empty<string>()).ToArray());
        var summaryHeaders = summaries is null ? "" : Cells(["*", "Absent(Day(s))", "Attended(Actual)"]) + "<td colspan='2'>Leave</td>";
        var subheaders = summaries is null ? "" : Row(new[] { "No.", "Person ID", "Name", "" }
            .Concat(header.Select(_ => "Tue.")).Concat(new[] { "*", "Absent", "Actual", "Sick Leave", "Annual Leave" }).ToArray());
        return Wrap("<tr>" + Cells(["No.", "Person ID", "Name", ""]) + Cells(header) + summaryHeaders + "</tr>" + subheaders
            + Metric("Check-in1", "06:30") + Metric("Check-out1", "19:30") + Metric("Attended", "12.5") + Metric("OT", "3") + Metric("Status", "P"), from, to);
    }

    private static string Wrap(string rows, DateOnly? from = null, DateOnly? to = null) =>
        "<html><body><table>" + Row($"From: {(from ?? new DateOnly(2026, 9, 30)):dd-MM-yyyy} To: {(to ?? new DateOnly(2026, 9, 30)):dd-MM-yyyy}")
        + rows + "</table></body></html>";

    private static string Row(params string[] values) => "<tr>" + Cells(values) + "</tr>";
    private static string Cells(IEnumerable<string> values) => string.Concat(values.Select(value => $"<td>{value}</td>"));
}
