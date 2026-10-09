using System.Globalization;
using AttendanceCleaner.Core;
using Microsoft.Maui.Graphics;

namespace AttendanceCleaner;

/// <summary>Native chart cards. The page owns vertical scrolling; only date charts scroll sideways.</summary>
internal static class DashboardChartViews
{
    private static readonly Color Green = Color.FromArgb("#059669");
    private static readonly Color Amber = Color.FromArgb("#D97706");
    private static readonly Color Slate = Color.FromArgb("#94A3B8");
    private static readonly Color Blue = Color.FromArgb("#2563EB");

    public static void BuildKpis(Grid container, Dashboard d)
    {
        container.Children.Clear();
        foreach (var (title, value, caption, color) in new[]
        {
            ("Employees", d.EmployeeCount.ToString(CultureInfo.InvariantCulture), $"{d.Days.Count} exported date(s)", Blue),
            ("Complete pairs", $"{d.PresentRate:0.0}%", $"{d.Present} of {d.TotalRows} records", Green),
            ("Incomplete pairs", d.InPunchOnly.ToString(CultureInfo.InvariantCulture), "Needs punch review", Amber),
            ("Average recorded span", d.AverageHours ?? "No data", "Complete pairs only", Green),
        })
        {
            var content = new VerticalStackLayout { Spacing = 6 };
            content.Add(Text(title, 12, secondary: true));
            content.Add(Text(value, 25, bold: true, color: color));
            content.Add(Text(caption, 11, secondary: true));
            var card = Surface(content);
            card.Padding = 14;
            container.Add(card);
        }
        ArrangeCards(container, container.Width >= 680 ? 4 : container.Width >= 340 ? 2 : 1);
    }

    public static void ReflowKpis(Grid container)
    {
        var columns = container.Width >= 680 ? 4 : container.Width >= 340 ? 2 : 1;
        if (container.ColumnDefinitions.Count != columns) ArrangeCards(container, columns);
    }

