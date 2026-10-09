using System.Globalization;
using ClosedXML.Excel;

namespace AttendanceCleaner.Core;

public enum MonthlyTemplateKind
{
    DutyAndOvertime,
    InAndOut,
}

public sealed record MonthlyEmployeeSummary(
    string Id,
    string Name,
    int Order,
    int ScheduledWorkdays,
    decimal PresentDays,
    decimal AbsentDays,
    decimal LeaveDays,
    decimal TotalDutyHours,
    decimal TotalOvertimeHours,
    decimal TotalHours,
    string? Warning = null);

public sealed record MonthlyWorkbookPreview(IReadOnlyList<MonthlySheetPreview> Sheets);

public sealed record MonthlySheetPreview(
    string Name,
    int RowCount,
    int ColumnCount,
    IReadOnlyList<double> ColumnWidths,
    IReadOnlyList<double> RowHeights,
    IReadOnlyList<MonthlyPreviewCell> Cells);

public sealed record MonthlyPreviewCell(
    int Row,
    int Column,
    int RowSpan,
    int ColumnSpan,
    string Text,
    string? FillColor,
    string? TextColor,
    bool Bold,
    double FontSize,
    string HorizontalAlignment,
    string VerticalAlignment,
    bool WrapText);

/// <summary>Builds the two monthly matrix layouts supplied by the user.</summary>
public static class MonthlyTemplateWriter
{
    private const int HeaderFirstRow = 3;
    private const int FirstDataRow = 6;
    private static readonly string[] HolidayHeaders = { "Date", "Holiday details", "Category" };

    public static IReadOnlyList<MonthlyEmployeeSummary> BuildSummaries(
        IEnumerable<AttendanceRecord> source,
        IReadOnlyCollection<HolidayEntry> holidays,
        DateOnly month,
        IReadOnlyList<MonthlyEmployeeTotals>? employeeTotals = null)
    {
        // Summary aggregates can overlap or describe a different period. Attendance is
        // always calculated from unique exported dates, daily statuses and the calendar.
        // Keep employeeTotals in the public signature for existing callers.
        holidays = HolidayCalendarStore.ValidateAndSort(holidays);
        var records = TemplateWriter.DistinctEmployeeDates(source
            .Where(record => record.Date.Year == month.Year && record.Date.Month == month.Month));
        var mandatoryHolidayDates = holidays
            .Where(entry => MonthlyTemplateSpec.IsMandatoryHoliday(entry.Category))
            .Select(entry => entry.Date)
            .Where(date => date.Year == month.Year && date.Month == month.Month)
            .ToHashSet();
        var workingSaturdayDates = holidays
            .Where(entry => MonthlyTemplateSpec.IsWorkingSaturday(entry.Category))
            .Select(entry => entry.Date)
            .Where(date => date.Year == month.Year && date.Month == month.Month)
            .ToHashSet();
        var summaries = new List<MonthlyEmployeeSummary>();
        foreach (var group in EmployeeGroups(records))
        {
            var byDate = group.Records.GroupBy(record => record.Date)
                .ToDictionary(day => day.Key, day => day.OrderBy(record => record.Order).First());
            var scheduledWorkdays = byDate.Keys
                .Count(date => IsScheduledWorkday(date, mandatoryHolidayDates, workingSaturdayDates));
            decimal present = 0;
            decimal absent = 0;
            decimal leave = 0;
            long dutyMinutes = 0;
            long overtimeMinutes = 0;
            long elapsedMinutes = 0;

            foreach (var (date, record) in byDate)
            {
                var isPresent = IsPresent(record);
                var status = record.Status?.Trim() ?? "";
                var creditHoliday = mandatoryHolidayDates.Contains(date);

                if (creditHoliday)
                {
                    present++;
                    dutyMinutes += (int)(MonthlyTemplateSpec.DutyHours * 60);
                    // Keep holiday payroll credit separate from elapsed time when punches are available.
                    elapsedMinutes += AttendanceTime.PunchDurationMinutes(record.InPunch, record.OutPunch)
                        ?? (int)(MonthlyTemplateSpec.DutyHours * 60);
                }
                else if (isPresent)
                {
                    present++;
                    var minutes = CalculateMinutes(record);
                    dutyMinutes += minutes.Duty;
                    overtimeMinutes += minutes.Overtime;
                    elapsedMinutes += TemplateWriter.WorkedMinutes(record) ?? 0;
                }
                else if (IsRegularDayOff(date, workingSaturdayDates)
                    || IsLeaveStatus(status))
                {
                    leave++;
                }
                else
                {
                    // Empty punches for an exported weekday count as absent, as do explicit A statuses.
                    // Dates outside this employee's export are not attendance observations.
                    absent++;
                }
            }

            summaries.Add(new MonthlyEmployeeSummary(
                group.Id,
                group.Name,
                group.Order,
                scheduledWorkdays,
                present,
                absent,
                leave,
                dutyMinutes / 60m,
                overtimeMinutes / 60m,
                elapsedMinutes / 60m));
        }
        return summaries;
    }

