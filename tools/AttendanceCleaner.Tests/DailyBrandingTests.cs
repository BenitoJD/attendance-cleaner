using AttendanceCleaner.Core;
using ClosedXML.Excel;
using Xunit;

namespace AttendanceCleaner.Tests;

public sealed class DailyBrandingTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(180)]
    public void Branding_preserves_daily_data_filters_and_column_widths(int employeeCount)
    {
        var records = Enumerable.Range(1, employeeCount).Select(employee => new AttendanceRecord(
            $"001{employee}", $"Employee {employee} with longer name", "Male", new DateOnly(2026, 10, 9),
            employee % 3 == 0 ? null : "06:51", employee % 4 == 0 ? null : "19:00", employee,
            Attended: "12")).ToArray();
        var expected = TemplateWriter.BuildRows(records);
        using var stream = new MemoryStream();
        TemplateWriter.Write(records, stream);
        stream.Position = 0;
        using (var workbook = new XLWorkbook(stream))
        {
            var sheet = Assert.Single(workbook.Worksheets);
            Assert.Contains(MonthlyTemplateSpec.CompanyName, sheet.Cell(1, 2).GetString());
            Assert.Contains("Daily Attendance Report", sheet.Cell(1, 2).GetString());
            Assert.Single(sheet.Pictures);
            var banner = Assert.Single(sheet.MergedRanges);
            Assert.Equal("B1:J1", banner.RangeAddress.ToStringRelative());
            Assert.Equal(TemplateSpec.HeaderRow, sheet.SheetView.SplitRow);
            Assert.Equal(employeeCount + TemplateSpec.HeaderRow, sheet.LastRowUsed()!.RowNumber());
            Assert.Equal(10, sheet.LastColumnUsed()!.ColumnNumber());
            for (var column = 0; column < TemplateSpec.Headers.Length; column++)
                Assert.Equal(TemplateSpec.Headers[column], sheet.Cell(TemplateSpec.HeaderRow, column + 1).GetString());
            for (var row = 0; row < expected.Count; row++)
                for (var column = 0; column < TemplateSpec.Headers.Length; column++)
                    Assert.Equal(expected[row].Cells[column], sheet.Cell(TemplateSpec.FirstDataRow + row, column + 1).GetFormattedString());
            foreach (var (column, width) in TemplateSpec.ColumnWidths)
                Assert.Equal(width, sheet.Column(column).Width);
            if (employeeCount == 0)
                Assert.False(sheet.AutoFilter.IsEnabled);
            else
            {
                Assert.True(sheet.AutoFilter.IsEnabled);
                Assert.Equal(TemplateSpec.HeaderRow, sheet.AutoFilter.Range.RangeAddress.FirstAddress.RowNumber);
                Assert.Equal(employeeCount + 1, sheet.AutoFilter.Range.RowCount());
                Assert.Equal(10, sheet.AutoFilter.Range.ColumnCount());
            }
        }
        // The PDF reads the same branded workbook, including the empty/header-only case.
        stream.Position = 0;
        var pdf = TemplatePdf.Build(stream, monthly: false);
        Assert.True(pdf.Length > 1000);
    }
}
