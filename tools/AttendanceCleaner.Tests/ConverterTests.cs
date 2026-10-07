using System.Globalization;
using AttendanceCleaner.Core;
using ClosedXML.Excel;
using Xunit;

namespace AttendanceCleaner.Tests;

public class DailyReportParserTests
{
    private static ParsedReport ParseDaily() => AttendanceParser.Parse(TestReports.Daily());

    [Fact]
    public void Detects_daily_format()
    {
        Assert.Equal(AttendanceExportFormat.DailyReport, ParseDaily().Format);
    }

    [Fact]
    public void Parses_all_employee_rows()
    {
        Assert.Equal(4, ParseDaily().Records.Count);
    }

    [Fact]
    public void Parses_identity_and_gender()
    {
        var first = ParseDaily().Records[0];
        Assert.Equal("3", first.Id);
        Assert.Equal("AJITH.S.S", first.Name);
        Assert.Equal("Male", first.Gender);
    }

    [Fact]
    public void Parses_date_from_the_row()
    {
        Assert.All(ParseDaily().Records, r => Assert.Equal(new DateOnly(2026, 10, 6), r.Date));
    }

    [Fact]
    public void Present_row_has_both_punches_and_total()
    {
        var row = ParseDaily().Records[0];
        Assert.Equal("06:51", row.InPunch);
        Assert.Equal("19:00", row.OutPunch);
    }

    [Fact]
    public void Absent_row_has_no_punches()
    {
        var row = ParseDaily().Records[1];
        Assert.Null(row.InPunch);
        Assert.Null(row.OutPunch);
    }

    [Fact]
    public void Punch_with_seconds_is_normalised_to_hours_and_minutes()
    {
        Assert.Equal("07:10", ParseDaily().Records[2].InPunch);
    }

    [Fact]
    public void Punches_crossing_midnight_still_count_as_present()
    {
        var row = ParseDaily().Records[3];
        Assert.Equal("23:30", row.InPunch);
        Assert.Equal("00:15", row.OutPunch);
    }

    [Fact]
    public void Source_row_order_is_preserved()
    {
        Assert.Equal(new[] { "3", "5", "7", "9" }, ParseDaily().Records.Select(r => r.Id));
    }
}

public class MonthlyBlocksParserTests
{
    private static ParsedReport ParseBlocks() => AttendanceParser.Parse(TestReports.MonthlyBlocks());

    [Fact]
    public void Detects_monthly_block_format()
    {
        Assert.Equal(AttendanceExportFormat.MonthlyBlocks, ParseBlocks().Format);
    }

    [Fact]
    public void Parses_every_employee_times_every_day()
    {
        // 3 employees x 3 days in the Date row
        Assert.Equal(9, ParseBlocks().Records.Count);
    }

    [Fact]
    public void Reads_identity_of_every_block_even_with_the_broken_html_rows()
    {
        var names = ParseBlocks().Records.Select(r => r.Name).Distinct().OrderBy(n => n).ToList();
        Assert.Equal(new[] { "AJITH.S.S", "IN PUNCH GUY", "MICHEAL ANTONY" }, names);
    }

    [Fact]
    public void Maps_days_to_september_dates_from_the_report_title()
    {
        var ajith = ParseBlocks().Records.Where(r => r.Name == "AJITH.S.S").OrderBy(r => r.Date).ToList();
        Assert.Equal(new DateOnly(2026, 9, 1), ajith[0].Date);
        Assert.Equal(new DateOnly(2026, 9, 2), ajith[1].Date);
        Assert.Equal(new DateOnly(2026, 9, 3), ajith[2].Date);
    }

    [Fact]
    public void Punches_land_on_the_right_day()
    {
        var Micheal = ParseBlocks().Records.Where(r => r.Name == "MICHEAL ANTONY").OrderBy(r => r.Date).ToList();
        Assert.Null(Micheal[0].InPunch);
        Assert.Equal("08:10", Micheal[1].InPunch);
        Assert.Equal("17:45", Micheal[1].OutPunch);
        Assert.Null(Micheal[2].InPunch);
    }

