using System.Globalization;
using AttendanceCleaner.Core;

namespace AttendanceCleaner;

public partial class MainPage : ContentPage
{
    private FileResult? _selectedFile;

    public MainPage()
    {
        InitializeComponent();
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
                using (var reader = new StreamReader(stream))
                {
                    html = await reader.ReadToEndAsync();
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
            var outputPath = await AskForSaveLocationAsync(parsed.fileName)
                ?? Path.Combine(GetFallbackDirectory(), parsed.fileName);

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
            var inOnly = rows.Count(r => r.Remark == TemplateSpec.RemarkInPunchOnly);

            SummaryLabel.Text = $"{rows.Count} rows · {present} present · {absent} absent"
                + (inOnly > 0 ? $" · {inOnly} in-punch-only" : "");
            SavedLabel.Text = $"{Path.GetFileName(outputPath)}";
            StatusLabel.Text = $"Saved to: {outputPath}";
            BuildTable(rows);

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
    /// Elsewhere: null (the caller saves next to the input file).</summary>
    private async Task<string?> AskForSaveLocationAsync(string fileName)
    {
#if WINDOWS
        if (this.Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window nativeWindow)
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow));
            picker.SuggestedFileName = Path.GetFileNameWithoutExtension(fileName);
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
            picker.DefaultFileExtension = ".xlsx";
            picker.FileTypeChoices.Add("Excel workbook", new List<string> { ".xlsx" });

            var file = await picker.PickSaveFileAsync();
            return file?.Path;
        }
#endif
        return null;
    }

    private string GetFallbackDirectory()
    {
        var directory = Path.GetDirectoryName(_selectedFile?.FullPath);
        return string.IsNullOrEmpty(directory) ? FileSystem.AppDataDirectory : directory;
    }

    private void OnConvertAnotherClicked(object? sender, EventArgs e)
    {
        ResultsSection.IsVisible = false;
        ConvertSection.IsVisible = true;
        FileNameLabel.Text = "";
        StatusLabel.Text = "";
        ConvertBtn.IsEnabled = false;
    }

    /// <summary>Renders the header from the template spec and feeds the rows to the
    /// virtualised list — nothing about the column layout is written in UI code.</summary>
    private void BuildTable(IReadOnlyList<TemplateRow> rows)
    {
        var widths = TemplateSpec.PreviewColumnWidths;
        TableGrid.WidthRequest = widths.Sum() + 2;

        var header = BuildTableRow(TemplateSpec.Headers, header: true);
        Grid.SetRow(header, 0);
        TableGrid.Add(header);

        ItemsView.ItemsSource = rows;
    }

    private static Grid BuildTableRow(IReadOnlyList<string> cells, bool header)
    {
        var widths = TemplateSpec.PreviewColumnWidths;
        var grid = new Grid { BackgroundColor = header ? Colors.LightGray : Colors.White };

        for (var c = 0; c < widths.Length; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(widths[c]));
            var label = new Label
            {
                Text = c < cells.Count ? cells[c] : "",
                FontSize = 12,
                FontAttributes = header ? FontAttributes.Bold : FontAttributes.None,
                Padding = new Thickness(4, header ? 8 : 7),
                HorizontalTextAlignment = Array.IndexOf(TemplateSpec.PreviewLeftAlignedColumns, c) >= 0
                    ? TextAlignment.Start
                    : TextAlignment.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
            };
            Grid.SetRow(label, 0);
            Grid.SetColumn(label, c);
            grid.Add(label);
        }
        return grid;
    }
}
