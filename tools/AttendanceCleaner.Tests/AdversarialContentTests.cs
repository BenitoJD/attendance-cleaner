using AttendanceCleaner.Core;
using ClosedXML.Excel;
using Xunit;

namespace AttendanceCleaner.Tests;

/// <summary>
/// Adversarial and data-integrity tests: sensitive attendance data must survive every
/// conversion exactly, and hostile-looking cell content must never be misinterpreted.
/// </summary>
public class AdversarialContentTests
{
    private static DateOnly Sep(int day) => new(2026, 9, day);

    [Fact]
    public void Unicode_and_tamil_names_survive_the_round_trip()
    {
        var names = new[] { "தங்கவேல்", "RÄJÉŚH Ñ", "李小明", "محمد", "O'Brien & Sons" };
        var records = names.Select((n, i) =>
            new AttendanceRecord($"{i + 1}", n, "Male", Sep(1), "06:51", "19:00", Order: i));

        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xlsx");
        try
        {
            TemplateWriter.WriteToFile(records, path);
            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheets.Single();
            for (var i = 0; i < names.Length; i++)
            {
                Assert.Equal(names[i], ws.Cell(i + 2, 3).GetString());
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Formula_looking_names_are_written_as_plain_text()
    {
        // Excel formula injection: a name like =1+1 must never become a live formula
        var hostile = new[] { "=1+1", "+SUM(A1:A9)", "@cmd", "-2+3", "=HYPERLINK(\"http://evil\")" };
        var records = hostile.Select((n, i) =>
            new AttendanceRecord($"{i + 1}", n, "Male", Sep(1), "06:51", "19:00", Order: i));

        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xlsx");
        try
        {
            TemplateWriter.WriteToFile(records, path);
            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheets.Single();
            for (var i = 0; i < hostile.Length; i++)
            {
                var cell = ws.Cell(i + 2, 3);
                Assert.Equal(XLDataType.Text, cell.DataType);
                Assert.Equal(hostile[i], cell.GetString());
                Assert.Equal("", cell.FormulaA1); // empty means "no formula"
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Boundary_punches_are_kept_and_computed()
    {
        var rows = TemplateWriter.BuildRows(new[]
        {
            new AttendanceRecord("1", "MIDNIGHT", "", Sep(1), "00:00", "23:59", Order: 0),
            new AttendanceRecord("2", "ZERO", "", Sep(1), "00:00", "00:00", Order: 1),
            new AttendanceRecord("3", "ONE_MIN", "", Sep(1), "09:00", "09:01", Order: 2),
        });

        Assert.Equal("23:59", rows[0].TotalHours);
        Assert.Equal("00:00", rows[1].TotalHours); // in == out -> zero hours, not negative
        Assert.Equal("00:01", rows[2].TotalHours);
    }

    [Fact]
    public void Title_only_report_is_rejected_with_a_clear_message()
    {
        // no recognisable data structure -> refuse rather than return empty-but-valid looking output
        var html = """
            <html><body>
            <table><tr><td colspan=32>Attendance Monthly Report</td></tr>
            <tr><td colspan=32>From: 01-09-2026 00:00 To: 30-09-2026 23:59</td></tr></table>
            </body></html>
            """;
        Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(html));
    }

    [Fact]
    public void Truncated_html_is_rejected_never_partially_parsed()
    {
        // a chopped-off download must never yield silent partial attendance data
        var full = TestReports.MonthlyBlocks();
        var half = Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(full[..(full.Length / 2)]));
        Assert.Contains("incomplete", half.Message);
        Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse("<html><table><tr><td>Person ID"));
    }

    [Fact]
    public void Duplicate_employee_entries_are_all_preserved()
    {
        var rows = TemplateWriter.BuildRows(new[]
        {
            new AttendanceRecord("3", "AJITH.S.S", "", Sep(1), "06:51", "19:00", Order: 0),
            new AttendanceRecord("3", "AJITH.S.S", "", Sep(1), "06:55", "19:02", Order: 1), // same person twice
            new AttendanceRecord("3", "AJITH.S.S", "", Sep(1), null, null, Order: 2),
        });
        Assert.Equal(3, rows.Count); // nothing is silently dropped; payroll sees the truth
    }

    [Fact]
    public void Pipe_characters_in_names_do_not_break_monthly_rows_parsing()
    {
        // the internal block key uses '|' as a separator; a name containing one must not corrupt identity
        var days = new[] { 1, 2 };
        var html = TestReports.MonthlyBlocksFlexible(
            "01-09-2026", days,
            new[] { new TestReports.BlockEmployee("3", "A|B|C", days,
                new string?[] { "06:51", null }, new string?[] { "19:00", null }) });

        var report = AttendanceParser.Parse(html);
        Assert.Equal(2, report.Records.Count);
        Assert.All(report.Records, r => Assert.Equal("A|B|C", r.Name));
        Assert.All(report.Records, r => Assert.Equal("3", r.Id));
    }

    [Fact]
    public void Large_company_month_parses_correctly_and_quickly()
    {
        // 300 employees x 31 days = 9,300 rows
        var days = Enumerable.Range(1, 31).ToArray();
        var employees = Enumerable.Range(1, 300).Select(i => new TestReports.BlockEmployee(
            $"{i}", $"EMPLOYEE {i:000}", days,
            days.Select(d => d % 3 == 0 ? $"0{i % 10}:15" : (string?)null).ToArray(),
            days.Select(d => d % 3 == 0 ? "18:30" : (string?)null).ToArray())).ToArray();

        var html = TestReports.MonthlyBlocksFlexible("01-10-2026", days, employees);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var report = AttendanceParser.Parse(html);
        var rows = TemplateWriter.BuildRows(report.Records);
        sw.Stop();

        Assert.Equal(9300, rows.Count);
        Assert.True(sw.ElapsedMilliseconds < 30_000, $"took {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Serial_numbers_reach_large_values_without_wrapping()
    {
        var records = Enumerable.Range(0, 120).Select(i =>
            new AttendanceRecord($"{i}", $"E{i}", "", Sep(1), "06:51", "19:00", Order: i));
        var rows = TemplateWriter.BuildRows(records);
        Assert.Equal(1, rows[0].SlNo);
        Assert.Equal(120, rows[^1].SlNo);
    }

    [Fact]
    public void Daily_report_with_no_matching_rows_yields_empty_not_garbage()
    {
        var html = TestReports.DailyFlexible(); // header, zero data rows
        var report = AttendanceParser.Parse(html);
        Assert.Empty(report.Records);
    }

    [Fact]
    public void Round_trip_through_the_excel_never_loses_a_cell()
    {
        // build rows -> write -> read back every cell of every row
        var records = new List<AttendanceRecord>();
        for (var d = 1; d <= 5; d++)
        {
            for (var e = 0; e < 20; e++)
            {
                records.Add(new AttendanceRecord(
                    $"{e}", $"EMP {e}", e % 2 == 0 ? "Male" : "Female", Sep(d),
                    e % 3 == 0 ? "06:51" : null, e % 3 == 0 ? "19:00" : null, Order: e));
            }
        }
        var expected = TemplateWriter.BuildRows(records);

        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xlsx");
        try
        {
            TemplateWriter.WriteToFile(records, path);
            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheets.Single();

            for (var i = 0; i < expected.Count; i++)
            {
                var row = i + 2;
                Assert.Equal(expected[i].SlNo, ws.Cell(row, 1).GetValue<int>());
                Assert.Equal(expected[i].Name, ws.Cell(row, 3).GetString());
                Assert.Equal(expected[i].Gender, ws.Cell(row, 4).GetString());
                Assert.Equal(expected[i].DateText, ws.Cell(row, 5).GetString());
                Assert.Equal(expected[i].Day, ws.Cell(row, 6).GetString());
                Assert.Equal(expected[i].InPunch ?? "", ws.Cell(row, 7).GetString());
                Assert.Equal(expected[i].OutPunch ?? "", ws.Cell(row, 8).GetString());
                Assert.Equal(expected[i].TotalHours ?? "", ws.Cell(row, 9).GetString());
                Assert.Equal(expected[i].Remark, ws.Cell(row, 10).GetString());
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