    [Fact]
    public void Monthly_exports_without_gender_get_the_placeholder()
    {
        Assert.All(ParseBlocks().Records, r => Assert.Equal("-", r.Gender));
    }

    [Fact]
    public void Employee_block_order_is_kept_for_the_output_sort()
    {
        var firstDay = ParseBlocks().Records.Where(r => r.Date.Day == 1).OrderBy(r => r.Date).ToList();
        Assert.Equal(new[] { "AJITH.S.S", "MICHEAL ANTONY", "IN PUNCH GUY" }, firstDay.Select(r => r.Name));
    }
}

public class MonthlyRowsParserTests
{
    private static ParsedReport ParseRows() => AttendanceParser.Parse(TestReports.MonthlyRows());

    [Fact]
    public void Detects_monthly_rows_format()
    {
        Assert.Equal(AttendanceExportFormat.MonthlyRows, ParseRows().Format);
    }

    [Fact]
    public void Parses_every_employee_times_every_day()
    {
        Assert.Equal(60, ParseRows().Records.Count); // 2 employees x 30 days
    }

    [Fact]
    public void Check_out_values_are_not_lost()
    {
        var ajithDay1 = ParseRows().Records.First(r => r.Name == "AJITH.S.S" && r.Date.Day == 1);
        Assert.Equal("06:51", ajithDay1.InPunch);
        Assert.Equal("19:00", ajithDay1.OutPunch);
    }

    [Fact]
    public void Last_day_of_month_is_mapped()
    {
        var ajithDay30 = ParseRows().Records.First(r => r.Name == "AJITH.S.S" && r.Date.Day == 30);
        Assert.Equal(new DateOnly(2026, 9, 30), ajithDay30.Date);
        Assert.Equal("07:05", ajithDay30.InPunch);
        Assert.Null(ajithDay30.OutPunch);
    }

    [Fact]
    public void Status_rows_are_ignored()
    {
        var Micheal = ParseRows().Records.Where(r => r.Name == "MICHEAL ANTONY").ToList();
        Assert.All(Micheal, r => Assert.True(r.InPunch == null || r.Date.Day == 2));
    }
}

public class ParserErrorTests
{
    [Fact]
    public void Unrecognised_tables_are_rejected_with_a_clear_message()
    {
        var ex = Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(TestReports.NotAnAttendanceExport()));
        Assert.Contains("attendance export", ex.Message);
    }

    [Fact]
    public void Empty_input_is_rejected()
    {
        Assert.ThrowsAny<Exception>(() => AttendanceParser.Parse(""));
    }

    [Fact]
    public void Monthly_report_without_dates_cannot_be_mapped_to_a_month()
    {
        var ex = Assert.Throws<InvalidDataException>(() => AttendanceParser.Parse(TestReports.MonthlyBlocksWithoutDates()));
        Assert.Contains("From/To", ex.Message);
    }

    [Fact]
    public void Real_xlsx_workbooks_are_rejected_with_the_template_hint()
    {
        // "PK\x03\x04" — a real .xlsx (e.g. the output template) picked as input by mistake
        var ex = Assert.Throws<InvalidDataException>(
            () => AttendanceParser.ValidateInputIsNotWorkbook(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00 }));
        Assert.Contains("OUTPUT", ex.Message);
        Assert.Contains("attendance software", ex.Message);
    }

    [Fact]
    public void Old_binary_xls_workbooks_are_rejected_with_the_reexport_hint()
    {
        // OLE2 compound file signature
        var ex = Assert.Throws<InvalidDataException>(
            () => AttendanceParser.ValidateInputIsNotWorkbook(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1 }));
        Assert.Contains("binary Excel", ex.Message);
    }

    [Fact]
    public void Html_reports_pass_the_workbook_check()
    {
        AttendanceParser.ValidateInputIsNotWorkbook("<html>"u8);
        AttendanceParser.ValidateInputIsNotWorkbook(""u8);
    }
}

