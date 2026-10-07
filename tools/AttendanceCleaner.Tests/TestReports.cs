using System.Text;

namespace AttendanceCleaner.Tests;

/// <summary>
/// Synthetic attendance exports in the software's HTML-as-.xls format, including its
/// real-world quirks: identity rows with a missing &lt;tr&gt;, colspan values like "1,",
/// punch times with seconds, and title tables that duplicate cells across columns.
/// </summary>
public static class TestReports
{
    // --- daily report: one row per employee, 4 cases (present / absent / in-punch-only / overnight) ---

    public static string Daily() => $"""
        <html xmlns:x="urn:schemas-microsoft-com:office:excel">
        <head><title>Daily Report</title></head>
        <body>
        <table>
        <tr><td colspan="20,">Attendance Daily Report</td></tr>
        <tr><td colspan=20>06-10-2026 00:00 To 06-10-2026 23:59</td></tr>
        <tr>
        <td>No.</td><td>Person ID</td><td>Name</td><td>Department</td><td>Position</td><td>Gender</td>
        <td>Date</td><td>Week</td><td>Timetable</td><td>Check-in</td><td>Check-out</td><td>Work</td>
        <td>OT</td><td>Attended</td><td>Late</td><td>Early</td><td>Absent</td><td>Leave</td><td>Status</td><td>Records</td>
        </tr>
        </table>
        <table>
        <tr><td>1</td><td>3</td><td>AJITH.S.S</td><td>DAKSHINAK</td><td>-</td><td>Male</td><td>06-10-2026</td><td>Tue.</td><td>General</td>
        <td>06:51</td><td>19:00</td><td>12</td><td>0</td><td>12</td><td>0</td><td>0</td><td>0</td><td>0</td><td>P-#</td><td>-</td></tr>
        <tr><td>2</td><td>5</td><td>SUDER BABU.A</td><td>DAKSHINAK</td><td>-</td><td>Male</td><td>06-10-2026</td><td>Tue.</td><td>General</td>
        <td>-</td><td>-</td><td>0</td><td>0</td><td>0</td><td>0</td><td>0</td><td>9</td><td>0</td><td>A-#</td><td>-</td></tr>
        <tr><td>3</td><td>7</td><td>IN PUNCH GUY</td><td>DAKSHINAK</td><td>-</td><td>Male</td><td>06-10-2026</td><td>Tue.</td><td>General</td>
        <td>07:10:33</td><td>-</td><td>0</td><td>0</td><td>0</td><td>0</td><td>0</td><td>0</td><td>0</td><td>A-#</td><td>-</td></tr>
        <tr><td>4</td><td>9</td><td>OVERNIGHT GUY</td><td>DAKSHINAK</td><td>-</td><td>Male</td><td>06-10-2026</td><td>Tue.</td><td>General</td>
        <td>23:30</td><td>00:15</td><td>0</td><td>0</td><td>0</td><td>0</td><td>0</td><td>0</td><td>0</td><td>P-#</td><td>-</td></tr>
        </table>
        <table><tr><td>Note: LV = Leave</td></tr></table>
        </body></html>
        """;

    // --- monthly block report: one identity + metric rows block per employee, 3 days ---

