using System.Globalization;
using System.Text;
using AttendanceCleaner.Core;

namespace AttendanceCleaner;

public partial class MainPage : ContentPage
{
    private FileResult? _selectedFile;
    private Dashboard? _dashboard;
    private IReadOnlyList<TemplateRow>? _convertedRows;
    private string _sourceFileName = "";
    private string _outputDirectory = "";

    public MainPage()
    {
        InitializeComponent();
        if (Application.Current is { } app)
            app.RequestedThemeChanged += OnRequestedThemeChanged;
    }

    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_convertedRows is not null)
                BuildTable(_convertedRows);
            if (_dashboard is not null)
                BuildDashboard();

            if (DashboardSection.IsVisible)
                ShowDashboardTab();
            else
                ShowTableTab();
        });
    }

    private async void OnPickFileClicked(object? sender, EventArgs e)
    {
        var fileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
        {
            [DevicePlatform.WinUI] = new[] { ".xls", ".xlsx", ".html" },
            [DevicePlatform.Android] = new[]
            {
                "application/vnd.ms-excel",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                "text/html",
            },
            [DevicePlatform.iOS] = new[] { "com.microsoft.excel.xls", "org.openxmlformats.spreadsheetml.sheet", "public.html" },
            [DevicePlatform.MacCatalyst] = new[] { "com.microsoft.excel.xls", "org.openxmlformats.spreadsheetml.sheet", "public.html" },
        });

        try
        {
            var result = await FilePicker.PickAsync(new PickOptions
            {
                PickerTitle = "Choose the attendance report file",
                FileTypes = fileTypes,
            });
            if (result == null) return;

            _selectedFile = result;
            FileNameLabel.Text = result.FileName;
            StatusLabel.Text = "";
            ConvertBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Could not open the file picker: {ex.Message}";
        }
    }

    private async void OnConvertClicked(object? sender, EventArgs e)
    {
        if (_selectedFile is null) return;

        ConvertBtn.IsEnabled = false;
        PickFileBtn.IsEnabled = false;
        Spinner.IsVisible = true;
        Spinner.IsRunning = true;
        StatusLabel.Text = "Reading the report...";

        try
        {
            // 1. read and parse the report, work out the output file name
            var parsed = await Task.Run(async () =>
            {
                string html;
                using (var stream = await _selectedFile.OpenReadAsync())
                using (var buffered = new MemoryStream())
                {
                    await stream.CopyToAsync(buffered);
                    var bytes = buffered.ToArray();

                    AttendanceParser.ValidateInputIsNotWorkbook(bytes.AsSpan(0, Math.Min(8, bytes.Length)));

                    html = Encoding.UTF8.GetString(bytes);
                }
                if (!html.Contains('<') || !html.Contains("table", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "This file is not a recognised attendance export. Please choose the .xls file downloaded from the attendance software.");
                }

                var report = AttendanceParser.Parse(html);
                var dates = report.Records.Select(r => r.Date).Distinct().OrderBy(d => d).ToList();
                if (dates.Count == 0)
                {
                    throw new InvalidDataException("No attendance rows were found in this file.");
                }

                // one date -> daily file name; a range -> monthly file name
                var fileName = string.Format(
                    CultureInfo.InvariantCulture,
                    dates.Count > 1 ? TemplateSpec.MonthlyFileName : TemplateSpec.DailyFileName,
                    dates[0]);

                return (report, dates, fileName);
            });

            // 2. let the user choose where to save it
            StatusLabel.Text = "Choose where to save the clean Excel...";
            var outputPath = await AskForSaveLocationAsync(parsed.fileName, ".xlsx")
                ?? Path.Combine(GetFallbackDirectory(), parsed.fileName);
            _outputDirectory = Path.GetDirectoryName(outputPath) ?? "";

            // 3. generate the workbook and write it
            StatusLabel.Text = "Converting...";
            var rows = await Task.Run(() =>
            {
                var built = TemplateWriter.BuildRows(parsed.report.Records);
                using var ms = new MemoryStream();
                TemplateWriter.Write(parsed.report.Records, ms);
                File.WriteAllBytes(outputPath, ms.ToArray());
                return built;
            });

            var present = rows.Count(r => r.Remark == TemplateSpec.RemarkPresent);
            var absent = rows.Count(r => r.Remark == TemplateSpec.RemarkAbsent);
            StatTotal.Text = $"{rows.Count}";
            StatPresent.Text = $"{present}";
            StatAbsent.Text = $"{absent}";
            SavedLabel.Text = $"Saved as {Path.GetFileName(outputPath)}  ·  {outputPath}";
            _convertedRows = rows;
            BuildTable(rows);

            _dashboard = AttendanceAnalytics.Build(parsed.report.Records);
            _sourceFileName = Path.GetFileName(outputPath);
            BuildDashboard();

            ShowTableTab();
            ConvertSection.IsVisible = false;
            ResultsSection.IsVisible = true;
            _selectedFile = null;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Conversion failed: {ex.Message}";
        }
        finally
        {
            Spinner.IsVisible = false;
            Spinner.IsRunning = false;
            ConvertBtn.IsEnabled = _selectedFile != null;
            PickFileBtn.IsEnabled = true;
        }
    }

    /// <summary>Windows: native "Save as" dialog so the user picks their own location.
    /// Elsewhere: null (the caller uses the fallback directory).</summary>
    private async Task<string?> AskForSaveLocationAsync(string fileName, string extension)
    {
#if WINDOWS
        if (this.Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow));
            picker.SuggestedFileName = Path.GetFileNameWithoutExtension(fileName);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
            picker.DefaultFileExtension = extension;
            picker.FileTypeChoices.Add(
                extension == ".pdf" ? "PDF report" : "Excel workbook",
                new List<string> { extension });

            var file = await picker.PickSaveFileAsync();
            return file?.Path;
        }
