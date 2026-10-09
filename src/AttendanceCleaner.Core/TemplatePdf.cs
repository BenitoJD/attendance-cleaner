using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AttendanceCleaner.Core;

/// <summary>Monthly attendance pages can show the complete matrix or weekly sections.</summary>
public enum MonthlyPdfLayout
{
    FullMonth,
    Weekly,
}

/// <summary>Exports the formatted workbook cells, including monthly totals and holidays, to PDF.</summary>
public static class TemplatePdf
{
    public static byte[] Build(Stream workbookStream, bool monthly, MonthlyPdfLayout monthlyLayout = MonthlyPdfLayout.Weekly)
    {
        if (!Enum.IsDefined(monthlyLayout))
            throw new ArgumentOutOfRangeException(nameof(monthlyLayout));
        QuestPDF.Settings.License = LicenseType.Community;
        PdfFonts.EnsureRegistered();
        var workbook = MonthlyTemplateWriter.ReadPreview(workbookStream);
        if (workbook.Sheets.Count == 0)
            throw new InvalidDataException("The report has no sheets to export.");

        return Document.Create(document =>
        {
            foreach (var sheet in workbook.Sheets)
            {
                var matrix = monthly && sheet.ColumnCount > 20;
                var dayCount = matrix ? (sheet.ColumnCount - 10) / 2 : 0;
                var fullMonth = matrix && monthlyLayout == MonthlyPdfLayout.FullMonth;
                var sections = matrix && !fullMonth ? (dayCount + 6) / 7 : 1;
                for (var section = 0; section < sections; section++)
                {
                    // Repeat employee details and totals beside each week, so punch times stay legible.
                    var firstDay = section * 7 + 1;
                    var lastDay = fullMonth ? dayCount : Math.Min(dayCount, firstDay + 6);
                    var columns = matrix
                        ? Enumerable.Range(1, 4)
                            .Concat(Enumerable.Range(5 + (firstDay - 1) * 2, (lastDay - firstDay + 1) * 2))
                            .Concat(Enumerable.Range(5 + dayCount * 2, 6)).ToArray()
                        : Enumerable.Range(1, sheet.ColumnCount).ToArray();
                    var headerRows = matrix ? 5 : monthly ? 3 : TemplateSpec.HeaderRow;
                    document.Page(page =>
                    {
                        page.Size(fullMonth ? PageSizes.A1.Landscape() : PageSizes.A4.Landscape());
                        page.Margin(24);
                        page.DefaultTextStyle(style => style.FontFamily("Open Sans").FontSize(matrix && !fullMonth ? 7 : 9));
                        page.Header().PaddingBottom(8).Column(header =>
                        {
                            header.Item().Row(row =>
                            {
                                if (matrix || !monthly)
                                {
                                    using var logo = typeof(TemplatePdf).Assembly.GetManifestResourceStream(
                                        "AttendanceCleaner.Core.DakshinakTemplateLogo.png");
                                    if (logo is not null)
                                        row.ConstantItem(48).PaddingRight(8).Image(logo);
                                }
                                row.RelativeItem().AlignMiddle().Text(sheet.Name).FontSize(16).Bold().FontColor("#059669");
                            });
                            if (matrix)
                                header.Item().Text(fullMonth
                                    ? $"Full month · Days 1-{dayCount}"
                                    : $"Days {firstDay}-{lastDay} · Monthly totals cover all exported dates").FontSize(9);
                        });
                        page.Content().Table(table =>
                        {
                            table.ColumnsDefinition(definition =>
                            {
                                foreach (var column in columns)
                                    definition.RelativeColumn((float)(matrix && column == 1 ? 10 : Math.Min(sheet.ColumnWidths[column - 1], matrix && !fullMonth ? 24 : 40)));
                            });
                            table.Header(header =>
                            {
                                foreach (var cell in sheet.Cells.Where(cell => cell.Row <= headerRows))
                                    AddCell(header.Cell, cell, columns, 0, fullMonth);
                            });
                            foreach (var cell in sheet.Cells.Where(cell => cell.Row > headerRows))
                                AddCell(table.Cell, cell, columns, headerRows, fullMonth);
                        });
                        page.Footer().PaddingTop(8).Row(footer =>
                        {
                            footer.RelativeItem().Text("Attendance Report Studio").FontSize(7).FontColor("#6B7280");
                            footer.RelativeItem().AlignRight().Text(text =>
                            {
                                text.CurrentPageNumber();
                                text.Span(" / ");
                                text.TotalPages();
                            });
                        });
                    });
                }
            }
        }).GeneratePdf();
    }

    private static void AddCell(Func<QuestPDF.Elements.Table.ITableCellContainer> createCell, MonthlyPreviewCell cell, int[] columns, int rowOffset, bool fullMonth)
    {
        var included = columns.Select((column, index) => (column, index))
            .Where(item => item.column >= cell.Column && item.column < cell.Column + cell.ColumnSpan).ToArray();
        if (included.Length == 0) return;
        IContainer container = createCell().Row((uint)(cell.Row - rowOffset))
            .Column((uint)(included[0].index + 1)).RowSpan((uint)cell.RowSpan).ColumnSpan((uint)included.Length)
            .Border(0.4f).BorderColor("#CBD5E1").Background(cell.FillColor ?? "#FFFFFF")
            .PaddingHorizontal(3).PaddingVertical(fullMonth ? 3 : 4);
        container = container.PreventPageBreak();
        container = cell.HorizontalAlignment switch
        {
            "Center" => container.AlignCenter(),
            "Right" => container.AlignRight(),
            _ => container.AlignLeft(),
        };
        if (cell.VerticalAlignment == "Center") container = container.AlignMiddle();
        else if (cell.VerticalAlignment == "Bottom") container = container.AlignBottom();
        var text = container.Text(cell.Text).FontColor(cell.TextColor ?? "#1F2937");
        if (cell.Row == 1 && cell.ColumnSpan > 1) text.FontSize(12);
        if (cell.Bold) text.Bold();
    }
}

internal static class PdfFonts
{
    private static readonly Lazy<bool> Registration = new(() =>
    {
        using var stream = typeof(PdfFonts).Assembly.GetManifestResourceStream("AttendanceCleaner.Core.OpenSans.ttf")
            ?? throw new InvalidOperationException("The embedded Open Sans font is missing.");
        QuestPDF.Drawing.FontManager.RegisterFontFromStream(stream);
        return true;
    });

    public static void EnsureRegistered() => _ = Registration.Value;
}