    private static void ArrangeCards(Grid container, int columns)
    {
        container.ColumnDefinitions.Clear();
        container.RowDefinitions.Clear();
        for (var index = 0; index < columns; index++)
            container.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var index = 0; index < (container.Children.Count + columns - 1) / columns; index++)
            container.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var index = 0; index < container.Children.Count; index++)
        {
            container.SetColumn(container.Children[index], index % columns);
            container.SetRow(container.Children[index], index / columns);
        }
    }

    public static void BuildCharts(VerticalStackLayout container, Dashboard d, bool daily)
    {
        container.Clear();
        var overview = new Grid { RowSpacing = 12, ColumnSpacing = 16 };
        foreach (var card in new[] { Completeness(d), Distribution(d) })
            overview.Add(card);
        ArrangeCards(overview, 1);
        overview.SizeChanged += (_, _) =>
        {
            var columns = overview.Width >= 640 ? 2 : 1;
            if (overview.ColumnDefinitions.Count != columns) ArrangeCards(overview, columns);
        };
        container.Add(overview);
        if (!daily) container.Add(HoursTrend(d));
        container.Add(EmployeeSpans(d, daily));
    }

    private static Border Completeness(Dashboard d)
    {
        var content = Heading("Punch completeness", "Share of exported employee-date records");
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(120), new ColumnDefinition(GridLength.Star)), ColumnSpacing = 12 };
        var ring = new GraphicsView
        {
            HeightRequest = 158,
            Drawable = new CompletenessRing(d),
        };
        SemanticProperties.SetDescription(ring, $"{d.PresentRate:0.0}% complete. {d.Present} complete pairs, {d.InPunchOnly} incomplete pairs, {d.Absent} records without punches.");
        layout.Add(ring);
        var legend = new VerticalStackLayout { Spacing = 14, VerticalOptions = LayoutOptions.Center };
        foreach (var (label, count, color) in new[] { ("Complete pairs", d.Present, Green), ("Incomplete pairs", d.InPunchOnly, Amber), ("No punches", d.Absent, Slate) })
        {
            var item = new VerticalStackLayout { Spacing = 2 };
            item.Add(Text(label, 12, color: color, bold: true));
            item.Add(Text($"{count}  ·  {(d.TotalRows == 0 ? 0 : 100.0 * count / d.TotalRows):0.0}%", 12, secondary: true));
            legend.Add(item);
        }
        layout.Add(legend, 1);
        content.Add(layout);
        return Surface(content);
    }

    private static Border Distribution(Dashboard d)
    {
        var content = Heading("Recorded span distribution", "Complete pairs only · breaks are not deducted");
        if (d.Present == 0 || d.DurationBuckets.Count == 0)
            content.Add(Empty("No complete pairs to calculate recorded spans."));
        else
        {
            var bars = new Grid { ColumnSpacing = 10, HeightRequest = 158 };
            var max = Math.Max(1, d.DurationBuckets.Max(bucket => bucket.Count));
            foreach (var bucket in d.DurationBuckets)
            {
                bars.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                var column = new VerticalStackLayout { Spacing = 6, VerticalOptions = LayoutOptions.End };
                var count = Text(bucket.Count.ToString(CultureInfo.InvariantCulture), 16, bold: true, color: Blue);
                count.HorizontalTextAlignment = TextAlignment.Center;
                column.Add(count);
                column.Add(new BoxView { Color = Blue, WidthRequest = 30, HeightRequest = 88.0 * bucket.Count / max, HorizontalOptions = LayoutOptions.Center });
                var label = Text(bucket.Label, 11, secondary: true);
                label.HorizontalTextAlignment = TextAlignment.Center;
                label.HeightRequest = 30;
                column.Add(label);
                bars.Add(column, bars.ColumnDefinitions.Count - 1);
            }
            content.Add(bars);
        }
        return Surface(content);
    }

    private static Border HoursTrend(Dashboard d)
    {
        var content = Heading("Average recorded span by date", "Each date averages its complete pairs · hh:mm labels · no data is not zero");
        var bars = new Grid { ColumnSpacing = 6 };
        var max = Math.Max(1, d.Days.Max(day => day.AverageMinutes ?? 0));
        foreach (var day in d.Days)
        {
            bars.ColumnDefinitions.Add(new ColumnDefinition(62));
            var column = new VerticalStackLayout { Spacing = 5, VerticalOptions = LayoutOptions.End };
            var value = Text(day.AverageHours ?? "No data", 11, color: day.AverageMinutes.HasValue ? Blue : Slate, bold: true);
            value.HorizontalTextAlignment = TextAlignment.Center;
            column.Add(value);
            column.Add(new BoxView { Color = Blue, WidthRequest = 24, HeightRequest = 110.0 * (day.AverageMinutes ?? 0) / max, HorizontalOptions = LayoutOptions.Center });
            var label = Text(day.Date.ToString("dd MMM", CultureInfo.InvariantCulture), 11, secondary: true);
            label.HorizontalTextAlignment = TextAlignment.Center;
            column.Add(label);
            bars.Add(column, bars.ColumnDefinitions.Count - 1);
        }
        content.Add(new ScrollView { Orientation = ScrollOrientation.Horizontal, HeightRequest = 165, Content = bars });
        return Surface(content);
    }

    private static Border EmployeeSpans(Dashboard d, bool daily)
    {
        var content = Heading(daily ? "Longest recorded spans" : "Longest average recorded spans",
            "Up to 8 employees · highest spans first · complete pairs only");
        var employees = DashboardCharts.LongestEmployeeSpans(d);
        if (employees.Count == 0)
            content.Add(Empty("No complete pairs to compare. Review missing punches in the employee summary."));
        else
        {
            var max = Math.Max(1, employees.Max(employee => employee.Minutes));
            foreach (var employee in employees)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(GridLength.Star), new ColumnDefinition(new GridLength(1.6, GridUnitType.Star)), new ColumnDefinition(GridLength.Auto)), ColumnSpacing = 12, Padding = new Thickness(0, 5) };
                var caption = new VerticalStackLayout { Spacing = 2 };
                var name = Text(employee.Name, 12, bold: true);
                name.LineBreakMode = LineBreakMode.TailTruncation;
                name.MaxLines = 1;
                caption.Add(name);
                caption.Add(Text($"ID {employee.Id} · {employee.CompletePairs} complete pair(s)", 10, secondary: true));
                row.Add(caption);
                var track = new Grid { HeightRequest = 12, VerticalOptions = LayoutOptions.Center };
                track.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(employee.Minutes, GridUnitType.Star)));
                track.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(max - employee.Minutes, GridUnitType.Star)));
                track.BackgroundColor = Color.FromArgb("#E2E8F0");
                if (employee.Minutes > 0) track.Add(new BoxView { Color = Green });
                row.Add(track, 1);
                var duration = Text(employee.Duration, 13, bold: true, color: Green);
                duration.VerticalOptions = LayoutOptions.Center;
                row.Add(duration, 2);
                content.Add(row);
            }
        }
        content.Add(Text("Recorded spans measure time between punches, not productivity or payable hours.", 11, secondary: true));
        return Surface(content);
    }

    private static VerticalStackLayout Heading(string title, string subtitle)
    {
        var content = new VerticalStackLayout { Spacing = 10 };
        content.Add(Text(title, 15, bold: true));
        content.Add(Text(subtitle, 11, secondary: true));
        return content;
    }

    private static Label Empty(string text) => Text(text, 13, secondary: true);

    private static Label Text(string text, int size, bool bold = false, bool secondary = false, Color? color = null)
    {
        var label = new Label { Text = text, FontSize = size, FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None };
        if (color is not null)
            label.SetAppThemeColor(Label.TextColorProperty, color,
                color == Green ? Color.FromArgb("#34D399") : color == Blue ? Color.FromArgb("#60A5FA")
                    : color == Amber ? Color.FromArgb("#FBBF24") : color);
        else label.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb(secondary ? "#64748B" : "#0F172A"), Color.FromArgb(secondary ? "#CBD5E1" : "#F8FAFC"));
        return label;
    }

    private static Border Surface(View content)
    {
        var border = new Border { Content = content, Padding = 16, StrokeThickness = 0, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 } };
        border.SetAppThemeColor(Border.BackgroundColorProperty, Colors.White, Color.FromArgb("#1F2937"));
        border.Shadow = new Shadow { Brush = new SolidColorBrush(Colors.Black), Opacity = 0.05f, Radius = 10 };
        return border;
    }

    private sealed class CompletenessRing(Dashboard dashboard) : IDrawable
    {
        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            var diameter = Math.Min(dirtyRect.Width - 20, 114);
            var x = (dirtyRect.Width - diameter) / 2;
            var y = (dirtyRect.Height - diameter) / 2;
            canvas.StrokeSize = 16;
            canvas.StrokeColor = Color.FromArgb("#E2E8F0");
            canvas.DrawEllipse(x, y, diameter, diameter);
            var angle = 90f;
            foreach (var (count, color) in new[] { (dashboard.Present, Green), (dashboard.InPunchOnly, Amber), (dashboard.Absent, Slate) })
            {
                if (count == 0 || dashboard.TotalRows == 0) continue;
                var sweep = 360f * count / dashboard.TotalRows;
                canvas.StrokeColor = color;
                if (count == dashboard.TotalRows) canvas.DrawEllipse(x, y, diameter, diameter);
                else canvas.DrawArc(x, y, diameter, diameter, angle, angle - sweep, true, false);
                angle -= sweep;
            }
            canvas.FontColor = Application.Current?.RequestedTheme == AppTheme.Dark ? Colors.White : Color.FromArgb("#0F172A");
            canvas.FontSize = 24;
            canvas.DrawString($"{dashboard.PresentRate:0}%", 0, dirtyRect.Height / 2 - 18, dirtyRect.Width, 32, HorizontalAlignment.Center, VerticalAlignment.Center);
            canvas.FontColor = Slate;
            canvas.FontSize = 11;
            canvas.DrawString("complete", 0, dirtyRect.Height / 2 + 14, dirtyRect.Width, 18, HorizontalAlignment.Center, VerticalAlignment.Center);
        }
    }
}
