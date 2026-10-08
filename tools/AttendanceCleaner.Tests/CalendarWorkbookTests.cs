using System.Globalization;
using AttendanceCleaner.Core;
using ClosedXML.Excel;
using Xunit;

namespace AttendanceCleaner.Tests;

public sealed class CalendarWorkbookTests
{
    public static IEnumerable<object[]> CalendarMonths()
    {
        foreach (var year in CalendarValidationRange.Years)
            foreach (var monthNumber in Enumerable.Range(1, 12))
            {
                // Explicit month lengths keep the fixture independent of the writer's date calculation.
                var days = monthNumber switch
                {
                    2 => year is 2028 or 2032 or 2036 ? 29 : 28,
                    4 or 6 or 9 or 11 => 30,
                    _ => 31,
                };
                var month = new DateOnly(year, monthNumber, 1);
                var lastDay = new DateOnly(year, monthNumber, days);
                foreach (var kind in new[] { MonthlyTemplateKind.DutyAndOvertime, MonthlyTemplateKind.InAndOut })
                    yield return
                    [
                        year, monthNumber, days, month.ToString("MMMM-yyyy", CultureInfo.InvariantCulture),
                        month.ToString("dddd", CultureInfo.InvariantCulture),
                        lastDay.ToString("dddd", CultureInfo.InvariantCulture), kind,
                    ];
            }
    }

    public static IEnumerable<object[]> CalendarYearsAndKinds()
    {
        foreach (var year in CalendarValidationRange.Years)
            foreach (var kind in new[] { MonthlyTemplateKind.DutyAndOvertime, MonthlyTemplateKind.InAndOut })
                yield return [year, kind];
    }

    public static IEnumerable<object[]> CalendarWarningYears()
    {
        foreach (var year in CalendarValidationRange.Years)
        {
            yield return [year, 0, true];
            yield return [year, year - 1, true];
            yield return [year, year, false];
            yield return [year, year + 1, true];
        }
    }

    public static IEnumerable<object[]> WorkbookWarningYears()
    {
        foreach (var row in CalendarYearsAndKinds())
            foreach (var configured in new[] { false, true })
                yield return [row[0], row[1], configured];
    }

