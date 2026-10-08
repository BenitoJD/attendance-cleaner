using System.Text;
using AttendanceCleaner.Core;
using Xunit;

namespace AttendanceCleaner.Tests;

public sealed class ImportIntegrityTests
{
    [Fact]
    public void Leave_totals_stop_before_late_early_and_overtime_summary_columns()
    {
        var report = AttendanceParser.Parse(Performance(
            Row("Check-in1", "-", "1", "2", "45", "15", "4") + Row("Check-out1", "-"),
            "<td colspan='2'>Leave</td><td>Late</td><td>Early</td><td>Overtime</td>",
            "<td>Sick Leave</td><td>Annual Leave</td><td>Late Minutes</td><td>Early Minutes</td><td>OT Hours</td>"));

        Assert.Equal(3m, Assert.Single(report.EmployeeTotals!).LeaveDays);
    }

    [Theory]
    [InlineData("EMP-13")]
    [InlineData("team|007")]
    [InlineData("0013")]
    [InlineData("12345678901234567890")]
    public void Monthly_blocks_read_identity_from_labels_without_numeric_assumptions(string id)
    {
        var report = AttendanceParser.Parse(Block(
            "<tr><td>Person ID</td><td></td><td>" + id + "</td><td>Department</td><td>Finance</td><td>Employee Name</td><td>AJITH</td></tr>",
            Metric("Check-in1", "06:00") + Metric("Check-out1", "19:00")));

        var record = Assert.Single(report.Records);
        Assert.Equal(id, record.Id);
        Assert.Equal("AJITH", record.Name);
    }

