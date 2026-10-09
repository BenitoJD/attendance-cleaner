using System.Globalization;
using System.Text;

namespace AttendanceCleaner.Core;

public sealed record EmployeeSpanBar(string Id, string Name, int Minutes, string Duration, int CompletePairs);

/// <summary>Chart projections shared by the live dashboard and its PDF export.</summary>
public static class DashboardCharts
{
    public static IReadOnlyList<EmployeeSpanBar> LongestEmployeeSpans(Dashboard dashboard) =>
        dashboard.Employees
            .Where(employee => employee.PresentDays > 0 && employee.AverageHours is not null)
            .Select(employee =>
            {
                if (!AttendanceTime.TryParseHours(employee.AverageHours, out var hours))
                    throw new FormatException("Invalid dashboard duration.");
                return new EmployeeSpanBar(employee.Id, employee.Name, (int)Math.Round(hours * 60),
                    employee.AverageHours!, employee.PresentDays);
            })
            .OrderByDescending(employee => employee.Minutes)
            .ThenBy(employee => employee.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(employee => employee.Id, StringComparer.Ordinal)
            .Take(8).ToArray();

    public static string CompletenessRingSvg(Dashboard dashboard)
    {
        var svg = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 200 200\"><circle cx=\"100\" cy=\"100\" r=\"70\" fill=\"none\" stroke=\"#E2E8F0\" stroke-width=\"24\"/>");
        var offset = 0.0;
        var circumference = 2 * Math.PI * 70;
        foreach (var (count, color) in new[] { (dashboard.Present, "#059669"), (dashboard.InPunchOnly, "#D97706"), (dashboard.Absent, "#94A3B8") })
        {
            if (count == 0 || dashboard.TotalRows == 0) continue;
            var length = circumference * count / dashboard.TotalRows;
            svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"100\" cy=\"100\" r=\"70\" fill=\"none\" stroke=\"{color}\" stroke-width=\"24\" stroke-dasharray=\"{length:0.####} {circumference - length:0.####}\" stroke-dashoffset=\"{-offset:0.####}\" transform=\"rotate(-90 100 100)\"/>");
            offset += length;
        }
        svg.Append(CultureInfo.InvariantCulture, $"<text x=\"100\" y=\"103\" text-anchor=\"middle\" font-family=\"sans-serif\" font-size=\"26\" fill=\"#0F172A\">{dashboard.PresentRate:0}%</text><text x=\"100\" y=\"124\" text-anchor=\"middle\" font-family=\"sans-serif\" font-size=\"12\" fill=\"#64748B\">complete</text></svg>");
        return svg.ToString();
    }
}
