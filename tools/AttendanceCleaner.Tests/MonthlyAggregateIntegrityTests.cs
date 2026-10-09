using AttendanceCleaner.Core;
using ClosedXML.Excel;
using Xunit;

namespace AttendanceCleaner.Tests;

public sealed class MonthlyAggregateIntegrityTests
{
    private static readonly DateOnly Month = new(2026, 9, 1);

    [Fact]
    public void Overlapping_source_totals_cannot_count_a_partial_punch_day_twice()
    {
        var records = new[] { Record(1), Record(2) with { OutPunch = null } };
        var sourceTotals = new MonthlyEmployeeTotals("007", "Example", 0, 1m, 2m, 0m);
        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries(records, [], Month, [sourceTotals]));

        Assert.Equal(2m, summary.PresentDays);
        Assert.Equal(0m, summary.AbsentDays);
        Assert.Equal(0m, summary.LeaveDays);
        Assert.NotNull(summary.Warning);
        Assert.Equal(1m, sourceTotals.AbsentDays);
    }

    [Fact]
    public void Partial_exports_ignore_full_month_aggregate_counts_and_keep_holiday_credit()
    {
        var records = Enumerable.Range(5, 16).Select(day => Record(day) with
        {
            InPunch = day <= 12 ? "07:00" : null,
            OutPunch = day <= 12 ? "16:00" : null,
        }).ToArray();
        var holiday = HolidayEntry.Create(Month.AddDays(13), "Example holiday", MonthlyTemplateSpec.CorporateOfficeCategory);
        var totals = new MonthlyEmployeeTotals("007", "Example", 0, 10m, 20m, 0m);
        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries(records, [holiday], Month, [totals]));

        Assert.Equal(9m, summary.PresentDays);
        Assert.Equal(16m, summary.PresentDays + summary.AbsentDays + summary.LeaveDays);
        Assert.NotNull(summary.Warning);
        Assert.Equal(81m, summary.TotalDutyHours);
    }

    [Fact]
    public void Coherent_fractional_source_counts_are_preserved_with_weekly_off_reclassification()
    {
        var records = Enumerable.Range(1, 6).Select(day => Record(day) with { InPunch = null, OutPunch = null }).ToArray();
        var totals = new MonthlyEmployeeTotals("007", "Example", 0, 5.5m, 0m, 0.5m);
        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries(records, [], Month, [totals]));

        Assert.Equal(0m, summary.PresentDays);
        Assert.Equal(3.5m, summary.AbsentDays);
        Assert.Equal(2.5m, summary.LeaveDays);
        Assert.Null(summary.Warning);
    }

    [Fact]
    public void Negative_overflowing_and_incomplete_public_aggregates_fall_back_without_losing_dates()
    {
        var records = new[] { Record(1) with { InPunch = null, OutPunch = null }, Record(2) };
        var invalidTotals = new[]
        {
            new MonthlyEmployeeTotals("007", "Example", 0, decimal.MaxValue, decimal.MaxValue, decimal.MaxValue),
            new MonthlyEmployeeTotals("007", "Example", 0, -1m, 1m, 0m),
            new MonthlyEmployeeTotals("007", "Example", 0, 0m, 0m, 0m),
        };
        foreach (var totals in invalidTotals)
        {
            var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries(records, [], Month, [totals]));
            Assert.Equal(1m, summary.PresentDays);
            Assert.Equal(1m, summary.AbsentDays);
            Assert.Equal(0m, summary.LeaveDays);
            Assert.NotNull(summary.Warning);
        }
    }

    [Fact]
    public void Daily_status_codes_take_precedence_over_aggregate_counts()
    {
        var records = new[] { Record(1) with { Status = "P" }, Record(2) with { InPunch = null, OutPunch = null, Status = "A" } };
        var totals = new MonthlyEmployeeTotals("007", "Example", 0, 2m, 0m, 0m);
        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries(records, [], Month, [totals]));

        Assert.Equal(1m, summary.PresentDays);
        Assert.Equal(1m, summary.AbsentDays);
        Assert.Null(summary.Warning);
    }

    [Theory]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime)]
    [InlineData(MonthlyTemplateKind.InAndOut)]
    public void Workbook_and_preview_show_recalculation_warning_and_partial_export_note(MonthlyTemplateKind kind)
    {
        var records = new[] { Record(1), Record(2) with { OutPunch = null } };
        var totals = new MonthlyEmployeeTotals("007", "Example", 0, 1m, 2m, 0m);
        using var stream = new MemoryStream();
        MonthlyTemplateWriter.Write(records, [], kind, stream, [totals]);
        stream.Position = 0;
        using (var workbook = new XLWorkbook(stream))
        {
            var sheet = workbook.Worksheet(TemplateSpec.SheetName);
            var summaryColumn = 5 + DateTime.DaysInMonth(Month.Year, Month.Month) * 2;
            Assert.Equal(2m, sheet.Cell(6, summaryColumn + 2).GetValue<decimal>());
            Assert.Equal(0m, sheet.Cell(6, summaryColumn + 3).GetValue<decimal>());
            Assert.Equal(0m, sheet.Cell(6, summaryColumn + 4).GetValue<decimal>());
            var remarks = sheet.Cell(6, summaryColumn + 5);
            Assert.Contains("Reported dates: 2 of 30 days", remarks.GetString());
            Assert.Contains("Source totals inconsistent", remarks.GetString());
            Assert.True(remarks.Style.Alignment.WrapText);
            Assert.True(sheet.Row(6).Height >= 60);
        }
        stream.Position = 0;
        var preview = MonthlyTemplateWriter.ReadPreview(stream);
        Assert.Contains(preview.Sheets[0].Cells, cell => cell.Text.Contains("Source totals inconsistent", StringComparison.Ordinal));
    }

    private static AttendanceRecord Record(int day) => new("007", "Example", "", Month.AddDays(day - 1), "07:00", "16:00", 0);
}