#endif
        return null;
    }

    private string GetFallbackDirectory() =>
        string.IsNullOrEmpty(_outputDirectory) ? FileSystem.AppDataDirectory : _outputDirectory;

    private void OnConvertAnotherClicked(object? sender, EventArgs e)
    {
        ResultsSection.IsVisible = false;
        ConvertSection.IsVisible = true;
        FileNameLabel.Text = "";
        StatusLabel.Text = "";
        ConvertBtn.IsEnabled = false;
        _convertedRows = null;
        _dashboard = null;
        ShowTableTab();
    }

    // --- tabs ---

    private void OnTabTableClicked(object? sender, EventArgs e) => ShowTableTab();

    private void OnTabDashboardClicked(object? sender, EventArgs e) => ShowDashboardTab();

    private void ShowTableTab()
    {
        TableCard.IsVisible = true;
        DashboardSection.IsVisible = false;
        SavePdfBtn.IsVisible = false;
        TabTableBtn.BackgroundColor = GetThemeColor("SuccessLight");
        TabTableBtn.TextColor = Colors.White;
        SetThemeColor(TabDashboardBtn, Button.BackgroundColorProperty, "SurfaceLight", "SurfaceDark");
        SetThemeColor(TabDashboardBtn, Button.TextColorProperty, "TextBodyLight", "TextBodyDark");
    }

    private void ShowDashboardTab()
    {
        TableCard.IsVisible = false;
        DashboardSection.IsVisible = true;
        SavePdfBtn.IsVisible = true;
        TabDashboardBtn.BackgroundColor = GetThemeColor("SuccessLight");
        TabDashboardBtn.TextColor = Colors.White;
        SetThemeColor(TabTableBtn, Button.BackgroundColorProperty, "SurfaceLight", "SurfaceDark");
        SetThemeColor(TabTableBtn, Button.TextColorProperty, "TextBodyLight", "TextBodyDark");
    }

    // --- dashboard ---

    private void BuildDashboard()
    {
        if (_dashboard is null) return;
        var d = _dashboard;

        DashEmployees.Text = $"{d.EmployeeCount}";
        DashAvgIn.Text = d.AverageIn ?? "-";
        DashAvgOut.Text = d.AverageOut ?? "-";
        DashAvgHours.Text = d.AverageHours ?? "-";
        DashStatus.Text = "";

        BuildChart(d);
        BuildEmployeeTable(d);
    }

    private void BuildChart(Dashboard d)
    {
        var grid = new Grid { VerticalOptions = LayoutOptions.End };
        var max = Math.Max(1, d.Days.Max(x => Math.Max(x.Present, x.Absent)));

        foreach (var day in d.Days)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(18));
            var column = new VerticalStackLayout { VerticalOptions = LayoutOptions.End, Spacing = 0 };
            var presentBar = new BoxView
            {
                HeightRequest = Math.Max(1, 100.0 * day.Present / max),
                WidthRequest = 14,
                HorizontalOptions = LayoutOptions.Center,
            };
            SetThemeColor(presentBar, BoxView.ColorProperty, "SuccessLight", "SuccessDark");
            column.Add(presentBar);
            if (day.Absent > 0)
            {
                column.Add(new BoxView
                {
                    Color = Color.FromArgb("#FCA5A5"),
                    HeightRequest = Math.Max(1, 100.0 * day.Absent / max),
                    WidthRequest = 14,
                    HorizontalOptions = LayoutOptions.Center,
                });
            }
            var dayLabel = new Label
            {
                Text = day.Date.Day.ToString(CultureInfo.InvariantCulture),
                FontSize = 8,
                HorizontalTextAlignment = TextAlignment.Center,
                Padding = new Thickness(0, 3, 0, 0),
            };
            SetThemeColor(dayLabel, Label.TextColorProperty, "TextSecondaryLight", "TextSecondaryDark");
            column.Add(dayLabel);
            grid.Add(column, grid.ColumnDefinitions.Count - 1);
        }
        DashChartGrid.Children.Clear();
        DashChartGrid.Add(grid);
        Grid.SetColumn(grid, 0);
    }

    private void BuildEmployeeTable(Dashboard d)
    {
        var headers = new[] { "ID", "Name", "Present", "Absent", "In only", "Avg in", "Avg out", "Avg hrs", "Rate" };
        string[] Row(EmployeeSummary e) => new[]
        {
            e.Id, e.Name,
            e.PresentDays.ToString(CultureInfo.InvariantCulture),
            e.AbsentDays.ToString(CultureInfo.InvariantCulture),
            e.InPunchOnlyDays.ToString(CultureInfo.InvariantCulture),
            e.AverageIn ?? "-", e.AverageOut ?? "-", e.AverageHours ?? "-",
            $"{e.AttendanceRate:0}%",
        };

        var widths = new double[headers.Length];
        for (var c = 0; c < headers.Length; c++)
        {
            var longest = d.Employees.Select(e => Row(e)[c].Length).Prepend(headers[c].Length).DefaultIfEmpty(0).Max();
            widths[c] = Math.Clamp(longest * 7.4 + 16, 40, 220);
        }

        View RowView(string[] cells, bool header, bool alt)
        {
            var g = new Grid();
            if (header)
                g.BackgroundColor = GetThemeColor("SuccessLight");
            else
                SetThemeColor(g, Grid.BackgroundColorProperty,
                    alt ? "SurfaceAltLight" : "SurfaceLight",
                    alt ? "SurfaceAltDark" : "SurfaceDark");

            for (var c = 0; c < headers.Length; c++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition(widths[c]));
                var label = new Label
                {
                    Text = cells[c],
                    FontSize = 12,
                    FontAttributes = header ? FontAttributes.Bold : FontAttributes.None,
                    TextColor = header ? Colors.White : GetThemeColor("TextBodyLight"),
                    Padding = new Thickness(6, header ? 10 : 8),
                    HorizontalTextAlignment = c == 1 ? TextAlignment.Start : TextAlignment.Center,
                    VerticalTextAlignment = TextAlignment.Center,
                };
                if (!header)
                    SetThemeColor(label, Label.TextColorProperty, "TextBodyLight", "TextBodyDark");
                g.Add(label, c);
            }
            return g;
        }

        DashEmployeeTable.Clear();
        DashEmployeeTable.WidthRequest = widths.Sum() + 2;
        DashEmployeeTable.Add(RowView(headers, header: true, alt: false));
        foreach (var (e, i) in d.Employees.Select((e, i) => (e, i)))
        {
            DashEmployeeTable.Add(RowView(Row(e), header: false, alt: i % 2 == 1));
        }
    }

    // --- save dashboard as PDF ---

    private async void OnSavePdfClicked(object? sender, EventArgs e)
    {
        if (_dashboard is null) return;

        SavePdfBtn.IsEnabled = false;
        DashStatus.Text = "Preparing PDF...";

        try
        {
            var suggested = $"Attendance Dashboard {_dashboard.From:MMMM yyyy}.pdf";
            var bytes = await Task.Run(() => DashboardPdf.Build(_dashboard, _sourceFileName));

            var outputPath = await AskForSaveLocationAsync(suggested, ".pdf")
                ?? Path.Combine(GetFallbackDirectory(), suggested);

            await Task.Run(() => File.WriteAllBytes(outputPath, bytes));
            DashStatus.Text = $"Dashboard saved to: {outputPath}";
        }
        catch (TypeInitializationException)
        {
            // QuestPDF's native renderer is unavailable on this platform (e.g. Android);
            // the Windows build - the deployment target - is unaffected.
            DashStatus.Text = "PDF export is supported in the Windows version of the app.";
        }
        catch (Exception ex)
        {
            DashStatus.Text = $"Could not save the PDF: {ex.Message}";
        }
        finally
        {
            SavePdfBtn.IsEnabled = true;
        }
    }

    private Grid? _headerRow;

    /// <summary>Presentation wrapper: the template row plus its zebra-stripe colour.</summary>
    private sealed record DisplayRow(TemplateRow Row, bool Alt)
    {
        public Color RowColor => Application.Current?.RequestedTheme == AppTheme.Dark
            ? Alt ? Color.FromArgb("#273449") : Color.FromArgb("#1F2937")
            : Alt ? Color.FromArgb("#F4F8F7") : Colors.White;
    }

    /// <summary>Renders the header from the template spec and feeds the rows to the
    /// virtualised list. Column widths are computed from the actual content, so nothing
    /// is truncated and no width is written in UI code.</summary>
    private void BuildTable(IReadOnlyList<TemplateRow> rows)
    {
        var widths = ComputeColumnWidths(rows);
        TableGrid.WidthRequest = widths.Sum() + 2;

        if (_headerRow is null)
        {
            _headerRow = BuildTableRow(TemplateSpec.Headers, widths, header: true);
            Grid.SetRow(_headerRow, 0);
            TableGrid.Add(_headerRow);
        }

        ItemsView.ItemTemplate = new DataTemplate(() =>
        {
            var grid = new Grid();
            grid.SetBinding(Grid.BackgroundColorProperty, nameof(DisplayRow.RowColor));
            for (var c = 0; c < widths.Length; c++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition(widths[c]));
                var left = Array.IndexOf(TemplateSpec.PreviewLeftAlignedColumns, c) >= 0;
                var label = new Label
                {
                    FontSize = 12,
                    Padding = new Thickness(6, 8),
                    HorizontalTextAlignment = left ? TextAlignment.Start : TextAlignment.Center,
                    LineBreakMode = LineBreakMode.NoWrap,
                    VerticalTextAlignment = TextAlignment.Center,
                };
                SetThemeColor(label, Label.TextColorProperty, "TextBodyLight", "TextBodyDark");
                label.SetBinding(Label.TextProperty, $"{nameof(DisplayRow.Row)}.{nameof(TemplateRow.Cells)}[{c}]");
                Grid.SetColumn(label, c);
                grid.Add(label);
            }
            return new ViewCell { View = grid };
        });
        ItemsView.ItemsSource = rows.Select((r, i) => new DisplayRow(r, i % 2 == 1)).ToList();
    }

    private static double[] ComputeColumnWidths(IReadOnlyList<TemplateRow> rows)
    {
        var widths = new double[TemplateSpec.Headers.Length];
        for (var c = 0; c < widths.Length; c++)
        {
            var headerLength = TemplateSpec.Headers[c].Length;
            var longest = rows.Count == 0
                ? headerLength
                : rows.Select(r => r.Cells[c].Length).Prepend(headerLength).Max();
            widths[c] = Math.Clamp(
                longest * TemplateSpec.PreviewCharWidth + TemplateSpec.PreviewColumnPadding,
                TemplateSpec.PreviewMinColumnWidth,
                TemplateSpec.PreviewMaxColumnWidth);
        }
        return widths;
    }

    private static Grid BuildTableRow(IReadOnlyList<string> cells, double[] widths, bool header)
    {
        var grid = new Grid();
        if (header)
            grid.BackgroundColor = GetThemeColor("SuccessLight");
        else
            SetThemeColor(grid, Grid.BackgroundColorProperty, "SurfaceLight", "SurfaceDark");

        for (var c = 0; c < widths.Length; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(widths[c]));
            var label = new Label
            {
                Text = c < cells.Count ? cells[c] : "",
                FontSize = 12,
                FontAttributes = header ? FontAttributes.Bold : FontAttributes.None,
                TextColor = header ? Colors.White : GetThemeColor("TextBodyLight"),
                Padding = new Thickness(6, header ? 10 : 8),
                HorizontalTextAlignment = Array.IndexOf(TemplateSpec.PreviewLeftAlignedColumns, c) >= 0
                    ? TextAlignment.Start
                    : TextAlignment.Center,
                LineBreakMode = LineBreakMode.NoWrap,
                VerticalTextAlignment = TextAlignment.Center,
            };
            if (!header)
                SetThemeColor(label, Label.TextColorProperty, "TextBodyLight", "TextBodyDark");
            Grid.SetRow(label, 0);
            Grid.SetColumn(label, c);
            grid.Add(label);
        }
        return grid;
    }

    private static Color GetThemeColor(string key) => (Color)Application.Current!.Resources[key];

    private static void SetThemeColor(VisualElement view, BindableProperty property, string lightKey, string darkKey) =>
        view.SetAppThemeColor(property, GetThemeColor(lightKey), GetThemeColor(darkKey));
}