    [Theory]
    [MemberData(nameof(CalendarMonths))]
    public void Monthly_workbooks_keep_every_calendar_day_and_the_last_days_attendance(
        int year, int monthNumber, int days, string monthTitle, string firstWeekday, string lastWeekday,
        MonthlyTemplateKind kind)
    {
        var month = new DateOnly(year, monthNumber, 1);
        var records = Enumerable.Range(1, days)
            .Select(day => Record(new DateOnly(year, monthNumber, day), status: "A"))
            .ToArray();
        records[^1] = records[^1] with
        {
            InPunch = "07:00",
            OutPunch = "19:00",
            Attended = "12",
            Status = "P",
        };
        using var workbook = WriteWorkbook(records, [], kind);
        var sheet = workbook.Worksheet(TemplateSpec.SheetName);
        var lastDayColumn = 5 + (days - 1) * 2;
        var summaryStart = lastDayColumn + 2;

        Assert.Equal($"{MonthlyTemplateSpec.CompanyName}- Employee Attendance Sheet For Month - {monthTitle}",
            sheet.Cell(1, 2).GetString());
        Assert.Equal(firstWeekday, sheet.Cell(3, 5).GetString());
        Assert.Equal(lastWeekday, sheet.Cell(3, lastDayColumn).GetString());
        Assert.Equal(days, sheet.Cell(4, lastDayColumn).GetValue<int>());
        for (var day = 1; day <= days; day++)
        {
            var column = 5 + (day - 1) * 2;
            Assert.Equal(day, sheet.Cell(4, column).GetValue<int>());
            Assert.Equal(new DateOnly(year, monthNumber, day).ToString("dddd", CultureInfo.InvariantCulture),
                sheet.Cell(3, column).GetString());
        }

        if (kind == MonthlyTemplateKind.DutyAndOvertime)
        {
            Assert.Equal("Duty", sheet.Cell(5, lastDayColumn).GetString());
            Assert.Equal("OT", sheet.Cell(5, lastDayColumn + 1).GetString());
            Assert.Equal(9m, sheet.Cell(6, lastDayColumn).GetValue<decimal>());
            Assert.Equal(3m, sheet.Cell(6, lastDayColumn + 1).GetValue<decimal>());
            Assert.Equal("No.Of.Hrs", sheet.Cell(3, summaryStart).GetString());
            Assert.Equal(9m, sheet.Cell(6, summaryStart).GetValue<decimal>());
            Assert.Equal(3m, sheet.Cell(6, summaryStart + 1).GetValue<decimal>());
        }
        else
        {
            Assert.Equal("IN", sheet.Cell(5, lastDayColumn).GetString());
            Assert.Equal("OUT", sheet.Cell(5, lastDayColumn + 1).GetString());
            Assert.Equal("07:00", sheet.Cell(6, lastDayColumn).GetString());
            Assert.Equal("19:00", sheet.Cell(6, lastDayColumn + 1).GetString());
            Assert.Equal("Total Hours", sheet.Cell(3, summaryStart).GetString());
            Assert.Equal(12m, sheet.Cell(6, summaryStart).GetValue<decimal>());
        }

        var scheduledDays = records.Count(record => !IsWeekend(record.Date));
        var lastDayWasScheduled = !IsWeekend(records[^1].Date);
        var expectedAbsent = scheduledDays - (lastDayWasScheduled ? 1 : 0);
        var expectedLeave = days - scheduledDays - (lastDayWasScheduled ? 0 : 1);
        Assert.Equal(scheduledDays, sheet.Cell(6, 4).GetValue<int>());
        Assert.Equal("No.Of.Days", sheet.Cell(3, summaryStart + 2).GetString());
        Assert.Equal(1m, sheet.Cell(6, summaryStart + 2).GetValue<decimal>());
        Assert.Equal((decimal)expectedAbsent, sheet.Cell(6, summaryStart + 3).GetValue<decimal>());
        Assert.Equal((decimal)expectedLeave, sheet.Cell(6, summaryStart + 4).GetValue<decimal>());
        Assert.Equal(days, 1 + expectedAbsent + expectedLeave);
        Assert.True(sheet.Cell(6, summaryStart + 5).IsEmpty());
        Assert.Equal(21d, sheet.Row(6).Height);
    }

