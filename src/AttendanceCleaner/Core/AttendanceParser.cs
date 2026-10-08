using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace AttendanceCleaner.Core;

/// <summary>
/// Parses the HTML tables (exported as .xls) produced by the attendance software.
/// Three export layouts are recognised: the daily report (one row per employee),
/// the monthly block report (one block of rows per employee, days as columns) and
/// the monthly performance report (metric rows per employee, days as columns).
/// Label matching is case-insensitive so minor software updates can't break parsing.
/// </summary>
public static partial class AttendanceParser
{
    // Row/section labels used by the software's exports (matched case-insensitively).
    private const string LabelPersonId = "Person ID";
    private const string LabelEmployeeName = "Employee Name";
    private const string LabelDepartment = "Department";
    private const string LabelJoiningDate = "Joining Date";
    private const string LabelPosition = "Position";
    private const string LabelNo = "No.";
    private const string LabelName = "Name";
    private const string LabelGender = "Gender";
    private const string LabelDate = "Date";
    private const string LabelCheckIn = "Check-in1";
    private const string LabelCheckOut = "Check-out1";
    private const string LabelAttended = "Attended";
    private const string LabelOvertime = "OT";
    private const string LabelCheckInPrefix = "Check-in";
    private const string LabelStatus = "Status";