    public static string MonthlyBlocks() => $"""
        <html xmlns:x="urn:schemas-microsoft-com:office:excel">
        <head><title>Attendance Monthly Report</title></head>
        <body>
        <table>
        <tr><td colspan=32>Attendance Monthly Report</td></tr>
        <tr><td colspan=32>From: 01-09-2026 00:00 To: 30-09-2026 23:59</td></tr>
        </table>
        <table>
        <tr><td>Person ID</td><td>3</td><td>Employee Name</td><td>AJITH.S.S</td><td>Department</td><td>DAKSHINAK</td><td>Joining Date</td><td>2026-09-18</td><td>Position</td><td>-</td></tr>
        <tr><td>Date</td><td>1</td><td>2</td><td>3</td></tr>
        <tr><td>Check-in1</td><td>06:51</td><td>-</td><td>07:05</td></tr>
        <tr><td>Check-out1</td><td>19:00</td><td>-</td><td>-</td></tr>
        <tr><td>OT</td><td>0</td><td>0</td><td>0</td></tr>
        <tr><td>Late</td><td>0</td><td>0</td><td>0</td></tr>
        <tr><td>Status</td><td>P-#</td><td>A-#</td><td>A-#</td></tr>
        <tr><td>Summary</td><td colspan=31>Normal Attendance:1; Weekend:2; Leave:0;</td></tr>
        </tr>
        <TD><b>Person ID</b></TD><TD><b>4</b></TD><TD><b>Employee Name</b></TD><TD><b>MICHEAL ANTONY</b></TD><TD><b>Department</b></TD><TD><b>DAKSHINAK</b></TD><TD><b>Joining Date</b></TD><TD><b>2026-09-18</b></TD><TD><b>Position</b></TD><TD><b>-</b></TD>
        <tr><td>Date</td><td>1</td><td>2</td><td>3</td></tr>
        <tr><td>Check-in1</td><td>-</td><td>08:10</td><td>-</td></tr>
        <tr><td>Check-out1</td><td>-</td><td>17:45</td><td>-</td></tr>
        <tr><td>OT</td><td>0</td><td>0</td><td>0</td></tr>
        <tr><td>Late</td><td>0</td><td>0</td><td>0</td></tr>
        <tr><td>Status</td><td>A-#</td><td>P-#</td><td>A-#</td></tr>
        <tr><td>Summary</td><td colspan=31>Normal Attendance:1; Weekend:2; Leave:0;</td></tr>
        </tr>
        <TD><b>Person ID</b></TD><TD><b>7</b></TD><TD><b>Employee Name</b></TD><TD><b>IN PUNCH GUY</b></TD><TD><b>Department</b></TD><TD><b>DAKSHINAK</b></TD><TD><b>Joining Date</b></TD><TD><b>2026-09-18</b></TD><TD><b>Position</b></TD><TD><b>-</b></TD>
        <tr><td>Date</td><td>1</td><td>2</td><td>3</td></tr>
        <tr><td>Check-in1</td><td>-</td><td>-</td><td>07:10</td></tr>
        <tr><td>Check-out1</td><td>-</td><td>-</td><td>-</td></tr>
        <tr><td>Status</td><td>A-#</td><td>A-#</td><td>A-#</td></tr>
        </table>
        <table><tr><td>Note: A = Absent</td></tr></table>
        </body></html>
        """;

    // --- monthly performance report: metric rows per employee, 30 day columns ---

    public static string MonthlyRows()
    {
        var days = Cells(Enumerable.Range(1, 30).Select(d => d.ToString()));
        var blanks = Cells(Enumerable.Repeat("-", 30));

        return $"""
        <html xmlns:x="urn:schemas-microsoft-com:office:excel">
        <head><title>4-Monthly Performance Report</title></head>
        <body>
        <table>
        <tr><td colspan="59,">Monthly Performance Report</td></tr>
        <tr><td colspan=59>From: 01-09-2026 To: 30-09-2026</td></tr>
        <tr><td>No.</td><td>Person ID</td><td>Name</td><td></td>{days}<td>*</td><td>Work(Day(s))</td><td>Work(Hour(s))</td><td>Absent(Day(s))</td></tr>
        <tr><td>No.</td><td>Person ID</td><td>Name</td><td></td>{blanks}<td>*</td><td>Workday</td><td>Non-Workday</td><td>Absent</td></tr>
        </table>
        <table>
        <tr><td>1</td><td>3</td><td>AJITH.S.S</td><td>Check-in1</td>{Row30(day1: "06:51", day30: "07:05")}</tr>
        <tr><td>1</td><td>3</td><td>AJITH.S.S</td><td>Check-out1</td>{Row30(day1: "19:00")}</tr>
        <tr><td>1</td><td>3</td><td>AJITH.S.S</td><td>Attended</td>{blanks}</tr>
        <tr><td>2</td><td>4</td><td>MICHEAL ANTONY</td><td>Check-in1</td>{Row30(day2: "08:10")}</tr>
        <tr><td>2</td><td>4</td><td>MICHEAL ANTONY</td><td>Check-out1</td>{Row30(day2: "17:45")}</tr>
        <tr><td>2</td><td>4</td><td>MICHEAL ANTONY</td><td>Status</td>{blanks}</tr>
        </table>
        <table><tr><td>Note: P = Present</td></tr></table>
        </body></html>
        """;

        static string Cells(IEnumerable<string> values) => string.Concat(values.Select(v => $"<td>{v}</td>"));

        static string Row30(string? day1 = null, string? day2 = null, string? day30 = null)
        {
            return Cells(Enumerable.Range(1, 30).Select(d => d switch
            {
                1 => day1 ?? "-",
                2 => day2 ?? "-",
                30 => day30 ?? "-",
                _ => "-",
            }));
        }
    }

    // --- an export the app should refuse (no recognisable layout) ---

    public static string NotAnAttendanceExport() => """
        <html><head><title>Sales</title></head>
        <body><table><tr><th>Product</th><th>Qty</th></tr>
        <tr><td>Widget</td><td>12</td></tr></table></body></html>
        """;

    // --- monthly blocks without any From/To date in the document ---

    public static string MonthlyBlocksWithoutDates() => MonthlyBlocks().Replace("From: 01-09-2026 00:00 To: 30-09-2026 23:59", "September report");
}