    [Theory]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime)]
    [InlineData(MonthlyTemplateKind.InAndOut)]
    public void Partial_export_counts_only_reported_dates_and_leaves_other_days_blank(MonthlyTemplateKind kind)
    {
        var month = new DateOnly(2026, 10, 1);
        var records = new[]
        {
            Record(new DateOnly(2026, 10, 5), status: "A"),
            Record(new DateOnly(2026, 10, 6), "07:00", "16:00", "P"),
            Record(new DateOnly(2026, 10, 7), status: "L"),
            Record(new DateOnly(2026, 10, 10), status: "W"),
        };
        var holidays = new[]
        {
            HolidayEntry.Create(month, "Outside the export", MonthlyTemplateSpec.CorporateOfficeCategory),
            HolidayEntry.Create(new DateOnly(2026, 10, 26), "After the export", MonthlyTemplateSpec.TamilNaduCategory),
        };

        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries(records, holidays, month));
        Assert.Equal(3, summary.ScheduledWorkdays);
        Assert.Equal(1m, summary.PresentDays);
        Assert.Equal(1m, summary.AbsentDays);
        Assert.Equal(2m, summary.LeaveDays);
        Assert.Equal(9m, summary.TotalDutyHours);
        Assert.Equal(0m, summary.TotalOvertimeHours);
        Assert.Equal(9m, summary.TotalHours);

        using var workbook = WriteWorkbook(records, holidays, kind);
        var sheet = workbook.Worksheet(TemplateSpec.SheetName);
        foreach (var day in Enumerable.Range(1, 31).Except(new[] { 5, 6, 7, 10 }))
        {
            var column = 5 + (day - 1) * 2;
            Assert.True(sheet.Cell(6, column).IsEmpty(), $"Unreported day {day} must stay blank.");
            Assert.True(sheet.Cell(6, column + 1).IsEmpty(), $"Unreported day {day} must stay blank.");
        }
        Assert.Equal(3, sheet.Cell(6, 4).GetValue<int>());
        const int summaryStart = 67;
        Assert.Equal(1m, sheet.Cell(6, summaryStart + 2).GetValue<decimal>());
        Assert.Equal(1m, sheet.Cell(6, summaryStart + 3).GetValue<decimal>());
        Assert.Equal(2m, sheet.Cell(6, summaryStart + 4).GetValue<decimal>());
        Assert.Equal("Reported dates: 4 of 31 days", sheet.Cell(6, summaryStart + 5).GetString());
        Assert.True(sheet.Cell(6, summaryStart + 5).Style.Alignment.WrapText);
        Assert.True(sheet.Row(6).Height >= 34);
    }

    [Fact]
    public void Partial_aggregate_totals_reclassify_only_exported_weekends_and_holidays()
    {
        var month = new DateOnly(2026, 10, 1);
        var records = Enumerable.Range(8, 5)
            .Select(day => Record(new DateOnly(2026, 10, day)))
            .ToArray();
        records[0] = records[0] with { InPunch = "07:00", OutPunch = "16:00" };
        var holidays = new[]
        {
            HolidayEntry.Create(new DateOnly(2026, 10, 12), "Reported holiday", MonthlyTemplateSpec.CorporateOfficeCategory),
            HolidayEntry.Create(new DateOnly(2026, 10, 14), "Unreported holiday", MonthlyTemplateSpec.CorporateOfficeCategory),
        };
        var totals = new[] { new MonthlyEmployeeTotals("007", "Example Employee", 0, 4m, 1m, 0m) };

        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries(records, holidays, month, totals));

        Assert.Equal(2, summary.ScheduledWorkdays);
        Assert.Equal(2m, summary.PresentDays);
        Assert.Equal(1m, summary.AbsentDays);
        Assert.Equal(2m, summary.LeaveDays);
        Assert.Equal(18m, summary.TotalDutyHours);
        Assert.Equal(18m, summary.TotalHours);
    }

    [Theory]
    [MemberData(nameof(CalendarYearsAndKinds))]
    public void Holidays_only_credit_reported_dates_in_the_correct_year(int year, MonthlyTemplateKind kind)
    {
        var date = Enumerable.Range(1, 7).Select(day => new DateOnly(year, 1, day)).First(day => !IsWeekend(day));
        var dayColumn = 5 + (date.Day - 1) * 2;
        var records = new[] { Record(date, status: "A") };
        var previousYearHoliday = HolidayEntry.Create(new DateOnly(year - 1, 1, date.Day), "New Year", MonthlyTemplateSpec.TamilNaduCategory);
        var currentYearHoliday = HolidayEntry.Create(date, "New Year", MonthlyTemplateSpec.TamilNaduCategory);

        var previousYearSummary = Assert.Single(MonthlyTemplateWriter.BuildSummaries(records, [previousYearHoliday], date));
        Assert.Equal(1, previousYearSummary.ScheduledWorkdays);
        Assert.Equal(0m, previousYearSummary.PresentDays);
        Assert.Equal(1m, previousYearSummary.AbsentDays);
        Assert.Equal(0m, previousYearSummary.LeaveDays);
        Assert.Equal(0m, previousYearSummary.TotalHours);
        using var previousYearWorkbook = WriteWorkbook(records, [previousYearHoliday], kind);
        Assert.True(previousYearWorkbook.Worksheet(TemplateSpec.SheetName).Cell(6, dayColumn).IsEmpty());
        Assert.True(previousYearWorkbook.Worksheet(TemplateSpec.SheetName).Cell(6, dayColumn + 1).IsEmpty());

        var currentYearSummary = Assert.Single(MonthlyTemplateWriter.BuildSummaries(records, [currentYearHoliday], date));
        Assert.Equal(0, currentYearSummary.ScheduledWorkdays);
        Assert.Equal(1m, currentYearSummary.PresentDays);
        Assert.Equal(0m, currentYearSummary.AbsentDays);
        Assert.Equal(0m, currentYearSummary.LeaveDays);
        Assert.Equal(9m, currentYearSummary.TotalHours);
        using var currentYearWorkbook = WriteWorkbook(records, [currentYearHoliday], kind);
        var sheet = currentYearWorkbook.Worksheet(TemplateSpec.SheetName);
        if (kind == MonthlyTemplateKind.DutyAndOvertime)
        {
            Assert.Equal(9m, sheet.Cell(6, dayColumn).GetValue<decimal>());
            Assert.Equal(0m, sheet.Cell(6, dayColumn + 1).GetValue<decimal>());
        }
        else
        {
            Assert.Equal("07:00", sheet.Cell(6, dayColumn).GetString());
            Assert.Equal("16:00", sheet.Cell(6, dayColumn + 1).GetString());
        }
    }

    [Fact]
    public void Summaries_use_each_employees_dates_in_the_requested_month()
    {
        var month = new DateOnly(2026, 10, 1);
        var records = new[]
        {
            Record(new DateOnly(2026, 10, 5), status: "A"),
            Record(new DateOnly(2026, 10, 6), status: "A"),
            Record(new DateOnly(2026, 9, 30), "07:00", "16:00", "P"),
            Record(new DateOnly(2026, 10, 10), status: "W") with { Id = "008", Name = "Other Employee", Order = 1 },
            Record(new DateOnly(2026, 10, 11), status: "W") with { Id = "008", Name = "Other Employee", Order = 1 },
        };
        var summaries = MonthlyTemplateWriter.BuildSummaries(records, [], month);

        Assert.Equal(2, summaries.Count);
        var first = Assert.Single(summaries, summary => summary.Id == "007");
        Assert.Equal(2, first.ScheduledWorkdays);
        Assert.Equal(0m, first.PresentDays);
        Assert.Equal(2m, first.AbsentDays);
        Assert.Equal(0m, first.LeaveDays);
        Assert.Equal(0m, first.TotalHours);
        var second = Assert.Single(summaries, summary => summary.Id == "008");
        Assert.Equal(0, second.ScheduledWorkdays);
        Assert.Equal(0m, second.PresentDays);
        Assert.Equal(0m, second.AbsentDays);
        Assert.Equal(2m, second.LeaveDays);
    }

    [Fact]
    public void Working_saturday_rules_apply_only_to_reported_saturdays()
    {
        var month = new DateOnly(2026, 10, 1);
        var records = Enumerable.Range(9, 3)
            .Select(day => Record(new DateOnly(2026, 10, day), status: "A"))
            .ToArray();
        var holidays = new[]
        {
            HolidayEntry.Create(new DateOnly(2026, 10, 10), "Reported working Saturday", MonthlyTemplateSpec.WorkingSaturdayCategory),
            HolidayEntry.Create(new DateOnly(2026, 10, 17), "Unreported working Saturday", MonthlyTemplateSpec.WorkingSaturdayCategory),
        };

        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries(records, holidays, month));

        Assert.Equal(2, summary.ScheduledWorkdays);
        Assert.Equal(0m, summary.PresentDays);
        Assert.Equal(2m, summary.AbsentDays);
        Assert.Equal(1m, summary.LeaveDays);
    }

    [Theory]
    [MemberData(nameof(CalendarWarningYears))]
    public void Calendar_warning_checks_entries_for_the_reports_exact_year(int reportYear, int calendarYear, bool warningExpected)
    {
        var entries = calendarYear == 0
            ? Array.Empty<HolidayEntry>()
            : new[] { HolidayEntry.Create(new DateOnly(calendarYear, 12, 25), "Configured holiday", MonthlyTemplateSpec.TamilNaduCategory) };

        var warning = HolidayCalendarStore.GetYearWarning(entries, reportYear);

        if (warningExpected)
        {
            Assert.Contains($"configured for {reportYear}", warning);
            Assert.Contains("Manage holidays", warning);
            Assert.Contains("not been applied", warning);
        }
        else
        {
            Assert.Equal("", warning);
        }
    }

    [Theory]
    [MemberData(nameof(WorkbookWarningYears))]
    public void Workbooks_persist_missing_year_warning_and_omit_it_when_calendar_year_is_configured(
        int year, MonthlyTemplateKind kind, bool currentYearConfigured)
    {
        var records = Enumerable.Range(1, 31)
            .Select(day => Record(new DateOnly(year, 1, day), status: "A"))
            .ToArray();
        var holidays = new List<HolidayEntry>
        {
            HolidayEntry.Create(new DateOnly(year - 1, 1, 1), "Previous year", MonthlyTemplateSpec.TamilNaduCategory),
        };
        if (currentYearConfigured)
            holidays.Add(HolidayEntry.Create(new DateOnly(year, 12, 25), "Current year", MonthlyTemplateSpec.TamilNaduCategory));
        using var workbook = WriteWorkbook(records, holidays, kind);
        var holidaySheet = workbook.Worksheet(MonthlyTemplateSpec.HolidaySheetName);

        Assert.Equal($"Holiday Details · January {year}", holidaySheet.Cell(1, 1).GetString());
        Assert.Equal("Date", holidaySheet.Cell(3, 1).GetString());
        Assert.Equal("Previous year", holidaySheet.Cell(4, 2).GetString());
        if (currentYearConfigured)
        {
            Assert.True(holidaySheet.Cell(2, 1).IsEmpty());
            Assert.Equal("Current year", holidaySheet.Cell(5, 2).GetString());
        }
        else
        {
            Assert.Equal(HolidayCalendarStore.GetYearWarning(holidays, year), holidaySheet.Cell(2, 1).GetString());
            Assert.True(holidaySheet.Cell(2, 1).Style.Alignment.WrapText);
            Assert.True(holidaySheet.Row(2).Height >= 60);
            Assert.Contains(holidaySheet.MergedRanges,
                range => range.RangeAddress.FirstAddress.RowNumber == 2
                    && range.RangeAddress.FirstAddress.ColumnNumber == 1
                    && range.RangeAddress.LastAddress.RowNumber == 2
                    && range.RangeAddress.LastAddress.ColumnNumber == 3);
        }
    }

    [Theory]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime)]
    [InlineData(MonthlyTemplateKind.InAndOut)]
    public void Coverage_notes_wrap_only_partial_employees_rows(MonthlyTemplateKind kind)
    {
        var records = Enumerable.Range(1, 31)
            .Select(day => Record(new DateOnly(2026, 10, day), status: "A"))
            .Append(Record(new DateOnly(2026, 10, 5), status: "A") with { Id = "008", Name = "Partial Employee", Order = 1 })
            .ToArray();
        using var workbook = WriteWorkbook(records, [], kind);
        var sheet = workbook.Worksheet(TemplateSpec.SheetName);

        Assert.Equal(21d, sheet.Row(6).Height);
        Assert.True(sheet.Cell(6, 72).IsEmpty());
        Assert.Equal("Reported dates: 1 of 31 days", sheet.Cell(7, 72).GetString());
        Assert.True(sheet.Cell(7, 72).Style.Alignment.WrapText);
        Assert.True(sheet.Row(7).Height >= 34);
    }

    private static AttendanceRecord Record(DateOnly date, string? inPunch = null, string? outPunch = null, string? status = null) =>
        new("007", "Example Employee", "-", date, inPunch, outPunch, 0, Status: status);

    private static bool IsWeekend(DateOnly date) => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    private static XLWorkbook WriteWorkbook(IEnumerable<AttendanceRecord> records, IReadOnlyCollection<HolidayEntry> holidays,
        MonthlyTemplateKind kind)
    {
        using var stream = new MemoryStream();
        MonthlyTemplateWriter.Write(records, holidays, kind, stream);
        stream.Position = 0;
        return new XLWorkbook(stream);
    }
}
