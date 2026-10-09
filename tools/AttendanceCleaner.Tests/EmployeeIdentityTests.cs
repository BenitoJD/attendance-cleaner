using AttendanceCleaner.Core;
using ClosedXML.Excel;
using Xunit;

namespace AttendanceCleaner.Tests;

public sealed class EmployeeIdentityTests
{
    private static readonly DateOnly Date = new(2026, 9, 1);
    private static AttendanceRecord Record(string id = "007", string name = "Example Employee") =>
        new(id, name, "Male", Date, "07:00", "16:00", 0);

    [Theory]
    [InlineData("EXAMPLE EMPLOYEE")]
    [InlineData("  Example   Employee  ")]
    [InlineData("Example\tEmployee")]
    public void Same_id_and_date_with_harmless_name_variations_counts_once_everywhere(string name)
    {
        var records = new[] { Record(), Record(name: name) with { Order = 2 } };
        Assert.Single(TemplateWriter.BuildRows(records));
        var dashboard = AttendanceAnalytics.Build(records);
        Assert.Equal(1, dashboard.EmployeeCount);
        Assert.Equal(1, dashboard.TotalRows);
        Assert.Equal(1m, Assert.Single(MonthlyTemplateWriter.BuildSummaries(records, [], Date)).PresentDays);
        Assert.Single(new DashboardSelection(new ParsedReport(AttendanceExportFormat.MonthlyRows, records)).Current.Employees);
    }

    [Fact]
    public void Name_variations_across_dates_share_one_employee_and_keep_both_days()
    {
        var records = new[] { Record(), Record(name: "EXAMPLE EMPLOYEE") with { Date = Date.AddDays(1) } };
        var dashboard = AttendanceAnalytics.Build(records);
        Assert.Equal(1, dashboard.EmployeeCount);
        Assert.Equal(2, dashboard.TotalRows);
        Assert.Equal(2, Assert.Single(dashboard.Employees).PresentDays);
        var summary = Assert.Single(MonthlyTemplateWriter.BuildSummaries(records, [], Date));
        Assert.Equal(2m, summary.PresentDays);
        Assert.Equal(18m, summary.TotalHours);
        Assert.All(TemplateWriter.BuildRows(records), row => Assert.Equal("Example Employee", row.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Different_names_for_one_id_are_flagged_instead_of_guessing(bool differentDate)
    {
        var records = new[] { Record(), Record(name: "Another Employee") with { Date = differentDate ? Date.AddDays(1) : Date } };
        foreach (var action in new Action[] {
            () => TemplateWriter.BuildRows(records),
            () => AttendanceAnalytics.Build(records),
            () => MonthlyTemplateWriter.BuildSummaries(records, [], Date) })
        {
            var error = Assert.Throws<InvalidDataException>(action);
            Assert.Contains("Conflicting employee names", error.Message);
            Assert.Contains("007", error.Message);
        }
    }

    [Fact]
    public void Renaming_cannot_bypass_conflicting_punch_detection()
    {
        var records = new[] { Record(), Record(name: "EXAMPLE EMPLOYEE") with { OutPunch = "17:00" } };
        Assert.Throws<InvalidDataException>(() => TemplateWriter.BuildRows(records));
        Assert.Throws<InvalidDataException>(() => AttendanceAnalytics.Build(records));
        Assert.Throws<InvalidDataException>(() => MonthlyTemplateWriter.BuildSummaries(records, [], Date));
    }

    [Fact]
    public void Conflicting_gender_data_is_not_silently_dropped_as_a_duplicate()
    {
        var records = new[] { Record(), Record() with { Gender = "Female" } };
        Assert.Throws<InvalidDataException>(() => TemplateWriter.BuildRows(records));
    }

    [Fact]
    public void Same_name_with_different_ids_remains_separate_including_leading_zero_ids()
    {
        var records = new[] { Record(), Record(id: "7") };
        Assert.Equal(2, TemplateWriter.BuildRows(records).Count);
        Assert.Equal(2, AttendanceAnalytics.Build(records).EmployeeCount);
        Assert.Equal(2, MonthlyTemplateWriter.BuildSummaries(records, [], Date).Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime)]
    [InlineData(MonthlyTemplateKind.InAndOut)]
    public void Saved_workbooks_and_previews_have_one_employee_row_for_repeated_input(MonthlyTemplateKind? kind)
    {
        var records = new[] { Record(), Record(name: "EXAMPLE EMPLOYEE") with { Order = 2 } };
        using var stream = new MemoryStream();
        if (kind is { } monthly) MonthlyTemplateWriter.Write(records, [], monthly, stream);
        else TemplateWriter.Write(records, stream);
        stream.Position = 0;
        using (var workbook = new XLWorkbook(stream))
        {
            var sheet = workbook.Worksheet(TemplateSpec.SheetName);
            var row = kind is null ? TemplateSpec.FirstDataRow : 6;
            var nameColumn = kind is null ? 3 : 2;
            Assert.Equal("Example Employee", sheet.Cell(row, nameColumn).GetString());
            Assert.True(sheet.Cell(row + 1, nameColumn).IsEmpty());
        }
        stream.Position = 0;
        var preview = MonthlyTemplateWriter.ReadPreview(stream);
        Assert.Single(preview.Sheets[0].Cells, cell => cell.Text == "Example Employee");
    }
}
