using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Drawing;
using QuestPDF.Infrastructure;

namespace AttendanceCleaner.Core;

/// <summary>
/// Renders a dashboard as a clean multi-page PDF report: KPIs, daily trend chart,
/// and the per-employee summary table. Uses the bundled OpenSans font so the PDF
/// renders identically on every machine.
/// </summary>
public static class DashboardPdf
{
    private const string FontResourceName = "AttendanceCleaner.Core.OpenSans.ttf";
    private static bool _fontRegistered;

    private static void EnsureFont()
    {
        if (_fontRegistered) return;
        var assembly = typeof(DashboardPdf).Assembly;
        using var stream = assembly.GetManifestResourceStream(FontResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded font '{FontResourceName}' is missing. Both the app and test projects must embed OpenSans-Regular.ttf with this LogicalName.");
        QuestPDF.Drawing.FontManager.RegisterFontFromStream(stream);
        _fontRegistered = true;
    }

    public static byte[] Build(Dashboard d, string sourceFileName)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        EnsureFont();

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontSize(9).FontFamily("Open Sans"));

                page.Header().Column(col =>
                {
                    col.Item().Text("Attendance Dashboard").FontSize(20).Bold().FontColor("#059669");
                    col.Item().Text($"{d.EmployeeCount} employees · {d.From.ToString(TemplateSpec.DateFormat, CultureInfo.InvariantCulture)} to {d.To.ToString(TemplateSpec.DateFormat, CultureInfo.InvariantCulture)}")
                        .FontSize(10).FontColor("#6B7280");
                    col.Item().Text($"Source: {sourceFileName}").FontSize(8).FontColor("#9CA3AF");
                });

                page.Content().PaddingVertical(8).Column(col =>
                {
                    col.Spacing(12);

                    // KPI band
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Kpi("Rows", d.TotalRows.ToString(CultureInfo.InvariantCulture), "#111827");
                        row.RelativeItem().Kpi("Present", $"{d.PresentRate:0.0}% ({d.Present})", "#059669");
                        row.RelativeItem().Kpi("Absent", $"{d.AbsentRate:0.0}% ({d.Absent})", "#DC2626");
                        if (d.InPunchOnly > 0)
                        {
                            row.RelativeItem().Kpi("In punch only", $"{d.InPunchOnly}", "#D97706");
                        }
                        row.RelativeItem().Kpi("Avg in", d.AverageIn ?? "-", "#111827");
                        row.RelativeItem().Kpi("Avg out", d.AverageOut ?? "-", "#111827");
                        row.RelativeItem().Kpi("Avg hours", d.AverageHours ?? "-", "#111827");
                    });

                    // daily trend chart
                    col.Item().Border(1).BorderColor("#E2E8F0").Padding(10).Column(chartCol =>
                    {
                        chartCol.Item().Text("Daily attendance").Bold().FontSize(11);
                        var max = Math.Max(1, d.Days.Max(x => Math.Max(x.Present, x.Absent)));
                        chartCol.Item().PaddingTop(6).Height(110).Row(bars =>
                        {
                            foreach (var day in d.Days)
                            {
                                bars.RelativeItem().Column(barCol =>
                                {
                                    barCol.Item().AlignBottom().Height((float)(90.0 * day.Present / max))
                                        .Background("#059669").PaddingHorizontal(1);
                                    barCol.Item().Height((float)(90.0 * day.Absent / max))
                                        .Background("#FCA5A5").PaddingHorizontal(1);
                                    barCol.Item().PaddingTop(2).AlignCenter()
                                        .Text(day.Date.Day.ToString(CultureInfo.InvariantCulture))
                                        .FontSize(6).FontColor("#6B7280");
                                });
                            }
                        });
                        chartCol.Item().PaddingTop(2).Row(legend =>
                        {
                            legend.ConstantItem(10).Height(8).Background("#059669");
                            legend.ConstantItem(4);
                            legend.AutoItem().Text("present").FontSize(7).FontColor("#6B7280");
                            legend.ConstantItem(10);
                            legend.ConstantItem(10).Height(8).Background("#FCA5A5");
                            legend.ConstantItem(4);
                            legend.AutoItem().Text("absent").FontSize(7).FontColor("#6B7280");
                        });
                    });

