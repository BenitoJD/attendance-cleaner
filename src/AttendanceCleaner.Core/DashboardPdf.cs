using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AttendanceCleaner.Core;

/// <summary>
/// Renders a dashboard as a clean multi-page PDF report: KPIs, daily trend chart,
/// and the per-employee summary table. Uses the bundled OpenSans font so the PDF
/// renders identically on every machine.
/// </summary>
public static class DashboardPdf
{
    public static byte[] Build(Dashboard d, string sourceFileName, DashboardPeriod? period = null)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        PdfFonts.EnsureRegistered();

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontSize(9).FontFamily("Open Sans"));

                page.Header().Column(col =>
                {
                    col.Item().Text(period is null ? "Attendance Dashboard" : $"{period} Attendance Dashboard")
                        .FontSize(20).Bold().FontColor("#059669");
                    col.Item().Text(MonthlyTemplateSpec.CompanyName.TrimEnd('/')).FontSize(10).Bold();
                    col.Item().Text($"{d.EmployeeCount} employees · {d.From.ToString(TemplateSpec.DateFormat, CultureInfo.InvariantCulture)} to {d.To.ToString(TemplateSpec.DateFormat, CultureInfo.InvariantCulture)}")
                        .FontSize(10).FontColor("#6B7280");
                    col.Item().Text($"Source: {sourceFileName}").FontSize(8).FontColor("#9CA3AF");
                    col.Item().Text("Punch evidence only. No punches does not establish absence or leave. Average hours uses complete pairs.")
                        .FontSize(8).FontColor("#6B7280");
                });

                page.Content().PaddingVertical(8).Column(col =>
                {
                    col.Spacing(12);

                    // KPI band
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Kpi("Rows", d.TotalRows.ToString(CultureInfo.InvariantCulture), "#111827");
                        row.RelativeItem().Kpi("Complete pairs", $"{d.PresentRate:0.0}% ({d.Present})", "#059669");
                        row.RelativeItem().Kpi("No punches", $"{d.AbsentRate:0.0}% ({d.Absent})", "#6B7280");
                        if (d.InPunchOnly > 0)
                        {
                            row.RelativeItem().Kpi("Incomplete pairs", $"{d.InPunchOnly}", "#D97706");
                        }
                        row.RelativeItem().Kpi("Avg in", d.AverageIn ?? "-", "#111827");
                        row.RelativeItem().Kpi("Avg out", d.AverageOut ?? "-", "#111827");
                        row.RelativeItem().Kpi("Avg hours", d.AverageHours ?? "-", "#111827");
                    });

                    // Keep each chart panel intact, and keep daily labels legible for long reports.
                    foreach (var days in d.Days.OrderBy(day => day.Date).Chunk(31))
                    {
                        col.Item().ShowEntire().Border(1).BorderColor("#E2E8F0").Padding(10).Column(chartCol =>
                        {
                            chartCol.Item().Text($"Punch coverage · {days[0].Date.ToString(TemplateSpec.DateFormat, CultureInfo.InvariantCulture)}"
                                + $" to {days[^1].Date.ToString(TemplateSpec.DateFormat, CultureInfo.InvariantCulture)}")
                                .Bold().FontSize(11);
                            var max = Math.Max(1, days.Max(day => day.Present + day.Absent + day.InPunchOnly));
                            chartCol.Item().PaddingTop(6).Height(130).Row(bars =>
                            {
                                foreach (var day in days)
                                {
                                    bars.RelativeItem().AlignBottom().Column(barCol =>
                                    {
                                        barCol.Item().AlignCenter().Width(14).Height((float)(90.0 * day.Present / max))
                                            .Background("#059669").PaddingHorizontal(1);
                                        barCol.Item().AlignCenter().Width(14).Height((float)(90.0 * day.InPunchOnly / max))
                                            .Background("#D97706").PaddingHorizontal(1);
                                        barCol.Item().AlignCenter().Width(14).Height((float)(90.0 * day.Absent / max))
                                            .Background("#94A3B8").PaddingHorizontal(1);
                                        barCol.Item().PaddingTop(2).AlignCenter()
                                            .Text(day.Date.ToString("dd MMM", CultureInfo.InvariantCulture))
                                            .FontSize(8).FontColor("#6B7280");
                                    });
                                }
                            });
                            chartCol.Item().PaddingTop(2).Row(legend =>
                            {
                                legend.ConstantItem(10).Height(8).Background("#059669");
                                legend.ConstantItem(4);
                                legend.AutoItem().Text("complete pairs").FontSize(7).FontColor("#6B7280");
                                legend.ConstantItem(10);
                                legend.ConstantItem(10).Height(8).Background("#94A3B8");
                                legend.ConstantItem(4);
                                legend.AutoItem().Text("no punches").FontSize(7).FontColor("#6B7280");
                                legend.ConstantItem(10);
                                legend.ConstantItem(10).Height(8).Background("#D97706");
                                legend.ConstantItem(4);
                                legend.AutoItem().Text("incomplete pairs").FontSize(7).FontColor("#6B7280");
                            });
                        });
                    }

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
                            h.Cell().Element(c => Cell(c, "Complete", header: true));
                            h.Cell().Element(c => Cell(c, "No punch", header: true));
                            h.Cell().Element(c => Cell(c, "Incomplete", header: true));
                            h.Cell().Element(c => Cell(c, "Avg in", header: true));
                            h.Cell().Element(c => Cell(c, "Avg out", header: true));
                            h.Cell().Element(c => Cell(c, "Avg hrs", header: true));
                            h.Cell().Element(c => Cell(c, "Pair rate", header: true));
                        });

                        foreach (var (e, i) in d.Employees.Select((e, i) => (e, i)))
                        {
                            table.Cell().Element(c => Cell(c, (i + 1).ToString(CultureInfo.InvariantCulture)));
                            table.Cell().Element(c => Cell(c, e.Id));
                            table.Cell().Element(c => Cell(c, e.Name));
                            table.Cell().Element(c => Cell(c, e.PresentDays.ToString(CultureInfo.InvariantCulture), color: "#059669"));
                            table.Cell().Element(c => Cell(c, e.AbsentDays.ToString(CultureInfo.InvariantCulture), color: "#6B7280"));
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
                    foot.RelativeItem().Text($"Generated {DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(5.5)):dd-MM-yyyy HH:mm} IST · Attendance Report Studio")
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
