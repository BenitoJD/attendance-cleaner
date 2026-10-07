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

    public static ParsedReport Parse(string html)
    {
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
            return new ParsedReport(AttendanceExportFormat.MonthlyRows, ParseMonthlyRows(tables, monthly.header, monthly.dayColumns, html));
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
                Order: order++));
        }
        return records;
    }

    // --- Monthly block report: one block of rows per employee, days as columns ---

    private static List<AttendanceRecord> ParseMonthlyBlocks(List<List<List<string>>> tables, string html)
    {
        (int Year, int Month) = MonthOf(html);
        var records = new List<AttendanceRecord>();

        int blockIndex = -1;
        Dictionary<int, int>? dayByColumn = null;
        Dictionary<int, string>? ins = null, outs = null;
        string id = "", name = "";

        void Flush()
        {
            if (dayByColumn == null) return;
            foreach (var (col, day) in dayByColumn.OrderBy(kv => kv.Value))
            {
                if (!TryMakeDate(Year, Month, day, out var date)) continue;
                ins!.TryGetValue(col, out var tin);
                outs!.TryGetValue(col, out var tout);
                records.Add(new AttendanceRecord(id, name, TemplateSpec.UnknownGender, date,
                    NormalizePunch(tin), NormalizePunch(tout), Order: blockIndex));
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
            else if (Same(label, LabelStatus) && dayByColumn != null)
            {
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

    private static (List<string> header, List<int> dayColumns)? FindMonthlyRowsHeader(List<List<List<string>>> tables)
    {
        foreach (var table in tables)
        foreach (var row in table)
        {
            if (!Same(Cell(row, 0), LabelNo) || !row.Any(c => Same(c, LabelPersonId))) continue;
            var dayColumns = new List<int>();
            for (int c = 0; c < row.Count; c++)
            {
                if (int.TryParse(Cell(row, c), out var d) && d is >= 1 and <= 31)
                {
                    dayColumns.Add(c);
                }
            }
            if (dayColumns.Count >= 28) return (row, dayColumns);
        }
        return null;
    }

    private static List<AttendanceRecord> ParseMonthlyRows(
        List<List<List<string>>> tables, List<string> header, List<int> dayColumns, string html)
    {
        (int Year, int Month) = MonthOf(html);
        int ixNo = FindIndex(header, LabelNo);
        int ixId = FindIndex(header, LabelPersonId);
        int ixName = FindIndex(header, LabelName);
        int labelColumn = ixName + 1;

        var rows = tables.SelectMany(t => t)
            .Where(r => r.Count > labelColumn && r.Count > dayColumns.Max())
            .ToList();

        // metric rows repeat (No, ID, Name) on every row of the employee's block
        var blocks = new Dictionary<string, Dictionary<string, Dictionary<int, string>>>();
        var order = new Dictionary<string, int>();
        foreach (var row in rows)
        {
            var label = Cell(row, labelColumn);
            var isCheckIn = Same(label, LabelCheckIn);
            var isCheckOut = Same(label, LabelCheckOut);
            if (!isCheckIn && !isCheckOut) continue;
            if (!int.TryParse(Cell(row, ixNo), out var no)) continue;

            var key = $"{Cell(row, ixNo)}|{Cell(row, ixId)}|{Cell(row, ixName)}";
            if (!blocks.TryGetValue(key, out var metrics))
            {
                metrics = new Dictionary<string, Dictionary<int, string>>();
                blocks[key] = metrics;
                order[key] = no;
            }
            var values = new Dictionary<int, string>();
            foreach (var col in dayColumns) values[col] = Cell(row, col);
            metrics[isCheckIn ? LabelCheckIn : LabelCheckOut] = values;
        }

        var records = new List<AttendanceRecord>();
        foreach (var (key, metrics) in blocks)
        {
            var parts = key.Split('|');
            string id = parts[1], name = parts[2];
            var ins = metrics.GetValueOrDefault(LabelCheckIn) ?? new Dictionary<int, string>();
            var outs = metrics.GetValueOrDefault(LabelCheckOut) ?? new Dictionary<int, string>();
            foreach (var col in dayColumns)
            {
                var day = int.Parse(Cell(header, col));
                if (!TryMakeDate(Year, Month, day, out var date)) continue;
                ins.TryGetValue(col, out var tin);
                outs.TryGetValue(col, out var tout);
                records.Add(new AttendanceRecord(id, name, TemplateSpec.UnknownGender, date,
                    NormalizePunch(tin), NormalizePunch(tout), Order: order[key]));
            }
        }
        return records;
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
                var text = System.Net.WebUtility.HtmlDecode(td.InnerText).Trim();
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

    private static string? NormalizePunch(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value) || value == "-") return null;
        if (Regex.IsMatch(value, @"^\d{1,2}:\d{2}:\d{2}$")) value = value[..value.LastIndexOf(':')];
        return Regex.IsMatch(value, @"^\d{1,2}:\d{2}$") ? value : null;
    }

    private static (int Year, int Month) MonthOf(string html)
    {
        var match = Regex.Match(html, @"\b(\d{2})-(\d{2})-(\d{4})\b");
        if (match.Success)
        {
            return (int.Parse(match.Groups[3].Value), int.Parse(match.Groups[2].Value));
        }
        throw new InvalidDataException("Could not find a From/To date in the report to determine the month.");
    }

    private static bool TryParseDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, TemplateSpec.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

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
}
