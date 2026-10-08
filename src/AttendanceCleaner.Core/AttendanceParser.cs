using System.Globalization;
using System.Text;
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
    static AttendanceParser() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

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
        return ParseBytes(File.ReadAllBytes(path));
    }

    /// <summary>Decodes the export consistently for file paths and platform file-picker streams.</summary>
    public static ParsedReport ParseBytes(ReadOnlySpan<byte> bytes)
    {
        ValidateInputIsNotWorkbook(bytes[..Math.Min(8, bytes.Length)]);
        Encoding encoding;
        int preambleLength;
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }))
            (encoding, preambleLength) = (new UTF32Encoding(false, false, true), 4);
        else if (bytes.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF }))
            (encoding, preambleLength) = (new UTF32Encoding(true, false, true), 4);
        else if (bytes.StartsWith(new byte[] { 0xFF, 0xFE }))
            (encoding, preambleLength) = (new UnicodeEncoding(false, false, true), 2);
        else if (bytes.StartsWith(new byte[] { 0xFE, 0xFF }))
            (encoding, preambleLength) = (new UnicodeEncoding(true, false, true), 2);
        else if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            (encoding, preambleLength) = (new UTF8Encoding(false, true), 3);
        else
        {
            preambleLength = 0;
            var prefix = Encoding.ASCII.GetString(bytes[..Math.Min(4096, bytes.Length)]);
            var charset = Regex.Match(prefix, "<meta\\b[^>]*\\bcharset\\s*=\\s*[\"']?\\s*(?<name>[a-z0-9._-]+)", RegexOptions.IgnoreCase);
            try
            {
                encoding = charset.Success
                    ? Encoding.GetEncoding(charset.Groups["name"].Value, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                    : new UTF8Encoding(false, true);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("The export declares an unsupported text encoding. Export the report again using Unicode or UTF-8.", exception);
            }
        }
        try
        {
            return Parse(encoding.GetString(bytes[preambleLength..]));
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The export contains invalid text for its encoding. Download the report again or export it using Unicode or UTF-8.", exception);
        }
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
            return new ParsedReport(AttendanceExportFormat.MonthlyBlocks, ParseMonthlyBlocks(tables));
        }
        if (FindMonthlyRowsHeader(tables) is { } monthly)
        {
            var (records, totals) = ParseMonthlyRows(
                tables, monthly.header, monthly.summaryHeader, monthly.dayColumns);
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
        if (new[] { ixNo, ixId, ixName, ixDate, ixIn, ixOut }.Any(index => index < 0))
            throw new InvalidDataException("The daily report is missing an employee, date, or check-in/check-out column.");

        var records = new List<AttendanceRecord>();
        int order = 0;
        foreach (var row in tables.SelectMany(t => t))
        {
            if (!int.TryParse(Cell(row, ixNo), out _))
            {
                continue; // title rows, note rows, empty rows
            }

            if (!TryParseDate(Cell(row, ixDate), out var date))
            {
                throw new InvalidDataException(
                    $"Invalid attendance date '{Cell(row, ixDate)}' for employee '{Cell(row, ixId)}' ({Cell(row, ixName)}). " +
                    "Use day-month-year or year-month-day dates and export the report again.");
            }
            if (row.Count <= Math.Max(ixIn, ixOut))
                throw new InvalidDataException($"The attendance row for employee '{Cell(row, ixId)}' on {date:dd-MM-yyyy} is incomplete.");

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

    private static List<AttendanceRecord> ParseMonthlyBlocks(List<List<List<string>>> tables)
    {
        var range = ReportRangeOf(tables);
        var records = new List<AttendanceRecord>();

        int blockIndex = -1;
        Dictionary<int, DateOnly>? dayByColumn = null;
        Dictionary<int, string>? ins = null, outs = null, attended = null, overtime = null, statuses = null;
        string id = "", name = "";
        bool hasDates = false;

        void ValidateEmployeeDates()
        {
            if (blockIndex >= 0 && !hasDates)
                throw new InvalidDataException($"The monthly report has no date columns for employee '{id}' ({name}).");
        }

        void ReadMetric(List<string> row, Dictionary<int, string> values)
        {
            var finalDayColumn = dayByColumn!.Keys.Max();
            if (row.Count <= finalDayColumn)
                throw new InvalidDataException($"The monthly '{Cell(row, 0)}' row for employee '{id}' is incomplete.");
            if (row.Skip(finalDayColumn + 1).Any(value => NormalizeMetric(value) is { } metric
                && !(decimal.TryParse(metric, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) && number == 0)))
                throw new InvalidDataException($"The monthly '{Cell(row, 0)}' row for employee '{id}' contains data without a date column.");
            foreach (var col in dayByColumn.Keys)
            {
                var value = Cell(row, col);
                if (values.TryGetValue(col, out var previous) && !MetricValuesEqual(Cell(row, 0), previous, value))
                    throw new InvalidDataException($"Conflicting monthly '{Cell(row, 0)}' rows for employee '{id}' ({name}) on {dayByColumn[col]:dd-MM-yyyy}.");
                values[col] = value;
            }
        }

        void Flush()
        {
            if (dayByColumn == null) return;
            if (ins!.Count == 0 || outs!.Count == 0)
                throw new InvalidDataException($"The monthly report is missing a Check-in or Check-out row for employee '{id}' ({name}).");
            var ordered = dayByColumn.OrderBy(kv => kv.Key).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var (col, date) = ordered[i];
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
                ValidateEmployeeDates();
                blockIndex++;
                hasDates = false;
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
                if (hasDates)
                    throw new InvalidDataException($"Repeated monthly date rows for employee '{id}' ({name}). Export the report again.");
                var days = new Dictionary<int, int>();
                for (int c = 1; c < row.Count; c++)
                {
                    if (int.TryParse(Cell(row, c), out var day))
                    {
                        days[c] = day;
                    }
                    else if (!string.IsNullOrWhiteSpace(Cell(row, c))
                        || row.Skip(c + 1).Any(cell => !string.IsNullOrWhiteSpace(cell)))
                        throw new InvalidDataException($"Invalid monthly date column '{Cell(row, c)}'. Expected a day number.");
                }
                dayByColumn = MapDayColumns(range, days);
                hasDates = true;
            }
            else if (Same(label, LabelCheckIn) && dayByColumn != null)
            {
                ReadMetric(row, ins!);
            }
            else if (Same(label, LabelCheckOut) && dayByColumn != null)
            {
                ReadMetric(row, outs!);
            }
            else if (Same(label, LabelAttended) && dayByColumn != null)
            {
                ReadMetric(row, attended!);
            }
            else if (IsOvertimeLabel(label) && dayByColumn != null)
            {
                ReadMetric(row, overtime!);
            }
            else if (Same(label, LabelStatus) && dayByColumn != null)
            {
                ReadMetric(row, statuses!);
            }
        }
        Flush();
        ValidateEmployeeDates();
        return records;
    }

    private static (string Id, string Name) ExtractBlockIdentity(List<string> row)
    {
        var id = FieldValue(LabelPersonId);
        var name = FieldValue(LabelEmployeeName);
        if (id.Length == 0 || name.Length == 0)
            throw new InvalidDataException("The monthly report has an employee block without a Person ID or Employee Name.");
        return (id, name);

        string FieldValue(string label)
        {
            var index = FindIndex(row, label);
            return index < 0 ? "" : row.Skip(index + 1)
                .TakeWhile(cell => !BlockFieldLabels.Contains(cell))
                .FirstOrDefault(cell => !string.IsNullOrWhiteSpace(cell)) ?? "";
        }
    }

    // --- Monthly performance report: metric rows per employee, days as columns ---

    private static (List<string> header, List<string> summaryHeader, List<int> dayColumns)? FindMonthlyRowsHeader(List<List<List<string>>> tables)
    {
        foreach (var table in tables)
            for (var rowIndex = 0; rowIndex < table.Count; rowIndex++)
            {
                var row = table[rowIndex];
                var nameColumn = FindIndex(row, LabelName);
                if (!Same(Cell(row, 0), LabelNo) || FindIndex(row, LabelPersonId) < 0 || nameColumn < 0) continue;
                var dayColumns = new List<int>();
                for (int c = nameColumn + 2; c < row.Count; c++)
                {
                    if (int.TryParse(Cell(row, c), out _))
                    {
                        dayColumns.Add(c);
                    }
                    else
                    {
                        if (IsMonthlySummaryHeader(Cell(row, c))) break;
                        throw new InvalidDataException($"Invalid monthly date column '{Cell(row, c)}'. Expected a day number.");
                    }
                }
                if (dayColumns.Count > 0 && tables.SelectMany(rows => rows)
                    .Any(metric => Same(Cell(metric, nameColumn + 1), LabelCheckIn)
                        || Same(Cell(metric, nameColumn + 1), LabelCheckOut)))
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
            && row.Any(c => int.TryParse(c, out var day) && day is >= 1 and <= 31));

    private static (List<AttendanceRecord> Records, List<MonthlyEmployeeTotals> Totals) ParseMonthlyRows(
        List<List<List<string>>> tables,
        List<string> header,
        List<string> summaryHeader,
        List<int> dayColumns)
    {
        var datesByColumn = MapDayColumns(ReportRangeOf(tables), dayColumns
            .ToDictionary(column => column, column => int.Parse(Cell(header, column), CultureInfo.InvariantCulture)));
        int ixNo = FindIndex(header, LabelNo);
        int ixId = FindIndex(header, LabelPersonId);
        int ixName = FindIndex(header, LabelName);
        int labelColumn = ixName + 1;
        int ixAbsent = FindHeader(header, value => NormalizeHeader(value).StartsWith("absent", StringComparison.Ordinal));
        int ixAttended = FindHeader(header, value => NormalizeHeader(value).StartsWith("attendedactual", StringComparison.Ordinal));
        int ixLeave = FindHeader(header, value => Same(value, "Leave"));
        var leaveEnd = ixLeave < 0 ? 0 : Enumerable.Range(ixLeave + 1, header.Count - ixLeave - 1)
            .FirstOrDefault(column => !string.IsNullOrWhiteSpace(Cell(header, column)), header.Count);
        var leaveColumns = ixLeave < 0
            ? Array.Empty<int>()
            : Enumerable.Range(ixLeave, Math.Max(0, Math.Min(leaveEnd, summaryHeader.Count) - ixLeave))
                .Where(column => !string.IsNullOrWhiteSpace(Cell(summaryHeader, column)))
                .ToArray();

        var rows = tables.SelectMany(t => t)
            .Where(r => r.Count > labelColumn)
            .ToList();

        // metric rows repeat (No, ID, Name) on every row of the employee's block
        var blocks = new Dictionary<(int No, string Id, string Name), Dictionary<string, Dictionary<int, string>>>();
        var employeeTotals = new Dictionary<(int No, string Id, string Name), MonthlyEmployeeTotals>();
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
            if (row.Count <= dayColumns.Max())
                throw new InvalidDataException($"The monthly '{label}' row for employee '{Cell(row, ixId)}' is incomplete.");

            var key = (No: no, Id: Cell(row, ixId), Name: Cell(row, ixName));
            if (!blocks.TryGetValue(key, out var metrics))
            {
                metrics = new Dictionary<string, Dictionary<int, string>>();
                blocks[key] = metrics;
            }
            if (isCheckIn)
            {
                decimal? absentDays = ReadDecimal(row, ixAbsent);
                decimal? attendedDays = ReadDecimal(row, ixAttended);
                decimal? leaveDays = SumDecimalCells(row, leaveColumns);
                if (absentDays is not null || attendedDays is not null || leaveDays is not null)
                {
                    var totals = new MonthlyEmployeeTotals(Cell(row, ixId), Cell(row, ixName), no,
                        absentDays, attendedDays, leaveDays);
                    if (employeeTotals.TryGetValue(key, out var previous) && previous != totals)
                        throw new InvalidDataException($"Conflicting monthly summary totals for employee '{key.Id}' ({key.Name}).");
                    employeeTotals[key] = totals;
                }
            }
            var values = new Dictionary<int, string>();
            foreach (var col in dayColumns) values[col] = Cell(row, col);
            var metricName = isCheckIn ? LabelCheckIn
                : isCheckOut ? LabelCheckOut
                : isAttended ? LabelAttended
                : isOvertime ? LabelOvertime
                : LabelStatus;
            if (metrics.TryGetValue(metricName, out var previousValues)
                && dayColumns.Any(column => !MetricValuesEqual(metricName, previousValues[column], values[column])))
                throw new InvalidDataException($"Conflicting monthly '{label}' rows for employee '{key.Id}' ({key.Name}).");
            metrics[metricName] = values;
        }

        var records = new List<AttendanceRecord>();
        foreach (var (key, metrics) in blocks)
        {
            var (_, id, name) = key;
            if (!metrics.TryGetValue(LabelCheckIn, out var ins) || !metrics.TryGetValue(LabelCheckOut, out var outs))
                throw new InvalidDataException($"The monthly report is missing a Check-in or Check-out row for employee '{id}' ({name}).");
            var attended = metrics.GetValueOrDefault(LabelAttended) ?? new Dictionary<int, string>();
            var overtime = metrics.GetValueOrDefault(LabelOvertime) ?? new Dictionary<int, string>();
            var statuses = metrics.GetValueOrDefault(LabelStatus) ?? new Dictionary<int, string>();
            for (var i = 0; i < dayColumns.Count; i++)
            {
                var col = dayColumns[i];
                var date = datesByColumn[col];
                ins.TryGetValue(col, out var tin);
                outs.TryGetValue(col, out var tout);
                attended.TryGetValue(col, out var totalHours);
                overtime.TryGetValue(col, out var otHours);
                statuses.TryGetValue(col, out var status);
                records.Add(new AttendanceRecord(id, name, TemplateSpec.UnknownGender, date,
                    NormalizePunch(tin), NormalizePunch(tout), Order: key.No,
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

        foreach (var tr in table.SelectNodes("./tr|./thead/tr|./tbody/tr|./tfoot/tr") ?? new HtmlNodeCollection(table))
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

    private static bool MetricValuesEqual(string label, string left, string right) =>
        Same(label, LabelCheckIn) || Same(label, LabelCheckOut)
            ? NormalizePunch(left) == NormalizePunch(right)
            : NormalizeMetric(left) == NormalizeMetric(right);

    private static bool IsMonthlySummaryHeader(string value)
    {
        if (value.Trim() == "*") return true;
        var normalized = NormalizeHeader(value);
        return new[] { "work", "absent", "attendedactual", "leave", "late", "early", "overtime", "total" }
            .Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal));
    }

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

    private readonly record struct ReportRange(DateOnly From, DateOnly To);

    /// <summary>Reads the declared range from decoded cells, never from metadata or employee joining dates.</summary>
    private static ReportRange ReportRangeOf(List<List<List<string>>> tables)
    {
        var text = string.Join(" ", tables.SelectMany(table => table).SelectMany(row => row));
        var match = Regex.Match(text, @"\bFrom\s*:?\s*(?<from>\S+)\s+.*?\bTo\s*:?\s*(?<to>\S+)",
            RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            // Some exports omit the From label but still have an explicit date-to-date title.
            match = Regex.Match(text,
                @"(?<![\d/.-])(?<from>\d{1,4}[-/.]\d{1,2}[-/.]\d{2,4})(?:\s+\d{1,2}:\d{2}(?::\d{2})?)?\s+To\s*:?\s*(?<to>\S+)",
                RegexOptions.IgnoreCase);
        }
        if (!match.Success)
            throw new InvalidDataException("Could not find a From/To date range in the report to determine the month.");

        var fromText = match.Groups["from"].Value;
        var toText = match.Groups["to"].Value;
        if (!TryParseDate(fromText, out var from) || !TryParseDate(toText, out var to))
            throw new InvalidDataException($"Invalid report date range '{fromText}' to '{toText}'. Use day-month-year or year-month-day dates.");
        if (to < from)
            throw new InvalidDataException($"Invalid report date range: To date {to:dd-MM-yyyy} is before From date {from:dd-MM-yyyy}.");
        return new ReportRange(from, to);
    }

    private static readonly string[] DateFormats =
    {
        "d-M-yyyy", "d/M/yyyy", "d.M.yyyy", "yyyy-M-d", "yyyy/M/d", "yyyy.M.d",
        "d-M-yy", "d/M/yy", "d.M.yy",
    };

    private static bool TryParseDate(string value, out DateOnly date)
    {
        value = value.Trim();
        if (DateOnly.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return true;
        }
        // tolerate timestamps like "06-10-2026 00:00"
        var datePart = value.Split(new[] { ' ', 'T', 't', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return DateOnly.TryParseExact(datePart, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static bool TryMakeDate(int year, int month, int day, out DateOnly date)
    {
        if (year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month))
        {
            date = new DateOnly(year, month, day);
            return true;
        }
        date = default;
        return false;
    }

    /// <summary>
    /// Resolves ordered day numbers within the declared range, preserving skipped days and
    /// advancing the month/year on rollover. Invalid headers never become invented dates.
    /// </summary>
    private static Dictionary<int, DateOnly> MapDayColumns(ReportRange range, Dictionary<int, int> days)
    {
        if (days.Count == 0)
            throw new InvalidDataException("The monthly report has no date columns.");
        var result = new Dictionary<int, DateOnly>();
        var month = new DateOnly(range.From.Year, range.From.Month, 1);
        int? previousDay = null;
        foreach (var (column, day) in days.OrderBy(pair => pair.Key))
        {
            if (day is < 1 or > 31 || day == previousDay)
                throw new InvalidDataException($"Invalid or duplicate monthly date column '{day}'. Expected distinct calendar days in chronological order.");
            if (previousDay is { } previous && day < previous)
            {
                if (month.Year == 9999 && month.Month == 12)
                    throw new InvalidDataException("The monthly date columns extend beyond the supported calendar.");
                month = month.AddMonths(1);
            }
            if (!TryMakeDate(month.Year, month.Month, day, out var date))
                throw new InvalidDataException($"Invalid monthly date column '{day}' for {month:MMMM yyyy}.");
            if (date < range.From || date > range.To)
                throw new InvalidDataException($"Monthly date column {date:dd-MM-yyyy} is outside the report's From/To date range ({range.From:dd-MM-yyyy} to {range.To:dd-MM-yyyy}).");
            result[column] = date;
            previousDay = day;
        }
        return result;
    }
}