    private static readonly HashSet<string> BlockFieldLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        LabelPersonId, LabelEmployeeName, LabelDepartment, LabelJoiningDate, LabelPosition,
    };

    public static ParsedReport ParseFile(string path)
    {
        var html = File.ReadAllText(path);
        return Parse(html);
    }

    /// <summary>
    /// Recognises real Excel workbooks by their file signature and explains the mistake —
    /// people often pick the output template or an already-converted file as input.
    /// The software's report export is HTML inside a .xls name, never a real workbook.
    /// </summary>
    public static void ValidateInputIsNotWorkbook(ReadOnlySpan<byte> leadingBytes)
    {
        if (leadingBytes is [0x50, 0x4B, ..]) // "PK" — zip container: real .xlsx
        {
            throw new InvalidDataException(
                "This is an Excel workbook (.xlsx) — probably the template or an already-converted file, which is the app's OUTPUT. " +
                "Please choose the .xls report downloaded from the attendance software.");
        }
        if (leadingBytes is [0xD0, 0xCF, 0x11, 0xE0, ..]) // OLE2 container: old binary .xls
        {
            throw new InvalidDataException(
                "This is a binary Excel (.xls) workbook, not the attendance software's report export. " +
                "Please export the report from the software again and choose that downloaded file.");
        }
    }

    public static ParsedReport Parse(string html)
    {
        if (!html.Contains("</html", StringComparison.OrdinalIgnoreCase)
            && !html.Contains("</body", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "This file looks incomplete (possibly a failed download). Please download the report from the attendance software again.");
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(NormalizeHtml(html));

        var tables = doc.DocumentNode.SelectNodes("//table")?
            .Select(t => ExtractRows(t).ToList())
            .Where(rows => rows.Count > 0)
            .ToList() ?? new List<List<List<string>>>();

        if (FindDailyHeader(tables) is { } daily)
        {
            return new ParsedReport(AttendanceExportFormat.DailyReport, ParseDaily(tables, daily));
        }
        if (tables.Any(t => t.Any(r => Same(Cell(r, 0), LabelPersonId))))
        {
            return new ParsedReport(AttendanceExportFormat.MonthlyBlocks, ParseMonthlyBlocks(tables, html));
        }
        if (FindMonthlyRowsHeader(tables) is { } monthly)
        {
            var (records, totals) = ParseMonthlyRows(
                tables, monthly.header, monthly.summaryHeader, monthly.dayColumns, html);
            return new ParsedReport(AttendanceExportFormat.MonthlyRows, records, totals);
        }
        if (LooksLikeMonthlyOverview(tables))
        {
            throw new InvalidDataException(
                "This is a Monthly Overview summary. It contains daily attendance marks and totals, but no check-in/check-out punch times. " +
                "Choose a detailed Monthly report export that includes the Check-in and Check-out columns to fill the monthly templates.");
        }

        throw new InvalidDataException(
            "The file does not look like an attendance export. Expected the software's Daily or Monthly report (.xls).");
    }

    private static bool Same(string? a, string b) =>
        string.Equals(a?.Trim(), b, StringComparison.OrdinalIgnoreCase);

    // --- Daily report: title table, then a table of one row per employee ---

    private static List<string>? FindDailyHeader(List<List<List<string>>> tables) =>
        tables.SelectMany(t => t)
            .FirstOrDefault(r => Same(Cell(r, 1), LabelPersonId) && r.Any(c => c.StartsWith(LabelCheckInPrefix, StringComparison.OrdinalIgnoreCase)));

    private static List<AttendanceRecord> ParseDaily(List<List<List<string>>> tables, List<string> header)
    {
        int ixNo = FindIndex(header, LabelNo);
        int ixId = FindIndex(header, LabelPersonId);
        int ixName = FindIndex(header, LabelName);
        int ixGender = FindIndex(header, LabelGender);
        int ixDate = FindIndex(header, LabelDate);
        int ixIn = header.FindIndex(c => c.StartsWith(LabelCheckInPrefix, StringComparison.OrdinalIgnoreCase));
        int ixOut = header.FindIndex(c => c.StartsWith("Check-out", StringComparison.OrdinalIgnoreCase));
        int ixAttended = FindIndex(header, LabelAttended);

        var records = new List<AttendanceRecord>();
        int order = 0;
        foreach (var row in tables.SelectMany(t => t))
        {
            if (row.Count < header.Count || !int.TryParse(Cell(row, ixNo), out _))
            {
                continue; // title rows, note rows, empty rows
            }

            if (!TryParseDate(Cell(row, ixDate), out var date))
            {
                continue;
            }

            records.Add(new AttendanceRecord(
                Id: Cell(row, ixId),
                Name: Cell(row, ixName),
                Gender: Cell(row, ixGender) is { Length: > 0 } g ? g : TemplateSpec.UnknownGender,
                Date: date,
                InPunch: NormalizePunch(Cell(row, ixIn)),
                OutPunch: NormalizePunch(Cell(row, ixOut)),
                Order: order++,
                Attended: NormalizeMetric(Cell(row, ixAttended))));
        }
        return records;
    }

    // --- Monthly block report: one block of rows per employee, days as columns ---

    private static List<AttendanceRecord> ParseMonthlyBlocks(List<List<List<string>>> tables, string html)
    {
        var from = StartDateOf(html);
        var records = new List<AttendanceRecord>();

        int blockIndex = -1;
        Dictionary<int, int>? dayByColumn = null;
        Dictionary<int, string>? ins = null, outs = null, attended = null, overtime = null, statuses = null;
        string id = "", name = "";

        void Flush()
        {
            if (dayByColumn == null) return;
            var ordered = dayByColumn.OrderBy(kv => kv.Key).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var (col, dayNumber) = ordered[i];
                var date = MapColumnToDate(from, i, dayNumber);
                ins!.TryGetValue(col, out var tin);
                outs!.TryGetValue(col, out var tout);
                attended!.TryGetValue(col, out var totalHours);
                overtime!.TryGetValue(col, out var otHours);
                statuses!.TryGetValue(col, out var status);
                records.Add(new AttendanceRecord(id, name, TemplateSpec.UnknownGender, date,
                    NormalizePunch(tin), NormalizePunch(tout), Order: blockIndex,
                    Attended: NormalizeMetric(totalHours), Overtime: NormalizeMetric(otHours), Status: NormalizeMetric(status)));
            }
        }

        foreach (var row in tables.SelectMany(t => t))
        {
            var label = Cell(row, 0);
            if (Same(label, LabelPersonId))
            {
                Flush();
                blockIndex++;
                dayByColumn = null;
                ins = new Dictionary<int, string>();
                outs = new Dictionary<int, string>();
                attended = new Dictionary<int, string>();
                overtime = new Dictionary<int, string>();
                statuses = new Dictionary<int, string>();
                (id, name) = ExtractBlockIdentity(row);
            }
            else if (Same(label, LabelDate) && blockIndex >= 0)
            {
                dayByColumn = new Dictionary<int, int>();
                for (int c = 1; c < row.Count; c++)
                {
                    if (int.TryParse(Cell(row, c), out var day) && day is >= 1 and <= 31)
                    {
                        dayByColumn[c] = day;
                    }
                }
            }
            else if (Same(label, LabelCheckIn) && dayByColumn != null)
            {
                foreach (var col in dayByColumn.Keys) ins![col] = Cell(row, col);
            }
            else if (Same(label, LabelCheckOut) && dayByColumn != null)
            {
                foreach (var col in dayByColumn.Keys) outs![col] = Cell(row, col);
            }
            else if (Same(label, LabelAttended) && dayByColumn != null)
            {
                foreach (var col in dayByColumn.Keys) attended![col] = Cell(row, col);
            }
            else if (IsOvertimeLabel(label) && dayByColumn != null)
            {
                foreach (var col in dayByColumn.Keys) overtime![col] = Cell(row, col);
            }
            else if (Same(label, LabelStatus) && dayByColumn != null)
            {
                foreach (var col in dayByColumn.Keys) statuses![col] = Cell(row, col);
                Flush();
                dayByColumn = null;
            }
        }
        Flush();
        return records;
    }

    private static (string Id, string Name) ExtractBlockIdentity(List<string> row)
    {
        string id = "", name = "";
        foreach (var cell in row.Skip(1))
        {
            if (cell.Length == 0 || BlockFieldLabels.Contains(cell)) continue;
            if (cell.All(char.IsDigit) && id.Length == 0)
            {
                id = cell;
            }
            else if (name.Length == 0 && !cell.All(char.IsDigit))
            {
                name = cell;
            }
        }
        return (id, name);
    }

    // --- Monthly performance report: metric rows per employee, days as columns ---

    private static (List<string> header, List<string> summaryHeader, List<int> dayColumns)? FindMonthlyRowsHeader(List<List<List<string>>> tables)
    {
        foreach (var table in tables)
        for (var rowIndex = 0; rowIndex < table.Count; rowIndex++)
        {
            var row = table[rowIndex];
            if (!Same(Cell(row, 0), LabelNo) || !row.Any(c => Same(c, LabelPersonId))) continue;
            var dayColumns = new List<int>();
            for (int c = 0; c < row.Count; c++)
            {
                if (int.TryParse(Cell(row, c), out var d) && d is >= 1 and <= 31)
                {
                    dayColumns.Add(c);
                }
            }
            if (dayColumns.Count >= 28)
            {
                var summaryHeader = rowIndex + 1 < table.Count ? table[rowIndex + 1] : new List<string>();
                return (row, summaryHeader, dayColumns);
            }
        }
        return null;
    }

    private static bool LooksLikeMonthlyOverview(List<List<List<string>>> tables) =>
        tables.SelectMany(t => t).Any(row =>
            Same(Cell(row, 0), "Department")
            && Same(Cell(row, 1), LabelName)
            && row.Count(c => int.TryParse(c, out var day) && day is >= 1 and <= 31) >= 28);

    private static (List<AttendanceRecord> Records, List<MonthlyEmployeeTotals> Totals) ParseMonthlyRows(
        List<List<List<string>>> tables,
        List<string> header,
        List<string> summaryHeader,
        List<int> dayColumns,
        string html)
    {
        var from = StartDateOf(html);
        int ixNo = FindIndex(header, LabelNo);
        int ixId = FindIndex(header, LabelPersonId);
        int ixName = FindIndex(header, LabelName);
        int labelColumn = ixName + 1;
        int ixAbsent = FindHeader(header, value => NormalizeHeader(value).StartsWith("absent", StringComparison.Ordinal));
        int ixAttended = FindHeader(header, value => NormalizeHeader(value).StartsWith("attendedactual", StringComparison.Ordinal));
        int ixLeave = FindHeader(header, value => Same(value, "Leave"));
        var leaveColumns = ixLeave < 0
            ? Array.Empty<int>()
            : Enumerable.Range(ixLeave, Math.Max(0, summaryHeader.Count - ixLeave))
                .Where(column => !string.IsNullOrWhiteSpace(Cell(summaryHeader, column)))
                .ToArray();

        var rows = tables.SelectMany(t => t)
            .Where(r => r.Count > labelColumn && r.Count > dayColumns.Max())
            .ToList();

        // metric rows repeat (No, ID, Name) on every row of the employee's block
        var blocks = new Dictionary<string, Dictionary<string, Dictionary<int, string>>>();
        var order = new Dictionary<string, int>();
        var employeeTotals = new Dictionary<string, MonthlyEmployeeTotals>();
        foreach (var row in rows)
        {
            var label = Cell(row, labelColumn);
            var isCheckIn = Same(label, LabelCheckIn);
            var isCheckOut = Same(label, LabelCheckOut);
            var isAttended = Same(label, LabelAttended);
            var isOvertime = IsOvertimeLabel(label);
            var isStatus = Same(label, LabelStatus);
            if (!isCheckIn && !isCheckOut && !isAttended && !isOvertime && !isStatus) continue;
            if (!int.TryParse(Cell(row, ixNo), out var no)) continue;

            var key = $"{Cell(row, ixNo)}|{Cell(row, ixId)}|{Cell(row, ixName)}";
            if (!blocks.TryGetValue(key, out var metrics))
            {
                metrics = new Dictionary<string, Dictionary<int, string>>();
                blocks[key] = metrics;
                order[key] = no;
            }
            if (isCheckIn)
            {
                decimal? absentDays = ReadDecimal(row, ixAbsent);
                decimal? attendedDays = ReadDecimal(row, ixAttended);
                decimal? leaveDays = SumDecimalCells(row, leaveColumns);
                if (absentDays is not null || attendedDays is not null || leaveDays is not null)
                    employeeTotals[key] = new MonthlyEmployeeTotals(Cell(row, ixId), Cell(row, ixName), no,
                        absentDays, attendedDays, leaveDays);
            }
            var values = new Dictionary<int, string>();
            foreach (var col in dayColumns) values[col] = Cell(row, col);
            var metricName = isCheckIn ? LabelCheckIn
                : isCheckOut ? LabelCheckOut
                : isAttended ? LabelAttended
                : isOvertime ? LabelOvertime
                : LabelStatus;
            metrics[metricName] = values;
        }

        var records = new List<AttendanceRecord>();
        foreach (var (key, metrics) in blocks)
        {
            var parts = key.Split('|');
            string id = parts[1], name = parts[2];
            var ins = metrics.GetValueOrDefault(LabelCheckIn) ?? new Dictionary<int, string>();
            var outs = metrics.GetValueOrDefault(LabelCheckOut) ?? new Dictionary<int, string>();
            var attended = metrics.GetValueOrDefault(LabelAttended) ?? new Dictionary<int, string>();
            var overtime = metrics.GetValueOrDefault(LabelOvertime) ?? new Dictionary<int, string>();
            var statuses = metrics.GetValueOrDefault(LabelStatus) ?? new Dictionary<int, string>();
            for (var i = 0; i < dayColumns.Count; i++)
            {
                var col = dayColumns[i];
                if (!int.TryParse(Cell(header, col), out var dayNumber)) continue;
                var date = MapColumnToDate(from, i, dayNumber);
                ins.TryGetValue(col, out var tin);
                outs.TryGetValue(col, out var tout);
                attended.TryGetValue(col, out var totalHours);
                overtime.TryGetValue(col, out var otHours);
                statuses.TryGetValue(col, out var status);
                records.Add(new AttendanceRecord(id, name, TemplateSpec.UnknownGender, date,
                    NormalizePunch(tin), NormalizePunch(tout), Order: order[key],
                    Attended: NormalizeMetric(totalHours), Overtime: NormalizeMetric(otHours), Status: NormalizeMetric(status)));
            }
        }
        return (records, employeeTotals.Values.OrderBy(total => total.Order).ToList());
    }

    private static int FindHeader(List<string> header, Func<string, bool> match) =>
        header.FindIndex(cell => match(cell.Trim()));

    private static string NormalizeHeader(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static decimal? ReadDecimal(List<string> row, int index)
    {
        var value = Cell(row, index).Trim();
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    private static decimal? SumDecimalCells(List<string> row, IReadOnlyList<int> columns)
    {
        if (columns.Count == 0) return null;
        decimal total = 0;
        var foundValue = false;
        foreach (var column in columns)
        {
            var value = Cell(row, column).Trim();
            if (value.Length == 0 || value == "-") continue;
            if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)) continue;
            total += number;
            foundValue = true;
        }
        return foundValue ? total : null;
    }

    // --- helpers ---

    /// <summary>
    /// The software's HTML export omits the opening &lt;tr&gt; of each new employee's
    /// identity row, which makes parsers drop or misplace those cells. A &lt;td&gt; can
    /// never legally follow &lt;/tr&gt; directly, so re-insert the implied row.
    /// </summary>
    private static string NormalizeHtml(string html) =>
        Regex.Replace(html, @"</tr>(\s*)<t([dh])", "</tr>$1<tr><t$2", RegexOptions.IgnoreCase);

    private static List<List<string>> ExtractRows(HtmlNode table)
    {
        var rows = new List<List<string>>();
        // Cells carried down from earlier rows by rowspan: column -> (text, rows left after current).
        var active = new SortedDictionary<int, (string Text, int Remaining)>();

        foreach (var tr in table.SelectNodes("./tr") ?? new HtmlNodeCollection(table))
        {
            var row = new SortedDictionary<int, string>();
            foreach (var (col, cell) in active)
            {
                row[col] = cell.Text;
            }

            var survivors = new SortedDictionary<int, (string Text, int Remaining)>();
            foreach (var (col, cell) in active)
            {
                if (cell.Remaining > 1) survivors[col] = (cell.Text, cell.Remaining - 1);
            }
            active = survivors;

            var column = 0;
            foreach (var td in tr.SelectNodes("./td|./th") ?? new HtmlNodeCollection(tr))
            {
                while (row.ContainsKey(column)) column++;
                // \u00A0 (from &nbsp;) would otherwise survive Trim() and break label matching
                var text = System.Net.WebUtility.HtmlDecode(td.InnerText).Replace('\u00A0', ' ').Trim();
                var colspan = SpanOf(td, "colspan");
                var rowspan = SpanOf(td, "rowspan");
                for (var k = 0; k < colspan; k++, column++)
                {
                    row[column] = k == 0 ? text : "";
                    if (rowspan > 1) active[column] = (k == 0 ? text : "", rowspan - 1);
                }
            }

            rows.Add(Enumerable.Range(0, row.Count == 0 ? 0 : row.Keys.Max() + 1)
                .Select(i => row.GetValueOrDefault(i, ""))
                .ToList());
        }
        return rows;
    }

    /// <summary>Reads a colspan/rowspan attribute defensively: the export writes values like "1," and "4;".</summary>
    private static int SpanOf(HtmlNode cell, string attribute)
    {
        var digits = new string(cell.GetAttributeValue(attribute, "1").Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) && value is >= 1 and <= 200 ? value : 1;
    }

    private static string Cell(List<string> row, int index) =>
        index >= 0 && index < row.Count ? row[index] : "";

    private static int FindIndex(List<string> row, string value) =>
        row.FindIndex(c => Same(c, value));

    private static bool IsOvertimeLabel(string value) =>
        Same(value, LabelOvertime) || Same(value, "Overtime") || Same(value, "Over Time");

    private static string? NormalizeMetric(string? value)
    {
        value = value?.Trim();
        return string.IsNullOrEmpty(value) || value == "-" ? null : value;
    }

    private static string? NormalizePunch(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value == "-") return null;
        if (Regex.IsMatch(value, @"^\d{1,2}:\d{2}:\d{2}$")) value = value[..value.LastIndexOf(':')];
        if (!Regex.IsMatch(value, @"^\d{1,2}:\d{2}$")) return null;

        var hour = int.Parse(value[..value.IndexOf(':')], CultureInfo.InvariantCulture);
        var minute = int.Parse(value[(value.IndexOf(':') + 1)..], CultureInfo.InvariantCulture);
        if (hour > 23 || minute > 59) return null; // "24:00" or "06:99" are not real punches
        return $"{hour:00}:{minute:00}";
    }

    /// <summary>
    /// Finds the report's start date ("From: dd-MM-yyyy ...") in the document.
    /// Every content date in monthly exports is derived relative to this date.
    /// </summary>
    private static DateOnly StartDateOf(string html)
    {
        var match = Regex.Match(html, @"\b(\d{1,2})[-/.](\d{1,2})[-/.](\d{4})\b");
        if (match.Success
            && TryMakeDate(int.Parse(match.Groups[3].Value), int.Parse(match.Groups[2].Value), int.Parse(match.Groups[1].Value), out var from))
        {
            return from;
        }
        throw new InvalidDataException("Could not find a From/To date in the report to determine the month.");
    }

    private static readonly string[] DateFormats =
    {
        "dd-MM-yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "dd.MM.yyyy", "dd-MM-yy",
    };

    private static bool TryParseDate(string value, out DateOnly date)
    {
        value = value.Trim();
        if (DateOnly.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }
        // tolerate timestamps like "06-10-2026 00:00"
        var datePart = value.Split(' ', 'T')[0];
        return DateOnly.TryParseExact(datePart, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool TryMakeDate(int year, int month, int day, out DateOnly date)
    {
        if (month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month))
        {
            date = new DateOnly(year, month, day);
            return true;
        }
        date = default;
        return false;
    }

    /// <summary>
    /// Maps the n-th day column of a monthly report to a calendar date.
    /// Day numbers are trusted when they agree with the running date sequence starting at the
    /// report's From date (calendar months, partial months); columns that break the sequence
    /// fall back to their day number within the From month (rare skipped-day exports); anything
    /// else continues the sequence, which keeps ranges that cross into a next month correct.
    /// </summary>
    private static DateOnly MapColumnToDate(DateOnly from, int index, int? dayNumber)
    {
        var sequential = from.AddDays(index);
        if (dayNumber.HasValue && dayNumber.Value == sequential.Day)
        {
            return sequential;
        }
        if (dayNumber.HasValue && TryMakeDate(from.Year, from.Month, dayNumber.Value, out var byDayNumber))
        {
            return byDayNumber;
        }
        return sequential;
    }
}
