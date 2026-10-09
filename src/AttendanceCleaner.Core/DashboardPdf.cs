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

                    if (d.TotalRows > 0)
                    {
                        AddVisualCharts(col, d, period == DashboardPeriod.Daily || d.Days.Count == 1);
                    }

                    // Keep each chart panel intact, and keep daily labels legible for long reports.
                    foreach (var days in d.Days.Where(_ => d.Days.Count > 1).OrderBy(day => day.Date).Chunk(31))
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
                                            .Text(day.Date.ToString("dd", CultureInfo.InvariantCulture)
                                                + "\n" + day.Date.ToString("MMM", CultureInfo.InvariantCulture))
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

    private static void AddVisualCharts(ColumnDescriptor col, Dashboard d, bool daily)
    {
        col.Item().ShowEntire().Row(overview =>
        {
            overview.Spacing(12);
            overview.RelativeItem().Border(1).BorderColor("#E2E8F0").Padding(12).Column(card =>
            {
                card.Item().Text("Punch completeness").FontSize(12).Bold();
                card.Item().Text("Share of exported employee-date records").FontSize(8).FontColor("#64748B");
                card.Item().PaddingTop(6).Row(row =>
                {
                    row.ConstantItem(120).Height(120).Svg(DashboardCharts.CompletenessRingSvg(d));
                    row.RelativeItem().AlignMiddle().Column(legend =>
                    {
                        legend.Spacing(8);
                        foreach (var (label, count, color) in new[] { ("Complete pairs", d.Present, "#059669"), ("Incomplete pairs", d.InPunchOnly, "#D97706"), ("No punches", d.Absent, "#64748B") })
                            legend.Item().Text($"{label}: {count} ({100.0 * count / d.TotalRows:0.0}%)").FontSize(9).FontColor(color);
                    });
                });
            });
            overview.RelativeItem().Border(1).BorderColor("#E2E8F0").Padding(12).Column(card =>
            {
                card.Item().Text("Recorded span distribution").FontSize(12).Bold();
                card.Item().Text("Complete pairs only · breaks are not deducted").FontSize(8).FontColor("#64748B");
                if (d.Present == 0 || d.DurationBuckets.Count == 0)
                    card.Item().PaddingTop(30).Text("No complete pairs to calculate recorded spans.").FontColor("#64748B");
                else
                {
                    var max = Math.Max(1, d.DurationBuckets.Max(bucket => bucket.Count));
                    card.Item().PaddingTop(6).Height(120).Row(bars =>
                    {
                        foreach (var bucket in d.DurationBuckets)
                            bars.RelativeItem().AlignBottom().Column(bar =>
                            {
                                bar.Item().AlignCenter().Text(bucket.Count.ToString(CultureInfo.InvariantCulture)).Bold().FontColor("#2563EB");
                                bar.Item().PaddingTop(4).AlignCenter().Width(28).Height(72f * bucket.Count / max).Background("#2563EB");
                                bar.Item().PaddingTop(5).AlignCenter().Text(bucket.Label).FontSize(8).FontColor("#64748B");
                            });
                    });
                }
            });
        });

        if (!daily && d.Days.Count <= 31)
            col.Item().ShowEntire().Border(1).BorderColor("#E2E8F0").Padding(12).Column(card =>
            {
                card.Item().Text("Average recorded span by date").FontSize(12).Bold();
                card.Item().Text("Each date averages its complete pairs · hh:mm labels · dash means no data, not zero").FontSize(8).FontColor("#64748B");
                var max = Math.Max(1, d.Days.Max(day => day.AverageMinutes ?? 0));
                card.Item().PaddingTop(8).Height(120).Row(bars =>
                {
                    foreach (var day in d.Days)
                        bars.RelativeItem().AlignBottom().Column(bar =>
                        {
                            bar.Item().AlignCenter().Text(day.AverageHours ?? "-").FontSize(7).FontColor("#2563EB");
                            bar.Item().PaddingTop(3).AlignCenter().Width(12).Height(75f * (day.AverageMinutes ?? 0) / max).Background("#2563EB");
                            bar.Item().PaddingTop(3).AlignCenter().Text(day.Date.ToString("dd\nMMM", CultureInfo.InvariantCulture)).FontSize(7).FontColor("#64748B");
                        });
                });
            });

        col.Item().ShowEntire().Border(1).BorderColor("#E2E8F0").Padding(12).Column(card =>
        {
            card.Spacing(5);
            card.Item().Text(daily ? "Longest recorded spans" : "Longest average recorded spans").FontSize(12).Bold();
            card.Item().Text("Up to 8 employees · highest spans first · complete pairs only").FontSize(8).FontColor("#64748B");
            var employees = DashboardCharts.LongestEmployeeSpans(d);
            var max = Math.Max(1, employees.Select(employee => employee.Minutes).DefaultIfEmpty(0).Max());
            if (employees.Count == 0)
                card.Item().Text("No complete pairs to compare.").FontColor("#64748B");
            foreach (var employee in employees)
                card.Item().PaddingVertical(2).Row(row =>
                {
                    row.RelativeItem().Column(name =>
                    {
                        name.Item().Text(employee.Name).FontSize(9).Bold().ClampLines(1);
                        name.Item().Text($"ID {employee.Id} · {employee.CompletePairs} complete pair(s)").FontSize(7).FontColor("#64748B").ClampLines(1);
                    });
                    row.ConstantItem(10);
                    row.RelativeItem(2).AlignMiddle().Height(9).Background("#E2E8F0").Row(track =>
                    {
                        if (employee.Minutes > 0) track.RelativeItem(employee.Minutes).Background("#059669");
                        if (max > employee.Minutes) track.RelativeItem(max - employee.Minutes);
                    });
                    row.ConstantItem(10);
                    row.ConstantItem(42).AlignMiddle().AlignRight().Text(employee.Duration).Bold().FontColor("#059669");
                });
            card.Item().Text("Recorded spans measure time between punches, not productivity or payable hours.").FontSize(8).FontColor("#64748B");
        });
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