public class TemplateWriterRowTests
{
    private static AttendanceRecord Record(string id, string name, DateOnly date, string? inPunch, string? outPunch, int order = 0, string gender = "Male") =>
        new(id, name, gender, date, inPunch, outPunch, order);

    private static readonly DateOnly Day1 = new(2026, 9, 1);
    private static readonly DateOnly Day2 = new(2026, 9, 2);

    [Fact]
    public void Rows_are_grouped_by_date_in_date_order()
    {
        var rows = TemplateWriter.BuildRows(new[]
        {
            Record("5", "LATER", Day2, "08:00", "17:00"),
            Record("3", "EARLIER", Day1, "06:51", "19:00"),
        });
        Assert.Equal(Day1.ToString("dd-MM-yyyy"), rows[0].DateText);
        Assert.Equal(Day2.ToString("dd-MM-yyyy"), rows[^1].DateText);
    }

    [Fact]
    public void Serial_number_restarts_for_each_day()
    {
        var rows = TemplateWriter.BuildRows(new[]
        {
            Record("3", "A", Day1, "06:51", "19:00", order: 0),
            Record("4", "B", Day1, null, null, order: 1),
            Record("5", "C", Day2, "08:00", "17:00", order: 0),
        });
        Assert.Equal(1, rows[0].SlNo);
        Assert.Equal(2, rows[1].SlNo);
        Assert.Equal(1, rows[2].SlNo);
    }

    [Fact]
    public void Remarks_follow_the_punches()
    {
        var rows = TemplateWriter.BuildRows(new[]
        {
            Record("3", "A", Day1, "06:51", "19:00"),
            Record("4", "B", Day1, "07:10", null),
            Record("5", "C", Day1, null, null),
        });
        Assert.Equal(TemplateSpec.RemarkPresent, rows[0].Remark);
        Assert.Equal(TemplateSpec.RemarkInPunchOnly, rows[1].Remark);
        Assert.Equal(TemplateSpec.RemarkAbsent, rows[2].Remark);
    }

    [Fact]
    public void Total_hours_is_computed_from_the_punches()
    {
        var rows = TemplateWriter.BuildRows(new[] { Record("3", "A", Day1, "06:51", "19:00") });
        Assert.Equal("12:09", rows[0].TotalHours);
    }

    [Fact]
    public void Total_hours_crossing_midnight_wraps_to_the_next_day()
    {
        var rows = TemplateWriter.BuildRows(new[] { Record("3", "A", Day1, "23:30", "00:15") });
        Assert.Equal("00:45", rows[0].TotalHours);
    }

    [Fact]
    public void Absent_rows_have_no_total()
    {
        var rows = TemplateWriter.BuildRows(new[] { Record("3", "A", Day1, null, null) });
        Assert.Null(rows[0].TotalHours);
    }

    [Fact]
    public void Missing_gender_falls_back_to_the_spec_placeholder()
    {
        var rows = TemplateWriter.BuildRows(new[] { Record("3", "A", Day1, "06:51", "19:00", gender: "") });
        Assert.Equal(TemplateSpec.UnknownGender, rows[0].Gender);
    }

    [Fact]
    public void Cells_match_the_header_layout_of_the_template()
    {
        var rows = TemplateWriter.BuildRows(new[] { Record("3", "A", Day1, "06:51", "19:00") });
        Assert.Equal(TemplateSpec.Headers.Length, rows[0].Cells.Length);
        Assert.Equal("A", rows[0].Cells[Array.IndexOf(TemplateSpec.Headers, "Name")]);
        Assert.Equal("Present", rows[0].Cells[Array.IndexOf(TemplateSpec.Headers, "Remarks")]);
    }
}

