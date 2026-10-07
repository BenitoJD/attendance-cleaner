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
        StatusLabel.Text = "Converting...";

        try
        {
            var result = await Task.Run(async () =>
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
                        "This file is not a recognised attendance export. Please upload the .xls file downloaded from the attendance software.");
                }

                var report = AttendanceParser.Parse(html);
                var dates = report.Records.Select(r => r.Date).Distinct().OrderBy(d => d).ToList();
                if (dates.Count == 0)
                {
                    throw new InvalidDataException("No attendance rows were found in this file.");
                }

                var isMonthly = ReportTypePicker.SelectedIndex == 1;
                var fileName = isMonthly
                    ? $"Attendance_{dates[0]:MMMM 'yyyy'}_Template.xlsx"
                    : $"Attendance_{dates[0]:dd-MM-yyyy}_Template.xlsx";

                var directory = Path.GetDirectoryName(_selectedFile.FullPath);
                if (string.IsNullOrEmpty(directory)) directory = FileSystem.AppDataDirectory;
                var outputPath = Path.Combine(directory, fileName);

                TemplateWriter.WriteToFile(report.Records, outputPath);

                return (report, dates, outputPath);
            });

            var scope = result.dates.Count > 1
                ? $"{result.dates.Count} days ({result.dates[0]:dd-MM-yyyy} to {result.dates[^1]:dd-MM-yyyy})"
                : $"{result.dates[0]:dd-MM-yyyy}";
            StatusLabel.Text = $"Done: {result.report.Records.Count} rows for {scope}. Saved to:\n{result.outputPath}";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Conversion failed: {ex.Message}";
        }
        finally
        {
            ConvertBtn.IsEnabled = true;
        }
    }
}