                    // per-employee summary table
                    col.Item().Border(1).BorderColor("#E2E8F0").Table(table =>
                    {
                        table.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(34);
                            c.ConstantColumn(40);
                            c.RelativeColumn();
                            c.ConstantColumn(52);
                            c.ConstantColumn(52);
                            c.ConstantColumn(56);
                            c.ConstantColumn(52);
                            c.ConstantColumn(52);
                            c.ConstantColumn(56);
                            c.ConstantColumn(58);
                        });

                        void Cell(QuestPDF.Infrastructure.IContainer cell, string text, bool header = false, string? color = null)
                        {
                            var style = cell.Background(header ? "#059669" : "#FFFFFF")
                                .BorderBottom(0.5f).BorderColor("#E5E7EB")
                                .PaddingHorizontal(6).PaddingVertical(4)
                                .Text(text)
                                .FontColor(header ? "#FFFFFF" : color ?? "#1F2937")
                                .FontSize(header ? 8 : 9);
                            if (header) style.Bold();
                        }

                        table.Header(h =>
                        {
                            h.Cell().Element(c => Cell(c, "Sl.No", header: true));
                            h.Cell().Element(c => Cell(c, "ID", header: true));
                            h.Cell().Element(c => Cell(c, "Name", header: true));
                            h.Cell().Element(c => Cell(c, "Present", header: true));
                            h.Cell().Element(c => Cell(c, "Absent", header: true));
                            h.Cell().Element(c => Cell(c, "In only", header: true));
                            h.Cell().Element(c => Cell(c, "Avg in", header: true));
                            h.Cell().Element(c => Cell(c, "Avg out", header: true));
                            h.Cell().Element(c => Cell(c, "Avg hrs", header: true));
                            h.Cell().Element(c => Cell(c, "Rate", header: true));
                        });

                        foreach (var (e, i) in d.Employees.Select((e, i) => (e, i)))
                        {
                            table.Cell().Element(c => Cell(c, (i + 1).ToString(CultureInfo.InvariantCulture)));
                            table.Cell().Element(c => Cell(c, e.Id));
                            table.Cell().Element(c => Cell(c, e.Name));
                            table.Cell().Element(c => Cell(c, e.PresentDays.ToString(CultureInfo.InvariantCulture), color: "#059669"));
                            table.Cell().Element(c => Cell(c, e.AbsentDays.ToString(CultureInfo.InvariantCulture), color: e.AbsentDays > 0 ? "#DC2626" : null));
                            table.Cell().Element(c => Cell(c, e.InPunchOnlyDays.ToString(CultureInfo.InvariantCulture), color: e.InPunchOnlyDays > 0 ? "#D97706" : null));
                            table.Cell().Element(c => Cell(c, e.AverageIn ?? "-"));
                            table.Cell().Element(c => Cell(c, e.AverageOut ?? "-"));
                            table.Cell().Element(c => Cell(c, e.AverageHours ?? "-"));
                            table.Cell().Element(c => Cell(c, $"{e.AttendanceRate:0}%"));
                        }
                    });
                });

                page.Footer().Row(foot =>
                {
                    foot.RelativeItem().Text($"Generated {DateTime.Now:dd-MM-yyyy HH:mm} · Attendance Report Studio")
                        .FontSize(7).FontColor("#9CA3AF");
                    foot.RelativeItem().AlignRight().Text(text =>
                    {
                        text.CurrentPageNumber().FontSize(7).FontColor("#9CA3AF");
                        text.Span(" / ").FontSize(7).FontColor("#9CA3AF");
                        text.TotalPages().FontSize(7).FontColor("#9CA3AF");
                    });
                });
            });
        }).GeneratePdf();
    }
}

file static class PdfExtensions
{
    public static void Kpi(this QuestPDF.Infrastructure.IContainer container, string label, string value, string color) =>
        container.Border(1).BorderColor("#E2E8F0").PaddingHorizontal(8).PaddingVertical(6).Column(col =>
        {
            col.Item().Text(value).FontSize(13).Bold().FontColor(color);
            col.Item().Text(label).FontSize(7).FontColor("#6B7280");
        });
}