    public static void Write(
        IEnumerable<AttendanceRecord> source,
        IReadOnlyCollection<HolidayEntry> holidays,
        MonthlyTemplateKind kind,
        Stream output,
        IReadOnlyList<MonthlyEmployeeTotals>? employeeTotals = null)
    {
        var records = TemplateWriter.DistinctEmployeeDates(source);
        holidays = HolidayCalendarStore.ValidateAndSort(holidays);
        if (records.Count == 0)
            throw new InvalidDataException("The monthly report has no attendance records.");

        var month = records.Min(record => record.Date);
        if (records.Any(record => record.Date.Year != month.Year || record.Date.Month != month.Month))
            throw new InvalidDataException("Monthly templates support one calendar month per report.");

        var daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);
        var summaries = BuildSummaries(records, holidays, month, employeeTotals);
        var summaryByEmployee = summaries.ToDictionary(summary => EmployeeKey(summary.Id, summary.Name));
        var employees = EmployeeGroups(records).ToList();
        var titleColumn = 4 + daysInMonth * 2 + 6;

        using var workbook = new XLWorkbook();
        var attendance = workbook.Worksheets.Add(TemplateSpec.SheetName);
        WriteAttendanceSheet(attendance, employees, summaryByEmployee, holidays,
            month, daysInMonth, kind, titleColumn);
        WriteHolidaySheet(workbook.Worksheets.Add(MonthlyTemplateSpec.HolidaySheetName), holidays, month);
        workbook.SaveAs(output);
    }

    /// <summary>Reads display data and formatting from the exact workbook shown to the user.</summary>
    public static MonthlyWorkbookPreview ReadPreview(Stream workbookStream)
    {
        using var workbook = new XLWorkbook(workbookStream);
        var sheets = new List<MonthlySheetPreview>();
        foreach (var worksheet in workbook.Worksheets)
        {
            var used = worksheet.RangeUsed(XLCellsUsedOptions.AllContents);
            if (used is null)
                continue;

            var firstRow = used.RangeAddress.FirstAddress.RowNumber;
            var firstColumn = used.RangeAddress.FirstAddress.ColumnNumber;
            var lastRow = used.RangeAddress.LastAddress.RowNumber;
            var lastColumn = used.RangeAddress.LastAddress.ColumnNumber;
            var rowCount = lastRow - firstRow + 1;
            var columnCount = lastColumn - firstColumn + 1;
            var mergedAnchors = new Dictionary<(int Row, int Column), (int RowSpan, int ColumnSpan)>();
            var mergedCells = new HashSet<(int Row, int Column)>();

            foreach (var range in worksheet.MergedRanges)
            {
                var rangeFirstRow = range.RangeAddress.FirstAddress.RowNumber;
                var rangeFirstColumn = range.RangeAddress.FirstAddress.ColumnNumber;
                var rangeLastRow = range.RangeAddress.LastAddress.RowNumber;
                var rangeLastColumn = range.RangeAddress.LastAddress.ColumnNumber;
                mergedAnchors[(rangeFirstRow, rangeFirstColumn)] =
                    (rangeLastRow - rangeFirstRow + 1, rangeLastColumn - rangeFirstColumn + 1);
                for (var row = rangeFirstRow; row <= rangeLastRow; row++)
                    for (var column = rangeFirstColumn; column <= rangeLastColumn; column++)
                    {
                        if (row != rangeFirstRow || column != rangeFirstColumn)
                            mergedCells.Add((row, column));
                    }
            }

            var cells = new List<MonthlyPreviewCell>(rowCount * columnCount);
            for (var row = firstRow; row <= lastRow; row++)
                for (var column = firstColumn; column <= lastColumn; column++)
                {
                    if (mergedCells.Contains((row, column)))
                        continue;

                    var cell = worksheet.Cell(row, column);
                    var fillColor = cell.Style.Fill.PatternType == XLFillPatternValues.None
                        ? null
                        : ToHtmlColor(cell.Style.Fill.BackgroundColor.Color);
                    var textColor = cell.Style.Font.FontColor.Color.A == 0
                        ? null
                        : ToHtmlColor(cell.Style.Font.FontColor.Color);
                    var span = mergedAnchors.GetValueOrDefault((row, column), (RowSpan: 1, ColumnSpan: 1));
                    cells.Add(new MonthlyPreviewCell(
                        row - firstRow + 1,
                        column - firstColumn + 1,
                        span.RowSpan,
                        span.ColumnSpan,
                        cell.GetFormattedString(CultureInfo.InvariantCulture),
                        fillColor,
                        textColor,
                        cell.Style.Font.Bold,
                        cell.Style.Font.FontSize,
                        cell.Style.Alignment.Horizontal.ToString(),
                        cell.Style.Alignment.Vertical.ToString(),
                        cell.Style.Alignment.WrapText));
                }

            sheets.Add(new MonthlySheetPreview(
                worksheet.Name,
                rowCount,
                columnCount,
                Enumerable.Range(firstColumn, columnCount).Select(column => worksheet.Column(column).Width).ToArray(),
                Enumerable.Range(firstRow, rowCount).Select(row => worksheet.Row(row).Height).ToArray(),
                cells));
        }

        return new MonthlyWorkbookPreview(sheets);
    }

    private static string ToHtmlColor(System.Drawing.Color color) =>
        $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static void WriteAttendanceSheet(
        IXLWorksheet ws,
        IReadOnlyList<EmployeeGroup> employees,
        IReadOnlyDictionary<(string Id, string Name), MonthlyEmployeeSummary> summaries,
        IReadOnlyCollection<HolidayEntry> holidays,
        DateOnly month,
        int daysInMonth,
        MonthlyTemplateKind kind,
        int lastColumn)
    {
        var finalDayColumn = 4 + daysInMonth * 2;
        var summaryStart = finalDayColumn + 1;
        var title = $"{MonthlyTemplateSpec.CompanyName}- Employee Attendance Sheet For Month - {month.ToString("MMMM-yyyy", CultureInfo.InvariantCulture)}";
        ws.Range(1, 2, 1, lastColumn).Merge().Value = title;
        ws.Range(2, 1, 2, lastColumn).Merge().Value = MonthlyTemplateSpec.StaffLabel;
        AddTemplateLogo(ws);

        // Fixed employee columns. The supplied Designation column is intentionally omitted.
        var fixedHeaders = new[] { "Sl.No", "Name", "Roll.No", "Days" };
        for (var col = 1; col <= fixedHeaders.Length; col++)
        {
            ws.Range(HeaderFirstRow, col, HeaderFirstRow + 2, col).Merge().Value = fixedHeaders[col - 1];
        }

        var holidaysByDate = holidays.Where(entry => entry.Date.Year == month.Year && entry.Date.Month == month.Month)
            .GroupBy(entry => entry.Date).ToDictionary(group => group.Key, group => group.ToList());
        var mandatoryHolidayDates = holidaysByDate
            .Where(group => group.Value.Any(entry => MonthlyTemplateSpec.IsMandatoryHoliday(entry.Category)))
            .Select(group => group.Key)
            .ToHashSet();
        for (var day = 1; day <= daysInMonth; day++)
        {
            var date = new DateOnly(month.Year, month.Month, day);
            var firstCol = 5 + (day - 1) * 2;
            ws.Range(HeaderFirstRow, firstCol, HeaderFirstRow, firstCol + 1).Merge().Value =
                date.ToString("dddd", CultureInfo.InvariantCulture);
            ws.Range(HeaderFirstRow + 1, firstCol, HeaderFirstRow + 1, firstCol + 1).Merge().Value = day;
            ws.Cell(HeaderFirstRow + 2, firstCol).Value = kind == MonthlyTemplateKind.DutyAndOvertime ? "Duty" : "IN";
            ws.Cell(HeaderFirstRow + 2, firstCol + 1).Value = kind == MonthlyTemplateKind.DutyAndOvertime ? "OT" : "OUT";
            if (holidaysByDate.ContainsKey(date))
            {
                for (var headerRow = HeaderFirstRow; headerRow <= HeaderFirstRow + 2; headerRow++)
                    ws.Range(headerRow, firstCol, headerRow, firstCol + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#FDE68A");
            }
        }

        if (kind == MonthlyTemplateKind.DutyAndOvertime)
        {
            var hoursGroup = ws.Range(HeaderFirstRow, summaryStart, HeaderFirstRow, summaryStart + 1);
            hoursGroup.Merge().Value = "No.Of.Hrs";
            ws.Cell(HeaderFirstRow + 1, summaryStart).Value = MonthlyTemplateSpec.DutyLabel;
            ws.Cell(HeaderFirstRow + 1, summaryStart + 1).Value = MonthlyTemplateSpec.OvertimeLabel;
            ws.Range(HeaderFirstRow + 1, summaryStart, HeaderFirstRow + 2, summaryStart).Merge();
            ws.Range(HeaderFirstRow + 1, summaryStart + 1, HeaderFirstRow + 2, summaryStart + 1).Merge();
            WriteDayCountHeaders(ws, summaryStart + 2);
        }
        else
        {
            ws.Range(HeaderFirstRow, summaryStart, HeaderFirstRow + 2, summaryStart + 1).Merge().Value = "Total Hours";
            WriteDayCountHeaders(ws, summaryStart + 2);
        }

        var remarksColumn = lastColumn;
        ws.Range(HeaderFirstRow, remarksColumn, HeaderFirstRow + 2, remarksColumn).Merge().Value = "Remarks";

        var rows = employees.Select(group =>
        {
            var map = group.Records.GroupBy(record => record.Date)
                .ToDictionary(day => day.Key, day => day.OrderBy(record => record.Order).First());
            return (Group: group, ByDate: map, Summary: summaries[EmployeeKey(group.Id, group.Name)]);
        }).ToList();

        for (var index = 0; index < rows.Count; index++)
        {
            var rowNumber = FirstDataRow + index;
            var (employee, byDate, summary) = rows[index];
            ws.Cell(rowNumber, 1).Value = index + 1;
            ws.Cell(rowNumber, 3).Value = SpreadsheetValues.EmployeeId(employee.Id);
            ws.Cell(rowNumber, 2).Value = employee.Name;
            ws.Cell(rowNumber, 4).Value = summary.ScheduledWorkdays;
            var remarks = new List<string>();
            if (byDate.Count < daysInMonth)
                remarks.Add($"Reported dates: {byDate.Count} of {daysInMonth} days");
            if (summary.Warning is not null) remarks.Add(summary.Warning);
            ws.Cell(rowNumber, remarksColumn).Value = string.Join("\n", remarks);

            for (var day = 1; day <= daysInMonth; day++)
            {
                var date = new DateOnly(month.Year, month.Month, day);
                var firstCol = 5 + (day - 1) * 2;
                if (!byDate.TryGetValue(date, out var record))
                    continue;
                var creditHoliday = mandatoryHolidayDates.Contains(date);
                if (creditHoliday && kind == MonthlyTemplateKind.DutyAndOvertime)
                {
                    ws.Cell(rowNumber, firstCol).Value = MonthlyTemplateSpec.DutyHours;
                    ws.Cell(rowNumber, firstCol + 1).Value = 0m;
                }
                else if (creditHoliday && AttendanceTime.PunchDurationMinutes(record.InPunch, record.OutPunch) is null)
                {
                    ws.Cell(rowNumber, firstCol).Value = MonthlyTemplateSpec.DutyStart.ToString(TemplateSpec.TimeFormat, CultureInfo.InvariantCulture);
                    ws.Cell(rowNumber, firstCol + 1).Value = MonthlyTemplateSpec.DutyEnd.ToString(TemplateSpec.TimeFormat, CultureInfo.InvariantCulture);
                }
                else if (kind == MonthlyTemplateKind.DutyAndOvertime)
                {
                    if (IsPresent(record))
                    {
                        var minutes = CalculateMinutes(record);
                        ws.Cell(rowNumber, firstCol).Value = minutes.Duty / 60m;
                        ws.Cell(rowNumber, firstCol + 1).Value = minutes.Overtime / 60m;
                    }
                }
                else
                {
                    if (record.InPunch is not null) ws.Cell(rowNumber, firstCol).Value = record.InPunch;
                    if (record.OutPunch is not null) ws.Cell(rowNumber, firstCol + 1).Value = record.OutPunch;
                }
            }

            var groupStart = summaryStart + 2;
            if (kind == MonthlyTemplateKind.DutyAndOvertime)
            {
                ws.Cell(rowNumber, summaryStart).Value = summary.TotalDutyHours;
                ws.Cell(rowNumber, summaryStart + 1).Value = summary.TotalOvertimeHours;
            }
            else
            {
                ws.Cell(rowNumber, summaryStart).Value = summary.TotalHours;
            }
            ws.Cell(rowNumber, groupStart).Value = summary.PresentDays;
            ws.Cell(rowNumber, groupStart + 1).Value = summary.AbsentDays;
            ws.Cell(rowNumber, groupStart + 2).Value = summary.LeaveDays;
        }

        ApplyAttendanceStyles(ws, employees.Count, daysInMonth, summaryStart, lastColumn, kind);
    }

    private static void WriteDayCountHeaders(IXLWorksheet ws, int startColumn)
    {
        ws.Range(HeaderFirstRow, startColumn, HeaderFirstRow + 1, startColumn + 2).Merge().Value = "No.Of.Days";
        ws.Cell(HeaderFirstRow + 2, startColumn).Value = "Present";
        ws.Cell(HeaderFirstRow + 2, startColumn + 1).Value = "Absent/LOP";
        ws.Cell(HeaderFirstRow + 2, startColumn + 2).Value = "Leave (W/O)";
    }

    private static void ApplyAttendanceStyles(
        IXLWorksheet ws,
        int employeeCount,
        int daysInMonth,
        int summaryStart,
        int lastColumn,
        MonthlyTemplateKind kind)
    {
        var lastRow = Math.Max(FirstDataRow, FirstDataRow + employeeCount - 1);
        const string titleYellow = "#FFFF00";
        const string paleYellow = "#FFF2CC";
        const string paleGreen = "#E2EFDA";
        const string paleBlue = "#DDEBF7";
        const string paleOrange = "#FCE4D6";
        const string sectionBlue = "#B4C7E7";

        var titleRange = ws.Range(1, 2, 1, lastColumn);
        titleRange.Style.Fill.BackgroundColor = XLColor.FromHtml(titleYellow);
        titleRange.Style.Font.FontColor = XLColor.Black;
        titleRange.Style.Font.Bold = true;
        titleRange.Style.Font.FontSize = 12;
        titleRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        titleRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Cell(1, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#E2EFDA");
        ws.Row(1).Height = 30;

        var subTitle = ws.Range(2, 1, 2, lastColumn);
        subTitle.Style.Fill.BackgroundColor = XLColor.FromHtml(sectionBlue);
        subTitle.Style.Font.FontColor = XLColor.Black;
        subTitle.Style.Font.Bold = true;
        subTitle.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        ws.Row(2).Height = 20;

        var headers = ws.Range(HeaderFirstRow, 1, HeaderFirstRow + 2, lastColumn);
        headers.Style.Fill.BackgroundColor = XLColor.White;
        headers.Style.Font.FontColor = XLColor.Black;
        headers.Style.Font.Bold = true;
        headers.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        headers.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        headers.Style.Alignment.WrapText = true;
        headers.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        headers.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
        ws.Range(HeaderFirstRow, 1, HeaderFirstRow + 2, 4).Style.Fill.BackgroundColor = XLColor.FromHtml(paleYellow);
        ws.Range(HeaderFirstRow, 5, HeaderFirstRow, summaryStart - 1).Style.Fill.BackgroundColor = XLColor.FromHtml(paleGreen);
        ws.Range(HeaderFirstRow + 1, 5, HeaderFirstRow + 1, summaryStart - 1).Style.Fill.BackgroundColor = XLColor.FromHtml(paleBlue);
        ws.Rows(HeaderFirstRow, HeaderFirstRow + 2).Height = 20;

        for (var day = 0; day < daysInMonth; day++)
        {
            var firstCol = 5 + day * 2;
            ws.Cell(HeaderFirstRow + 2, firstCol).Style.Fill.BackgroundColor = XLColor.FromHtml(paleYellow);
            ws.Cell(HeaderFirstRow + 2, firstCol + 1).Style.Fill.BackgroundColor = XLColor.FromHtml(paleOrange);
        }

        if (kind == MonthlyTemplateKind.DutyAndOvertime)
        {
            ws.Range(HeaderFirstRow, summaryStart, HeaderFirstRow + 1, summaryStart).Style.Fill.BackgroundColor = XLColor.FromHtml(paleYellow);
            ws.Range(HeaderFirstRow, summaryStart + 1, HeaderFirstRow + 1, summaryStart + 1).Style.Fill.BackgroundColor = XLColor.FromHtml(paleOrange);
        }
        else
        {
            ws.Range(HeaderFirstRow, summaryStart, HeaderFirstRow + 2, summaryStart + 1).Style.Fill.BackgroundColor = XLColor.FromHtml(paleYellow);
        }
        ws.Range(HeaderFirstRow, summaryStart + 2, HeaderFirstRow + 1, summaryStart + 4).Style.Fill.BackgroundColor = XLColor.FromHtml(paleGreen);
        ws.Range(HeaderFirstRow + 2, summaryStart + 2, HeaderFirstRow + 2, summaryStart + 2).Style.Fill.BackgroundColor = XLColor.FromHtml(paleGreen);
        ws.Range(HeaderFirstRow + 2, summaryStart + 3, HeaderFirstRow + 2, summaryStart + 3).Style.Fill.BackgroundColor = XLColor.FromHtml(sectionBlue);
        ws.Range(HeaderFirstRow + 2, summaryStart + 4, HeaderFirstRow + 2, summaryStart + 4).Style.Fill.BackgroundColor = XLColor.FromHtml("#D9D9D9");

        var data = ws.Range(FirstDataRow, 1, lastRow, lastColumn);
        data.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        data.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        data.Style.Border.InsideBorder = XLBorderStyleValues.Hair;
        for (var day = 0; day < daysInMonth; day++)
        {
            var firstCol = 5 + day * 2;
            ws.Range(FirstDataRow, firstCol, lastRow, firstCol).Style.Fill.BackgroundColor = XLColor.FromHtml(kind == MonthlyTemplateKind.DutyAndOvertime ? paleYellow : paleGreen);
            ws.Range(FirstDataRow, firstCol + 1, lastRow, firstCol + 1).Style.Fill.BackgroundColor = XLColor.FromHtml(kind == MonthlyTemplateKind.DutyAndOvertime ? paleOrange : paleBlue);
        }
        ws.Range(FirstDataRow, summaryStart, lastRow, summaryStart).Style.Fill.BackgroundColor = XLColor.FromHtml(paleYellow);
        if (kind == MonthlyTemplateKind.DutyAndOvertime)
            ws.Range(FirstDataRow, summaryStart + 1, lastRow, summaryStart + 1).Style.Fill.BackgroundColor = XLColor.FromHtml(paleOrange);
        ws.Range(FirstDataRow, summaryStart + 2, lastRow, summaryStart + 2).Style.Fill.BackgroundColor = XLColor.FromHtml(paleGreen);
        ws.Range(FirstDataRow, summaryStart + 3, lastRow, summaryStart + 3).Style.Fill.BackgroundColor = XLColor.FromHtml(sectionBlue);
        ws.Range(FirstDataRow, summaryStart + 4, lastRow, summaryStart + 4).Style.Fill.BackgroundColor = XLColor.FromHtml("#D9D9D9");

        ws.Column(1).Width = 7;
        ws.Column(2).Width = 26;
        ws.Column(3).Width = 12;
        ws.Column(4).Width = 8;
        for (var col = 5; col < summaryStart; col++) ws.Column(col).Width = 8;
        var summaryWidth = summaryStart + 5;
        for (var col = summaryStart; col <= summaryWidth; col++) ws.Column(col).Width = 12;
        ws.Column(lastColumn).Width = 20;
        if (employeeCount > 0) ws.Rows(FirstDataRow, lastRow).Height = 21;
        ws.Range(FirstDataRow, 5, lastRow, summaryStart - 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Range(FirstDataRow, summaryStart, lastRow, summaryWidth).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        var remarks = ws.Range(FirstDataRow, lastColumn, lastRow, lastColumn);
        remarks.Style.Alignment.WrapText = true;
        remarks.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        for (var row = FirstDataRow; row <= lastRow; row++)
        {
            var text = ws.Cell(row, lastColumn).GetString();
            if (text.Length == 0) continue;
            var wrappedLines = text.Split('\n').Sum(line => Math.Max(1, (int)Math.Ceiling(line.Length / 20d)));
            ws.Row(row).Height = Math.Max(34, wrappedLines * 12);
        }

        if (kind == MonthlyTemplateKind.DutyAndOvertime)
        {
            ws.Range(FirstDataRow, 5, lastRow, summaryStart + 1).Style.NumberFormat.Format = "0.00";
        }
        else
        {
            ws.Range(FirstDataRow, summaryStart, lastRow, summaryStart).Style.NumberFormat.Format = "0.00";
            for (var day = 0; day < daysInMonth; day++)
            {
                ws.Range(FirstDataRow, 5 + day * 2, lastRow, 6 + day * 2).Style.NumberFormat.Format = "@";
            }
        }
        ws.SheetView.FreezeRows(5);
        ws.SheetView.FreezeColumns(4);
    }

    private static void AddTemplateLogo(IXLWorksheet ws)
    {
        using var logo = typeof(MonthlyTemplateWriter).Assembly
            .GetManifestResourceStream("AttendanceCleaner.Core.DakshinakTemplateLogo.png");
        if (logo is null) return;
        ws.AddPicture(logo).MoveTo(ws.Cell(1, 1)).WithSize(47, 30);
    }

    private static void WriteHolidaySheet(IXLWorksheet ws, IReadOnlyCollection<HolidayEntry> holidays, DateOnly month)
    {
        var entries = HolidayCalendarStore.Sort(holidays);
        ws.Range(1, 1, 1, 3).Merge().Value = $"{MonthlyTemplateSpec.HolidaySheetName} · {month.ToString("MMMM yyyy", CultureInfo.InvariantCulture)}";
        var calendarWarning = HolidayCalendarStore.GetYearWarning(holidays, month.Year);
        if (calendarWarning.Length > 0)
        {
            var warningRange = ws.Range(2, 1, 2, 3).Merge();
            warningRange.Value = calendarWarning;
            warningRange.Style.Alignment.WrapText = true;
            warningRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#FCE4D6");
            ws.Row(2).Height = 60;
        }
        for (var col = 0; col < HolidayHeaders.Length; col++) ws.Cell(3, col + 1).Value = HolidayHeaders[col];
        for (var index = 0; index < entries.Count; index++)
        {
            var row = index + 4;
            ws.Cell(row, 1).Value = entries[index].Date.ToDateTime(TimeOnly.MinValue);
            ws.Cell(row, 1).Style.DateFormat.Format = "dd-mm-yyyy";
            ws.Cell(row, 2).Value = entries[index].Name;
            ws.Cell(row, 3).Value = entries[index].Category;
        }

        var title = ws.Range(1, 1, 1, 3);
        title.Style.Fill.BackgroundColor = XLColor.FromHtml("#164E63");
        title.Style.Font.FontColor = XLColor.White;
        title.Style.Font.Bold = true;
        title.Style.Font.FontSize = 15;
        title.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        title.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Row(1).Height = 30;
        var header = ws.Range(3, 1, 3, 3);
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#0F766E");
        header.Style.Font.FontColor = XLColor.White;
        header.Style.Font.Bold = true;
        header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Row(3).Height = 24;
        var lastRow = Math.Max(3, entries.Count + 3);
        ws.Range(3, 1, lastRow, 3).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        ws.Range(3, 1, lastRow, 3).Style.Border.InsideBorder = XLBorderStyleValues.Hair;
        ws.Column(1).Width = 16;
        ws.Column(2).Width = 36;
        ws.Column(3).Width = 34;
        if (entries.Count > 0)
        {
            var details = ws.Range(4, 1, lastRow, 3);
            details.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            ws.Range(4, 2, lastRow, 3).Style.Alignment.WrapText = true;
            var graphics = ClosedXML.Graphics.DefaultGraphicEngine.Instance.Value;
            var digitWidth = graphics.GetMaxDigitWidth(ws.Workbook.Style.Font, 96);
            for (var row = 4; row <= lastRow; row++)
            {
                var height = 24d;
                for (var column = 2; column <= 3; column++)
                {
                    var cell = ws.Cell(row, column);
                    // Excel does not auto-size wrapped rows when opening a generated workbook.
                    // Measure the preserved text at the stored width, including spaces and newlines.
                    var availableWidth = Math.Max(1, ws.Column(column).Width * digitWidth - 10);
                    var lineCount = 0;
                    foreach (var paragraph in cell.GetString().Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                    {
                        var lineWidth = 0d;
                        lineCount++;
                        foreach (System.Text.RegularExpressions.Match token in
                            System.Text.RegularExpressions.Regex.Matches(paragraph, @"\s+|\S+"))
                        {
                            var word = token.Value;
                            var whitespace = char.IsWhiteSpace(word[0]);
                            var wordWidth = whitespace ? 0 : graphics.GetTextWidth(word, cell.Style.Font, 96);
                            if (!whitespace && lineWidth > 0 && lineWidth + wordWidth > availableWidth)
                            {
                                lineCount++;
                                lineWidth = 0;
                            }
                            if (!whitespace && wordWidth <= availableWidth)
                            {
                                lineWidth += wordWidth;
                                continue;
                            }

                            // Wrap whitespace runs and long words without splitting Unicode text elements.
                            var elements = StringInfo.GetTextElementEnumerator(word);
                            while (elements.MoveNext())
                            {
                                var element = elements.GetTextElement();
                                var elementWidth = graphics.GetTextWidth(element == "\t" ? "        " : element,
                                    cell.Style.Font, 96);
                                if (lineWidth > 0 && lineWidth + elementWidth > availableWidth)
                                {
                                    lineCount++;
                                    lineWidth = 0;
                                }
                                lineWidth += elementWidth;
                            }
                        }
                    }
                    var lineHeight = Math.Max(cell.Style.Font.FontSize * 1.35,
                        graphics.GetTextHeight(cell.Style.Font, 96) * 72 / 96);
                    height = Math.Max(height, lineCount * lineHeight + 8);
                }
                ws.Row(row).Height = Math.Min(409.5, Math.Ceiling(height));
            }
        }
        ws.Range(3, 1, lastRow, 3).SetAutoFilter();
        ws.SheetView.FreezeRows(3);
    }

    private static IReadOnlyList<EmployeeGroup> EmployeeGroups(IEnumerable<AttendanceRecord> records) =>
        records.GroupBy(record => EmployeeKey(record.Id, record.Name))
            .Select(group => new EmployeeGroup(
                group.First().Id,
                group.First().Name,
                group.Min(record => record.Order),
                group.ToList()))
            .OrderBy(group => group.Order)
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static (string Id, string Name) EmployeeKey(string id, string name) => (id, name);

    private static bool IsPresent(AttendanceRecord? record)
    {
        if (record is null) return false;
        if (record.InPunch is not null || record.OutPunch is not null) return true;
        if (record.Status?.Trim().StartsWith("P", StringComparison.OrdinalIgnoreCase) == true) return true;
        return AttendanceTime.TryParseHours(record.Attended, out var attended) && attended > 0;
    }

    private static bool IsLeaveStatus(string status) =>
        status.StartsWith("W", StringComparison.OrdinalIgnoreCase)
        || status.StartsWith("L", StringComparison.OrdinalIgnoreCase)
        || status.StartsWith("H", StringComparison.OrdinalIgnoreCase);

    private static bool IsRegularDayOff(DateOnly date, IReadOnlySet<DateOnly> workingSaturdayDates) =>
        date.DayOfWeek == DayOfWeek.Sunday
        || (date.DayOfWeek == DayOfWeek.Saturday && !workingSaturdayDates.Contains(date));

    private static bool IsScheduledWorkday(
        DateOnly date,
        IReadOnlySet<DateOnly> mandatoryHolidayDates,
        IReadOnlySet<DateOnly> workingSaturdayDates) =>
        workingSaturdayDates.Contains(date)
        || (!mandatoryHolidayDates.Contains(date) && !IsRegularDayOff(date, workingSaturdayDates));

    private static (int Duty, int Overtime) CalculateMinutes(AttendanceRecord record)
    {
        var total = TemplateWriter.WorkedMinutes(record) ?? 0;
        var dutyLimit = (int)(MonthlyTemplateSpec.DutyHours * 60);
        var overtimeLimit = (int)(MonthlyTemplateSpec.MaximumOvertimeHours * 60);
        var reportedOvertime = AttendanceTime.TryParseWorkedMinutes(record.Overtime, out var reported) ? reported : 0;
        var punchOvertime = GetPunchWindowMinutes(record, MonthlyTemplateSpec.DutyEnd, MonthlyTemplateSpec.OvertimeEnd);
        var overtime = Math.Min(total, Math.Min(overtimeLimit,
            reportedOvertime > 0 ? reportedOvertime : punchOvertime));
        if (overtime == 0 && total > dutyLimit) overtime = Math.Min(overtimeLimit, total - dutyLimit);
        var duty = Math.Min(dutyLimit, Math.Max(0, total - overtime));
        return (duty, overtime);
    }

    private static int GetPunchWindowMinutes(AttendanceRecord record, TimeOnly fromTime, TimeOnly toTime)
    {
        if (!AttendanceTime.TryParsePunch(record.InPunch, out var inTime) || !AttendanceTime.TryParsePunch(record.OutPunch, out var outTime)) return 0;
        var start = inTime.Hour * 60 + inTime.Minute;
        var end = outTime.Hour * 60 + outTime.Minute;
        if (end < start) end += 24 * 60;
        var windowStart = fromTime.Hour * 60 + fromTime.Minute;
        var windowEnd = toTime.Hour * 60 + toTime.Minute;
        var overlapStart = start > windowStart ? start : windowStart;
        var overlapEnd = end < windowEnd ? end : windowEnd;
        return Math.Max(0, overlapEnd - overlapStart);
    }

    private sealed record EmployeeGroup(string Id, string Name, int Order, List<AttendanceRecord> Records);
}
