using AttendanceCleaner.Core;
using ClosedXML.Excel;
using Xunit;

namespace AttendanceCleaner.Tests;

public sealed class HolidaySheetLayoutTests
{
    private static readonly DateOnly Month = new(2026, 10, 1);

    [Theory]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime)]
    [InlineData(MonthlyTemplateKind.InAndOut)]
    public void Holiday_titles_headers_and_all_categories_have_readable_rows(MonthlyTemplateKind kind)
    {
        string[] categories =
        [
            MonthlyTemplateSpec.CorporateOfficeCategory,
            MonthlyTemplateSpec.TamilNaduCategory,
            MonthlyTemplateSpec.OptionalCorporateCategory,
            MonthlyTemplateSpec.OptionalTamilNaduCategory,
            MonthlyTemplateSpec.WorkingSaturdayCategory,
        ];
        var entries = categories.Select(category =>
            HolidayEntry.Create(new DateOnly(2026, 10, 24), "Working Day", category)).ToArray();
        using var stream = WriteWorkbook(entries, kind);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(MonthlyTemplateSpec.HolidaySheetName);

        Assert.Equal("Holiday Details · October 2026", sheet.Cell(1, 1).GetString());
        Assert.True(sheet.Row(1).Height >= 30);
        Assert.True(sheet.Row(3).Height >= 24);
        Assert.Equal(XLAlignmentVerticalValues.Center, sheet.Cell(1, 1).Style.Alignment.Vertical);
        foreach (var cell in sheet.Range(4, 2, entries.Length + 3, 3).Cells())
        {
            Assert.True(cell.Style.Alignment.WrapText);
            Assert.Equal(XLAlignmentVerticalValues.Center, cell.Style.Alignment.Vertical);
            Assert.True(cell.WorksheetRow().Height >= 24);
        }
        Assert.Equal(categories.OrderBy(category => category, StringComparer.OrdinalIgnoreCase),
            Enumerable.Range(4, entries.Length).Select(row => sheet.Cell(row, 3).GetString()));

        var preview = ReadHolidayPreview(stream);
        Assert.Equal(sheet.Row(1).Height, preview.RowHeights[0]);
        Assert.Equal(sheet.Row(3).Height, preview.RowHeights[2]);
        foreach (var category in categories)
        {
            var cell = Assert.Single(preview.Cells, cell => cell.Column == 3 && cell.Text == category);
            Assert.True(cell.WrapText);
            Assert.True(preview.RowHeights[cell.Row - 1] >= 24);
        }
    }

    [Theory]
    [InlineData("Company annual commemoration with a very long holiday description and special observance for all staff", 2)]
    [InlineData("First line\nSecond line\nThird line", 3)]
    [InlineData("First line\r\nSecond line\rThird line", 3)]
    [InlineData("WWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWWW", 3)]
    public void Long_and_multiline_holiday_names_keep_their_text_and_expand_the_row(string name, int minimumLines)
    {
        var entry = HolidayEntry.Create(new DateOnly(2026, 10, 20), name,
            MonthlyTemplateSpec.CorporateOfficeCategory);
        using var stream = WriteWorkbook([entry]);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(MonthlyTemplateSpec.HolidaySheetName);
        var normalizedName = name.Replace("\r\n", "\n").Replace('\r', '\n');

        Assert.Equal(normalizedName, sheet.Cell(4, 2).GetString());
        Assert.True(sheet.Cell(4, 2).Style.Alignment.WrapText);
        Assert.True(sheet.Row(4).Height >= minimumLines * sheet.Cell(4, 2).Style.Font.FontSize + 8);
        Assert.True(sheet.Row(4).Height > 24);

        var preview = ReadHolidayPreview(stream);
        var cell = Assert.Single(preview.Cells, cell => cell.Row == 4 && cell.Column == 2);
        Assert.Equal(normalizedName, cell.Text);
        Assert.True(cell.WrapText);
        Assert.Equal(sheet.Row(4).Height, preview.RowHeights[3]);
    }

    [Fact]
    public void Width_measurement_distinguishes_wide_and_narrow_names()
    {
        var entries = new[]
        {
            HolidayEntry.Create(new DateOnly(2026, 10, 20), new string('W', 80), MonthlyTemplateSpec.CorporateOfficeCategory),
            HolidayEntry.Create(new DateOnly(2026, 10, 21), new string('i', 80), MonthlyTemplateSpec.CorporateOfficeCategory),
        };
        using var stream = WriteWorkbook(entries);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(MonthlyTemplateSpec.HolidaySheetName);

        Assert.True(sheet.Row(4).Height > sheet.Row(5).Height);
        Assert.Equal(entries[0].Name, sheet.Cell(4, 2).GetString());
        Assert.Equal(entries[1].Name, sheet.Cell(5, 2).GetString());
    }

    [Theory]
    [InlineData(' ', 200)]
    [InlineData('\t', 40)]
    [InlineData('\u2003', 80)]
    public void Preserved_whitespace_wraps_and_expands_the_row(char whitespace, int count)
    {
        var name = "Alpha" + new string(whitespace, count) + "Omega END";
        var entry = HolidayEntry.Create(new DateOnly(2026, 10, 20), name,
            MonthlyTemplateSpec.CorporateOfficeCategory);
        using var stream = WriteWorkbook([entry]);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(MonthlyTemplateSpec.HolidaySheetName);

        Assert.Equal(name, sheet.Cell(4, 2).GetString());
        Assert.True(sheet.Cell(4, 2).Style.Alignment.WrapText);
        Assert.True(sheet.Row(4).Height >= 50);

        var preview = ReadHolidayPreview(stream);
        var cell = Assert.Single(preview.Cells, cell => cell.Row == 4 && cell.Column == 2);
        Assert.Equal(name, cell.Text);
        Assert.Equal(sheet.Row(4).Height, preview.RowHeights[3]);
    }

    [Theory]
    [InlineData(MonthlyTemplateKind.DutyAndOvertime)]
    [InlineData(MonthlyTemplateKind.InAndOut)]
    public void Missing_calendar_warning_preserves_merged_text_and_height(MonthlyTemplateKind kind)
    {
        using var stream = WriteWorkbook([], kind);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheet(MonthlyTemplateSpec.HolidaySheetName);
        var warning = HolidayCalendarStore.GetYearWarning([], Month.Year);

        Assert.Equal(warning, sheet.Cell(2, 1).GetString());
        Assert.True(sheet.Cell(2, 1).Style.Alignment.WrapText);
        Assert.True(sheet.Row(2).Height >= 60);

        var preview = ReadHolidayPreview(stream);
        var cell = Assert.Single(preview.Cells, cell => cell.Row == 2 && cell.Column == 1);
        Assert.Equal(warning, cell.Text);
        Assert.Equal(3, cell.ColumnSpan);
        Assert.True(cell.WrapText);
        Assert.True(preview.RowHeights[1] >= 60);
        Assert.True(preview.RowHeights[0] >= 30);
        Assert.True(preview.RowHeights[2] >= 24);
    }

    private static MemoryStream WriteWorkbook(IReadOnlyCollection<HolidayEntry> entries,
        MonthlyTemplateKind kind = MonthlyTemplateKind.DutyAndOvertime)
    {
        var stream = new MemoryStream();
        MonthlyTemplateWriter.Write(
            [new AttendanceRecord("007", "Layout Test Employee", "-", Month, "07:00", "16:00", 0)],
            entries, kind, stream);
        stream.Position = 0;
        return stream;
    }

    private static MonthlySheetPreview ReadHolidayPreview(MemoryStream stream)
    {
        stream.Position = 0;
        return Assert.Single(MonthlyTemplateWriter.ReadPreview(stream).Sheets,
            sheet => sheet.Name == MonthlyTemplateSpec.HolidaySheetName);
    }
}