    [Theory]
    [InlineData("", "AJITH")]
    [InlineData("3", "")]
    public void Missing_block_identity_is_rejected_instead_of_using_department(string id, string name)
    {
        var html = Block($"<tr><td>Person ID</td><td>{id}</td><td>Employee Name</td><td>{name}</td><td>Department</td><td>Finance</td></tr>",
            Metric("Check-in1", "06:00") + Metric("Check-out1", "19:00"));

        Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html));
    }

    [Fact]
    public void Metrics_after_status_are_preserved_in_monthly_blocks()
    {
        var report = AttendanceParser.Parse(Block(Identity,
            Metric("Check-in1", "06:00") + Metric("Check-out1", "19:00")
            + Metric("Status", "P") + Metric("Attended", "12") + Metric("OT", "3")));

        var record = Assert.Single(report.Records);
        Assert.Equal("P", record.Status);
        Assert.Equal("12", record.Attended);
        Assert.Equal("3", record.Overtime);
    }

    [Theory]
    [InlineData(false, "Check-in1", "06:00", "09:00")]
    [InlineData(true, "Check-in1", "06:00", "09:00")]
    [InlineData(false, "Check-out1", "17:00", "19:00")]
    [InlineData(true, "Check-out1", "17:00", "19:00")]
    [InlineData(false, "Attended", "12", "9")]
    [InlineData(true, "Attended", "12", "9")]
    [InlineData(false, "OT", "3", "2")]
    [InlineData(true, "OT", "3", "2")]
    [InlineData(false, "Status", "P", "A")]
    [InlineData(true, "Status", "P", "A")]
    public void Conflicting_repeated_monthly_metrics_are_rejected(bool performance, string label, string first, string second)
    {
        string Make(string metric, string value) => performance ? Row(metric, value) : Metric(metric, value);
        var rows = Make("Check-in1", label == "Check-in1" ? first : "06:00")
            + Make("Check-out1", label == "Check-out1" ? first : "19:00");
        if (label is not ("Check-in1" or "Check-out1")) rows += Make(label, first);
        rows += Make(label, second);
        var html = performance ? Performance(rows) : Block(Identity, rows);

        var exception = Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html));
        Assert.Contains("Conflicting", exception.Message);
        Assert.Contains(label, exception.Message);
        Assert.Contains("3", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Equivalent_repeated_punch_rows_are_accepted_without_duplicate_records(bool performance)
    {
        var rows = performance
            ? Row("Check-in1", "6:00") + Row("Check-in1", "06:00") + Row("Check-out1", "19:00")
            : Metric("Check-in1", "6:00") + Metric("Check-in1", "06:00") + Metric("Check-out1", "19:00");
        var report = AttendanceParser.Parse(performance ? Performance(rows) : Block(Identity, rows));

        Assert.Equal("06:00", Assert.Single(report.Records).InPunch);
    }

    [Fact]
    public void Conflicting_repeated_employee_summary_totals_are_rejected()
    {
        var html = Performance(Row("Check-in1", "06:00", "1") + Row("Check-in1", "06:00", "2") + Row("Check-out1", "19:00"),
            "<td colspan='2'>Leave</td>", "<td>Sick Leave</td><td>Annual Leave</td>");

        var exception = Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html));
        Assert.Contains("summary totals", exception.Message);
    }

    public static IEnumerable<object[]> UnicodeEncodings()
    {
        yield return [new UTF8Encoding(true, true)];
        yield return [new UnicodeEncoding(false, true, true)];
        yield return [new UnicodeEncoding(true, true, true)];
        yield return [new UTF32Encoding(false, true, true)];
        yield return [new UTF32Encoding(true, true, true)];
    }

    [Theory]
    [MemberData(nameof(UnicodeEncodings))]
    public void File_and_byte_imports_honor_unicode_boms_and_preserve_names(Encoding encoding)
    {
        var html = TestReports.DailyFlexible(new TestReports.DailyRow("1", "EMP-13", "தங்கவேல் José", "01-09-2026", "06:00", "19:00"));
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(html)).ToArray();
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(file, bytes);
            var fromBytes = Assert.Single(AttendanceParser.ParseBytes(bytes).Records);
            var fromFile = Assert.Single(AttendanceParser.ParseFile(file).Records);

            Assert.Equal("தங்கவேல் José", fromBytes.Name);
            Assert.Equal(fromBytes, fromFile);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Declared_legacy_html_encoding_preserves_employee_names()
    {
        var html = TestReports.DailyFlexible(new TestReports.DailyRow("1", "3", "José", "01-09-2026", "06:00", "19:00"))
            .Replace("<head>", "<head><meta http-equiv='Content-Type' content='text/html; charset=windows-1252'>", StringComparison.Ordinal);

        Assert.Equal("José", Assert.Single(AttendanceParser.ParseBytes(Encoding.Latin1.GetBytes(html)).Records).Name);
    }

    [Fact]
    public void Invalid_utf8_bytes_are_rejected_instead_of_corrupting_a_name()
    {
        var html = TestReports.DailyFlexible(new TestReports.DailyRow("1", "3", "José", "01-09-2026", "06:00", "19:00"));

        Assert.Throws<InvalidDataException>(() => AttendanceParser.ParseBytes(Encoding.Latin1.GetBytes(html)));
    }

    [Fact]
    public void Unsupported_declared_encoding_has_a_clear_import_error()
    {
        var html = TestReports.DailyFlexible().Replace("<head>", "<head><meta charset='unknown-encoding'>", StringComparison.Ordinal);

        var exception = Assert.Throws<InvalidDataException>(() => AttendanceParser.ParseBytes(Encoding.UTF8.GetBytes(html)));
        Assert.Contains("unsupported text encoding", exception.Message);
    }

    private const string Identity = "<tr><td>Person ID</td><td>3</td><td>Employee Name</td><td>AJITH</td></tr>";

    private static string Metric(string label, string value) => $"<tr><td>{label}</td><td>{value}</td></tr>";

    private static string Row(string label, string value, params string[] summary) =>
        $"<tr><td>1</td><td>3</td><td>AJITH</td><td>{label}</td><td>{value}</td>" + Cells(summary) + "</tr>";

    private static string Cells(IEnumerable<string> values) => string.Concat(values.Select(value => $"<td>{value}</td>"));

    private static string Wrap(string rows) =>
        $"<html><body><table><tr><td>From: 01-09-2026 To: 01-09-2026</td></tr>{rows}</table></body></html>";

    private static string Block(string identity, string rows) => Wrap(identity + "<tr><td>Date</td><td>1</td></tr>" + rows);

    private static string Performance(string rows, string headings = "", string subheadings = "") => Wrap(
        "<tr><td>No.</td><td>Person ID</td><td>Name</td><td></td><td>1</td>" + headings + "</tr>"
        + "<tr><td>No.</td><td>Person ID</td><td>Name</td><td></td><td>Tue.</td>" + subheadings + "</tr>" + rows);
}