public class TemplateWriterExcelTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xlsx");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    private IXLWorksheet WriteAndOpenSheet()
    {
        var records = new[]
        {
            new AttendanceRecord("3", "AJITH.S.S", "Male", new DateOnly(2026, 9, 1), "06:51", "19:00", Order: 0),
            new AttendanceRecord("5", "SUDER BABU.A", "Male", new DateOnly(2026, 9, 1), null, null, Order: 1),
            new AttendanceRecord("abc", "TEXT ID", "", new DateOnly(2026, 9, 2), "07:10", null, Order: 0),
        };
        TemplateWriter.WriteToFile(records, _path);

        // kept open on purpose: the worksheet must outlive this method for the assertions
        var workbook = new XLWorkbook(_path);
        return workbook.Worksheets.Single();
    }

    [Fact]
    public void Sheet_is_named_after_the_template()
    {
        var ws = WriteAndOpenSheet();
        Assert.Equal(TemplateSpec.SheetName, ws.Name);
    }

    [Fact]
    public void Header_row_matches_the_template_headers()
    {
        var ws = WriteAndOpenSheet();
        var headers = TemplateSpec.Headers.Select((_, i) => ws.Cell(1, i + 1).GetString()).ToList();
        Assert.Equal(TemplateSpec.Headers, headers);
    }

    [Fact]
    public void Headers_are_correctly_spelled()
    {
        // guards against typos sneaking back into the template
        Assert.Equal(
            new[] { "Sl.No", "ID No", "Name", "Gender", "Date", "Day", "In punch", "Out punch", "Total hours", "Remarks" },
            TemplateSpec.Headers);
    }

    [Fact]
    public void Suggested_file_names_are_clean_and_professional()
    {
        // these are only suggestions - the user renames freely in the save dialog -
        // but the default itself must look right: no technical suffixes
        var daily = string.Format(CultureInfo.InvariantCulture, TemplateSpec.DailyFileName, new DateOnly(2026, 10, 6));
        var monthly = string.Format(CultureInfo.InvariantCulture, TemplateSpec.MonthlyFileName, new DateOnly(2026, 9, 1));
        Assert.Equal("Attendance_06-10-2026.xlsx", daily);
        Assert.Equal("Attendance_September 2026.xlsx", monthly);
    }

    [Fact]
    public void Numeric_id_and_serial_number_are_written_as_numbers()
    {
        var ws = WriteAndOpenSheet();
        Assert.True(ws.Cell(2, 1).DataType == XLDataType.Number);
        Assert.True(ws.Cell(2, 2).DataType == XLDataType.Number);
    }

    [Fact]
    public void Non_numeric_id_is_written_as_text()
    {
        var ws = WriteAndOpenSheet();
        Assert.Equal("abc", ws.Cell(4, 2).GetString());
    }

    [Fact]
    public void Absent_cells_are_left_blank_like_the_template()
    {
        var ws = WriteAndOpenSheet();
        Assert.True(ws.Cell(3, 7).IsEmpty());
        Assert.True(ws.Cell(3, 8).IsEmpty());
        Assert.True(ws.Cell(3, 9).IsEmpty());
        Assert.Equal(TemplateSpec.RemarkAbsent, ws.Cell(3, 10).GetString());
    }

    [Fact]
    public void Present_row_carries_punches_total_and_remark()
    {
        var ws = WriteAndOpenSheet();
        Assert.Equal("06:51", ws.Cell(2, 7).GetString());
        Assert.Equal("19:00", ws.Cell(2, 8).GetString());
        Assert.Equal("12:09", ws.Cell(2, 9).GetString());
        Assert.Equal(TemplateSpec.RemarkPresent, ws.Cell(2, 10).GetString());
    }

    [Fact]
    public void Row_count_matches_records_plus_header()
    {
        var ws = WriteAndOpenSheet();
        var last = ws.LastRowUsed();
        Assert.NotNull(last);
        Assert.Equal(4, last.RowNumber());
    }
}
