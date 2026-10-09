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
            var charset = DeclaredHtmlCharset(bytes);
            try
            {
                encoding = charset is not null
                    ? Encoding.GetEncoding(charset, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                    : new UTF8Encoding(false, true);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
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

    private static string? DeclaredHtmlCharset(ReadOnlySpan<byte> bytes)
    {
        // Charset names and HTML tags are ASCII. Head tags may be omitted in HTML,
        // so read actual metadata before the body or first table, including large stylesheets.
        var document = new HtmlDocument { OptionReadEncoding = false };
        document.LoadHtml(Encoding.ASCII.GetString(bytes));
        var contentStart = document.DocumentNode.SelectSingleNode("//body|//table")?.StreamPosition ?? int.MaxValue;
        var metadata = document.DocumentNode.SelectNodes("//meta");
        if (metadata is null) return null;
        foreach (var meta in metadata)
        {
            if (meta.StreamPosition >= contentStart) continue;
            var charset = System.Net.WebUtility.HtmlDecode(meta.GetAttributeValue("charset", "")).Trim();
            if (charset.Length > 0) return charset;
            if (!Same(meta.GetAttributeValue("http-equiv", ""), "Content-Type")) continue;
            var content = System.Net.WebUtility.HtmlDecode(meta.GetAttributeValue("content", ""));
            var match = Regex.Match(content, @"(?:^|;)\s*charset\s*=\s*[""']?(?<name>[^\s;""']+)", RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups["name"].Value;
        }
        return null;
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
        // Text in comments, scripts, and styles cannot prove the download is complete.
        var markup = Regex.Replace(html, @"<!--[\s\S]*?(?:-->|$)|<(script|style)\b[^>]*>[\s\S]*?(?:</\1\s*>|$)",
            "", RegexOptions.IgnoreCase);
        var hasClosingDocumentTag = Regex.Matches(markup, @"<(?:""[^""]*""|'[^']*'|[^'""<>])*>")
            .Any(tag => Regex.IsMatch(tag.Value, @"^</(?:html|body)\s*>$", RegexOptions.IgnoreCase));
        if (!hasClosingDocumentTag)
        {
            throw new InvalidDataException(
                "This file looks incomplete (possibly a failed download). Please download the report from the attendance software again.");
        }

        var doc = new HtmlDocument { OptionReadEncoding = false };
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
        string.Equals(Regex.Replace(a ?? "", @"\s+", " ").Trim(),
            Regex.Replace(b, @"\s+", " ").Trim(), StringComparison.OrdinalIgnoreCase);

    // --- Daily report: title table, then a table of one row per employee ---

    private static List<string>? FindDailyHeader(List<List<List<string>>> tables) =>
        tables.SelectMany(t => t)
            .FirstOrDefault(IsDailyHeader);

    private static bool IsDailyHeader(List<string> row) =>
        FindIndex(row, LabelNo) >= 0 && FindIndex(row, LabelPersonId) >= 0
        && row.Any(cell => cell.StartsWith(LabelCheckInPrefix, StringComparison.OrdinalIgnoreCase));

    private static List<AttendanceRecord> ParseDaily(List<List<List<string>>> tables, List<string> header)
    {
        int ixNo = -1, ixId = -1, ixName = -1, ixGender = -1, ixDate = -1, ixIn = -1, ixOut = -1;
        int ixAttended = -1, ixOvertime = -1, ixStatus = -1;
        int headerWidth = 0;
        int[] additionalPunchColumns = [];
        void ReadHeader(List<string> current)
        {
            ixNo = FindIndex(current, LabelNo);
            ixId = FindIndex(current, LabelPersonId);
            ixName = FindIndex(current, LabelName);
            ixGender = FindIndex(current, LabelGender);
            ixDate = FindIndex(current, LabelDate);
            ixIn = current.FindIndex(IsFirstCheckIn);
            ixOut = current.FindIndex(IsFirstCheckOut);
            ixAttended = FindIndex(current, LabelAttended);
            ixOvertime = current.FindIndex(IsOvertimeLabel);
            ixStatus = FindIndex(current, LabelStatus);
            headerWidth = current.Count;
            additionalPunchColumns = current.Select((label, column) => (label, column))
                .Where(cell => IsAdditionalPunch(cell.label)).Select(cell => cell.column).ToArray();
            if (new[] { ixNo, ixId, ixName, ixDate, ixIn, ixOut }.Any(index => index < 0))
                throw new InvalidDataException("The daily report is missing an employee, date, or check-in/check-out column.");
        }
        ReadHeader(header);

        var records = new List<AttendanceRecord>();
        int order = 0;
        var hasHeader = false;
        foreach (var row in tables.SelectMany(t => t))
        {
            if (IsDailyHeader(row))
            {
                ReadHeader(row);
                hasHeader = true;
                continue;
            }
            if (!hasHeader) continue;
            if (!int.TryParse(Cell(row, ixNo), out _))
            {
                var looksLikeEmployee = !string.IsNullOrWhiteSpace(Cell(row, ixId)) && !string.IsNullOrWhiteSpace(Cell(row, ixName))
                    && new[] { ixDate, ixIn, ixOut, ixAttended, ixOvertime, ixStatus }
                        .Any(column => NormalizeMetric(Cell(row, column)) is not null);
                if (TryParseDate(Cell(row, ixDate), out _) || looksLikeEmployee)
                    throw new InvalidDataException($"The daily attendance row for employee '{Cell(row, ixId)}' has an invalid or missing row number.");
                continue; // title rows, note rows, empty rows
            }

            if (!TryParseDate(Cell(row, ixDate), out var date))
            {
                throw new InvalidDataException(
                    $"Invalid attendance date '{Cell(row, ixDate)}' for employee '{Cell(row, ixId)}' ({Cell(row, ixName)}). " +
                    "Use day-month-year or year-month-day dates and export the report again.");
            }
            if (row.Count <= new[] { ixNo, ixId, ixName, ixDate, ixIn, ixOut, ixAttended, ixOvertime, ixStatus }.Max())
                throw new InvalidDataException($"The attendance row for employee '{Cell(row, ixId)}' on {date:dd-MM-yyyy} is incomplete.");
            if (row.Skip(headerWidth).Any(value => !IsEmptyMetricPadding(value)))
                throw new InvalidDataException($"The daily attendance row for employee '{Cell(row, ixId)}' on {date:dd-MM-yyyy} contains data without a column heading.");
            var id = Cell(row, ixId);
            var name = Cell(row, ixName);
            ValidateIdentity(id, name);
            if (additionalPunchColumns.Any(column => !IsEmptyMetricPadding(Cell(row, column))))
                throw AdditionalPunchError(id, name, date);

            records.Add(new AttendanceRecord(
                Id: id,
                Name: name,
                Gender: Cell(row, ixGender) is { Length: > 0 } g ? g : TemplateSpec.UnknownGender,
                Date: date,
                InPunch: ReadPunch(Cell(row, ixIn), id, name, date, "Check-in"),
                OutPunch: ReadPunch(Cell(row, ixOut), id, name, date, "Check-out"),
                Order: order++,
                Attended: ReadHours(Cell(row, ixAttended), id, name, date, LabelAttended),
                Overtime: ReadHours(Cell(row, ixOvertime), id, name, date, LabelOvertime),
                Status: NormalizeMetric(Cell(row, ixStatus))));
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
            if (row.Skip(finalDayColumn + 1).Any(value => !IsEmptyMetricPadding(value)))
                throw new InvalidDataException($"The monthly '{Cell(row, 0)}' row for employee '{id}' contains data without a date column.");
            foreach (var col in dayByColumn.Keys)
            {
                var value = Cell(row, col);
                ValidateMetricValue(Cell(row, 0), value, id, name, dayByColumn[col]);
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
                    ReadPunch(tin, id, name, date, LabelCheckIn), ReadPunch(tout, id, name, date, LabelCheckOut), Order: blockIndex,
                    Attended: ReadHours(totalHours, id, name, date, LabelAttended),
                    Overtime: ReadHours(otHours, id, name, date, LabelOvertime), Status: NormalizeMetric(status)));
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
                var days = new Dictionary<int, int>();
                for (int c = 1; c < row.Count; c++)
                {
                    if (int.TryParse(Cell(row, c), out var day))
                    {
                        days[c] = day;
                    }
                    // Fixed-width exports pad unused day slots (for example September 31) with '-'.
                    // Padding is valid only after the final date; ReadMetric rejects data in those slots.
                    else if (NormalizeMetric(Cell(row, c)) is not null
                        || row.Skip(c + 1).Any(cell => NormalizeMetric(cell) is not null))
                        throw new InvalidDataException($"Invalid monthly date column '{Cell(row, c)}'. Expected a day number.");
                }
                var dates = MapDayColumns(range, days);
                if (hasDates && !dayByColumn!.OrderBy(pair => pair.Key).SequenceEqual(dates.OrderBy(pair => pair.Key)))
                    throw new InvalidDataException($"Conflicting monthly date rows for employee '{id}' ({name}). Export the report again.");
                dayByColumn = dates;
                hasDates = true;
            }
            else if (blockIndex >= 0 && dayByColumn is null && IsMetricLabel(label))
            {
                throw new InvalidDataException($"The monthly '{label}' row for employee '{id}' ({name}) appears before its date columns.");
            }
            else if (IsAdditionalPunch(label) && blockIndex >= 0)
            {
                if (row.Skip(1).Any(value => !IsEmptyMetricPadding(value))) throw AdditionalPunchError(id, name);
            }
            else if (IsFirstCheckIn(label) && dayByColumn != null)
            {
                ReadMetric(row, ins!);
            }
            else if (IsFirstCheckOut(label) && dayByColumn != null)
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
                .TakeWhile(cell => !BlockFieldLabels.Any(field => Same(cell, field)))
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
                var dayColumns = FindMonthlyDayColumns(row, nameColumn);
                if (dayColumns.Count > 0 && tables.SelectMany(rows => rows)
                    .Any(metric => IsFirstCheckIn(Cell(metric, nameColumn + 1))
                        || IsFirstCheckOut(Cell(metric, nameColumn + 1))))
                {
                    var summaryHeader = rowIndex + 1 < table.Count ? table[rowIndex + 1] : new List<string>();
                    return (row, summaryHeader, dayColumns);
                }
            }
        return null;
    }

    private static List<int> FindMonthlyDayColumns(List<string> header, int nameColumn)
    {
        var dayColumns = new List<int>();
        var hasPadding = false;
        for (var column = nameColumn + 2; column < header.Count; column++)
        {
            var value = Cell(header, column);
            if (IsMonthlySummaryHeader(value)) break;
            if (int.TryParse(value, out _) && !hasPadding)
            {
                dayColumns.Add(column);
            }
            else if (NormalizeMetric(value) is null)
            {
                hasPadding = true;
            }
            else
            {
                throw new InvalidDataException($"Invalid monthly date column '{value}'. Expected a day number.");
            }
        }
        return dayColumns;
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
        var summaryStart = Enumerable.Range(dayColumns.Max() + 1, header.Count - dayColumns.Max() - 1)
            .FirstOrDefault(column => IsMonthlySummaryHeader(Cell(header, column)), header.Count);
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
            if (FindIndex(row, LabelNo) >= 0 && FindIndex(row, LabelPersonId) >= 0 && FindIndex(row, LabelName) >= 0
                && row.Skip(labelColumn + 1).Any(value => int.TryParse(value, out _)))
            {
                var repeatedColumns = FindMonthlyDayColumns(row, FindIndex(row, LabelName));
                if (!repeatedColumns.SequenceEqual(dayColumns)
                    || dayColumns.Any(column => int.Parse(Cell(row, column), CultureInfo.InvariantCulture)
                        != int.Parse(Cell(header, column), CultureInfo.InvariantCulture))
                    || FindIndex(row, LabelNo) != ixNo || FindIndex(row, LabelPersonId) != ixId || FindIndex(row, LabelName) != ixName
                    || Enumerable.Range(summaryStart, Math.Max(0, header.Count - summaryStart))
                        .Any(column => !Same(Cell(row, column), Cell(header, column))))
                    throw new InvalidDataException("The monthly report contains conflicting date or summary headers. Export one consistent report range again.");
                continue;
            }
            var label = Cell(row, labelColumn);
            if (IsAdditionalPunch(label))
            {
                if (row.Skip(labelColumn + 1).Take(Math.Max(0, summaryStart - labelColumn - 1))
                    .Any(value => !IsEmptyMetricPadding(value))
                    || row.Skip(header.Count).Any(value => !IsEmptyMetricPadding(value)))
                    throw AdditionalPunchError(Cell(row, ixId), Cell(row, ixName));
                continue;
            }
            var isCheckIn = IsFirstCheckIn(label);
            var isCheckOut = IsFirstCheckOut(label);
            var isAttended = Same(label, LabelAttended);
            var isOvertime = IsOvertimeLabel(label);
            var isStatus = Same(label, LabelStatus);
            if (!isCheckIn && !isCheckOut && !isAttended && !isOvertime && !isStatus) continue;
            if (!int.TryParse(Cell(row, ixNo), out var no))
                throw new InvalidDataException($"The monthly '{label}' row for employee '{Cell(row, ixId)}' has an invalid or missing row number.");
            if (row.Count <= dayColumns.Max())
                throw new InvalidDataException($"The monthly '{label}' row for employee '{Cell(row, ixId)}' is incomplete.");
            if (row.Skip(dayColumns.Max() + 1).Take(Math.Max(0, summaryStart - dayColumns.Max() - 1))
                .Any(value => !IsEmptyMetricPadding(value)))
                throw new InvalidDataException($"The monthly '{label}' row for employee '{Cell(row, ixId)}' contains data without a date column.");
            if (row.Skip(header.Count).Any(value => !IsEmptyMetricPadding(value)))
                throw new InvalidDataException($"The monthly '{label}' row for employee '{Cell(row, ixId)}' contains data without a date column.");

            var key = (No: no, Id: Cell(row, ixId), Name: Cell(row, ixName));
            ValidateIdentity(key.Id, key.Name);
            if (!blocks.TryGetValue(key, out var metrics))
            {
                metrics = new Dictionary<string, Dictionary<int, string>>();
                blocks[key] = metrics;
            }
            if (isCheckIn)
            {
                decimal? absentDays = ReadDecimal(row, ixAbsent, "Absent", key.Id, key.Name);
                decimal? attendedDays = ReadDecimal(row, ixAttended, "Attended(Actual)", key.Id, key.Name);
                decimal? leaveDays = SumDecimalCells(row, leaveColumns, key.Id, key.Name);
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
            foreach (var col in dayColumns)
            {
                ValidateMetricValue(label, Cell(row, col), key.Id, key.Name, datesByColumn[col]);
                values[col] = Cell(row, col);
            }
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
                    ReadPunch(tin, id, name, date, LabelCheckIn), ReadPunch(tout, id, name, date, LabelCheckOut), Order: key.No,
                    Attended: ReadHours(totalHours, id, name, date, LabelAttended),
                    Overtime: ReadHours(otHours, id, name, date, LabelOvertime), Status: NormalizeMetric(status)));
            }
        }
        var uniqueTotals = new List<MonthlyEmployeeTotals>();
        foreach (var employee in employeeTotals.Values.GroupBy(total => (total.Id, total.Name)))
        {
            var orderedTotals = employee.OrderBy(value => value.Order).ToArray();
            var total = orderedTotals[0];
            foreach (var repeated in orderedTotals.Skip(1))
            {
                total = total with
                {
                    AbsentDays = Merge(total.AbsentDays, repeated.AbsentDays),
                    AttendedDays = Merge(total.AttendedDays, repeated.AttendedDays),
                    LeaveDays = Merge(total.LeaveDays, repeated.LeaveDays),
                };
            }
            uniqueTotals.Add(total);

            decimal? Merge(decimal? first, decimal? other)
            {
                if (first is not null && other is not null && first != other)
                    throw new InvalidDataException($"Conflicting monthly summary totals for employee '{total.Id}' ({total.Name}).");
                return first ?? other;
            }
        }
        return (records, uniqueTotals.OrderBy(total => total.Order).ToList());
    }

    private static int FindHeader(List<string> header, Func<string, bool> match) =>
        header.FindIndex(cell => match(cell.Trim()));

    private static string NormalizeHeader(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static decimal? ReadDecimal(List<string> row, int index, string label, string id, string name)
    {
        var value = NormalizeMetric(Cell(row, index));
        if (value is null) return null;
        if (decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var number) && number >= 0) return number;
        throw new InvalidDataException($"Invalid monthly '{label}' summary total '{value}' for employee '{id}' ({name}). "
            + "Use a nonnegative number with a decimal point, such as 1.5.");
    }

    private static decimal? SumDecimalCells(List<string> row, IReadOnlyList<int> columns, string id, string name)
    {
        if (columns.Count == 0) return null;
        decimal total = 0;
        var foundValue = false;
        foreach (var column in columns)
        {
            var number = ReadDecimal(row, column, "Leave", id, name);
            if (number is null) continue;
            try
            {
                total += number.Value;
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException($"The monthly Leave summary total for employee '{id}' ({name}) is too large.", exception);
            }
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
        Regex.Replace(html, @"</tr>((?:\s|<!--[\s\S]*?-->)*)<t([dh])", "</tr>$1<tr><t$2", RegexOptions.IgnoreCase);

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
                var text = CellText(td);
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

    private static string CellText(HtmlNode cell)
    {
        var text = new StringBuilder();
        Append(cell);
        return System.Net.WebUtility.HtmlDecode(text.ToString()).Replace('\u00A0', ' ').Trim();

        void Append(HtmlNode node)
        {
            if (node.NodeType == HtmlNodeType.Text)
            {
                text.Append(node.InnerText);
                return;
            }
            if (node.NodeType == HtmlNodeType.Comment || node.Name.Equals("script", StringComparison.OrdinalIgnoreCase)
                || node.Name.Equals("style", StringComparison.OrdinalIgnoreCase)) return;
            // Nested tables are extracted independently; their cells must not also become parent-cell metadata.
            if (node.Name.Equals("table", StringComparison.OrdinalIgnoreCase)) return;
            var separatesText = node.Name.Equals("br", StringComparison.OrdinalIgnoreCase)
                || node.Name.Equals("p", StringComparison.OrdinalIgnoreCase)
                || node.Name.Equals("div", StringComparison.OrdinalIgnoreCase);
            if (separatesText) text.Append(' ');
            foreach (var child in node.ChildNodes) Append(child);
            if (separatesText && !node.Name.Equals("br", StringComparison.OrdinalIgnoreCase)) text.Append(' ');
        }
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

    private static bool IsFirstCheckIn(string value) => Same(value, "Check-in")
        || Regex.IsMatch(value.Trim(), @"^Check-in\s*1$", RegexOptions.IgnoreCase);
    private static bool IsFirstCheckOut(string value) => Same(value, "Check-out")
        || Regex.IsMatch(value.Trim(), @"^Check-out\s*1$", RegexOptions.IgnoreCase);

    private static bool IsAdditionalPunch(string value) =>
        Regex.Match(value.Trim(), @"^Check-(?:in|out)\s*(?<pair>\d+)$", RegexOptions.IgnoreCase) is { Success: true } match
        && int.TryParse(match.Groups["pair"].Value, out var pair) && pair > 1;

    private static InvalidDataException AdditionalPunchError(string id, string name, DateOnly? date = null) =>
        new($"The report contains additional Check-in/Check-out punch pairs for employee '{id}' ({name})"
            + (date is { } day ? $" on {day:dd-MM-yyyy}" : "")
            + ". Multiple shifts per day are not supported. Export a report with one punch pair per employee and date.");

    private static bool IsMetricLabel(string value) =>
        IsFirstCheckIn(value) || IsFirstCheckOut(value) || Same(value, LabelAttended)
        || IsOvertimeLabel(value) || Same(value, LabelStatus);

    private static void ValidateIdentity(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException("The attendance report has a row without a Person ID or employee Name.");
    }

    private static bool IsEmptyMetricPadding(string value) =>
        NormalizeMetric(value) is not { } metric
        || decimal.TryParse(metric, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var number) && number == 0;

    private static void ValidateMetricValue(string label, string value, string id, string name, DateOnly date)
    {
        if (IsFirstCheckIn(label) || IsFirstCheckOut(label))
            ReadPunch(value, id, name, date, label);
        else if (Same(label, LabelAttended) || IsOvertimeLabel(label))
            ReadHours(value, id, name, date, label);
    }

    private static string? ReadPunch(string? value, string id, string name, DateOnly date, string label)
    {
        if (TryNormalizePunch(value, out var punch)) return punch;
        throw new InvalidDataException($"Invalid '{label}' punch '{value}' for employee '{id}' ({name}) on {date:dd-MM-yyyy}. "
            + "Use a valid time such as 07:30, 07:30:00, or 7:30 AM, or '-' for a missing punch.");
    }

    private static string? ReadHours(string? value, string id, string name, DateOnly date, string label)
    {
        var metric = NormalizeMetric(value);
        if (metric is null) return null;
        if (AttendanceTime.TryParseHours(metric, out var hours) && hours is >= 0 and <= 24) return metric;
        throw new InvalidDataException($"Invalid '{label}' duration '{value}' for employee '{id}' ({name}) on {date:dd-MM-yyyy}. "
            + "Use hours from 0 to 24, such as 12.5 or 12:30, or '-' for a missing value.");
    }

    private static bool MetricValuesEqual(string label, string left, string right)
    {
        if (IsFirstCheckIn(label) || IsFirstCheckOut(label))
            return NormalizePunch(left) == NormalizePunch(right);
        if ((Same(label, LabelAttended) || IsOvertimeLabel(label))
            && AttendanceTime.TryParseHours(left, out var leftHours)
            && AttendanceTime.TryParseHours(right, out var rightHours)) return leftHours == rightHours;
        return string.Equals(NormalizeMetric(left), NormalizeMetric(right),
            Same(label, LabelStatus) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

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
        if (TryNormalizePunch(value, out var punch)) return punch;
        throw new InvalidDataException($"Invalid attendance punch '{value}'.");
    }

    private static bool TryNormalizePunch(string? value, out string? punch)
    {
        value = NormalizeMetric(value);
        punch = null;
        if (value is null) return true;
        value = Regex.Replace(value, @"\s+", " ");
        if (!TimeOnly.TryParseExact(value,
            ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss", "h:mm tt", "hh:mm tt", "h:mm:ss tt", "hh:mm:ss tt",
                "h:mmtt", "hh:mmtt", "h:mm:sstt", "hh:mm:sstt"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return false;
        punch = time.ToString("HH:mm", CultureInfo.InvariantCulture);
        return true;
    }

    private readonly record struct ReportRange(DateOnly From, DateOnly To);

    /// <summary>Reads the declared range from decoded cells, never from metadata or employee joining dates.</summary>
    private static ReportRange ReportRangeOf(List<List<List<string>>> tables)
    {
        // Employee fields can contain arbitrary text, including strings that resemble dates/ranges.
        // Only report title rows provide the range; employee identity and metric rows are not metadata.
        var titleRows = tables.SelectMany(table => table).Where(row =>
            !row.Any(cell => Same(cell, LabelPersonId) || Same(cell, LabelEmployeeName) || Same(cell, LabelNo))
            && !int.TryParse(Cell(row, 0), out _)
            && !IsMetricLabel(Cell(row, 0)) && !Same(Cell(row, 0), LabelDate) && !Same(Cell(row, 0), "Summary"));
        var text = string.Join(" ", titleRows.SelectMany(row => row));
        var matches = Regex.Matches(text, @"\bFrom\s*:?\s*(?<from>\S+)\s+.*?\bTo\s*:?\s*(?<to>\S+)",
            RegexOptions.IgnoreCase);
        if (matches.Count == 0)
        {
            // Some exports omit the From label but still have an explicit date-to-date title.
            matches = Regex.Matches(text,
                @"(?<![\d/.-])(?<from>\d{1,4}[-/.]\d{1,2}[-/.]\d{2,4})(?:\s+\d{1,2}:\d{2}(?::\d{2})?)?\s+To\s*:?\s*(?<to>\S+)",
                RegexOptions.IgnoreCase);
        }
        if (matches.Count == 0)
            throw new InvalidDataException("Could not find a From/To date range in the report to determine the month.");

        ReportRange? range = null;
        foreach (Match match in matches)
        {
            var fromText = match.Groups["from"].Value;
            var toText = match.Groups["to"].Value;
            if (!TryParseDate(fromText, out var from) || !TryParseDate(toText, out var to))
                throw new InvalidDataException($"Invalid report date range '{fromText}' to '{toText}'. Use day-month-year or year-month-day dates.");
            if (to < from)
                throw new InvalidDataException($"Invalid report date range: To date {to:dd-MM-yyyy} is before From date {from:dd-MM-yyyy}.");
            var current = new ReportRange(from, to);
            if (range is { } previous && previous != current)
                throw new InvalidDataException("The monthly report contains conflicting From/To date ranges. Export one consistent report range again.");
            range = current;
        }
        return range!.Value;
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
