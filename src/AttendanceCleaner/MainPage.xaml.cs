using System.Globalization;
using System.Text;
using AttendanceCleaner.Core;
using RoundRectangle = Microsoft.Maui.Controls.Shapes.RoundRectangle;

namespace AttendanceCleaner;

public partial class MainPage : ContentPage
{
    private readonly IndiaTimeClock _indiaTimeClock = new();
    private readonly HolidayCalendarStore _holidayCalendarStore;
    private FileResult? _selectedFile;
    private Dashboard? _dashboard;
    private IReadOnlyList<TemplateRow>? _convertedRows;
    private string _sourceFileName = "";
    private string _outputDirectory = "";
    private ReportCategory? _selectedCategory;
    private MonthlyTemplateKind? _selectedMonthlyTemplate;
    private List<HolidayEntry> _holidayEntries = new();
    private IDispatcherTimer? _istTimer;
    private DateTimeOffset _nextIstSyncUtc = DateTimeOffset.MinValue;
    private bool _istSyncInProgress;
    private bool _use24HourIstTime;

    private enum ReportCategory
    {
        Daily,
        Monthly,
    }

    public MainPage()
    {
        InitializeComponent();
        _holidayCalendarStore = new HolidayCalendarStore(GetHolidayCalendarPath());
        if (Application.Current is { } app)
            app.RequestedThemeChanged += OnRequestedThemeChanged;
        UpdateTemplateCardSelection();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        _istTimer ??= CreateIstTimer();
        _istTimer.Start();
        UpdateIstClock();
        if (DateTimeOffset.UtcNow >= _nextIstSyncUtc)
            _ = SynchronizeIstClockAsync();
    }

    protected override void OnDisappearing()
    {
        _istTimer?.Stop();
        base.OnDisappearing();
    }

    private IDispatcherTimer CreateIstTimer()
    {
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += OnIstTimerTick;
        return timer;
    }

    private void OnIstTimerTick(object? sender, EventArgs e)
    {
        UpdateIstClock();
        if (!_istSyncInProgress && DateTimeOffset.UtcNow >= _nextIstSyncUtc)
            _ = SynchronizeIstClockAsync();
    }

    private void UpdateIstClock()
    {
        var now = _indiaTimeClock.CurrentTime;
        IstTimeLabel.Text = now.ToString(_use24HourIstTime ? "HH:mm:ss" : "hh:mm:ss tt", CultureInfo.InvariantCulture);
        IstDateLabel.Text = now.ToString("dddd, dd MMMM yyyy", CultureInfo.InvariantCulture);
    }

    private void OnIstTimeTapped(object? sender, TappedEventArgs e)
    {
        _use24HourIstTime = !_use24HourIstTime;
        UpdateIstClock();
    }

    private async Task SynchronizeIstClockAsync()
    {
        if (_istSyncInProgress) return;

        _istSyncInProgress = true;
        _nextIstSyncUtc = DateTimeOffset.UtcNow.AddMinutes(15);
        try
        {
            await _indiaTimeClock.SynchronizeAsync();
            UpdateIstClock();
        }
        finally
        {
            _istSyncInProgress = false;
        }
    }

    private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateTemplateCardSelection();
            UpdateMonthlyTemplateSelection();
            if (_convertedRows is not null)
                BuildTable(_convertedRows);
            if (_dashboard is not null)
                BuildDashboard();
            if (HolidayManagerCard.IsVisible)
                RenderHolidayRows();

            if (DashboardSection.IsVisible)
                ShowDashboardTab();
            else
                ShowTableTab();
        });
    }

    private async void OnDailyTemplateClicked(object? sender, EventArgs e) =>
        await SelectTemplateAsync(ReportCategory.Daily);

    private void OnMonthlyTemplateClicked(object? sender, EventArgs e)
    {
        ResetSelectedFile();

        _selectedCategory = ReportCategory.Monthly;
        _selectedMonthlyTemplate = null;
        MonthlyOptionsPanel.IsVisible = true;
        HolidayManagerCard.IsVisible = false;
        UploadCard.IsVisible = false;
        UpdateTemplateCardSelection();
        UpdateMonthlyTemplateSelection();
    }

    private async Task SelectTemplateAsync(ReportCategory category)
    {
        if (category == ReportCategory.Monthly)
        {
            OnMonthlyTemplateClicked(null, EventArgs.Empty);
            return;
        }

        if (_selectedCategory != category)
            ResetSelectedFile();

        _selectedCategory = category;
        _selectedMonthlyTemplate = null;
        MonthlyOptionsPanel.IsVisible = false;
        HolidayManagerCard.IsVisible = false;
        SelectedTemplateLabel.Text = "Daily template selected · one-day report";
        UploadCard.IsVisible = true;
        UpdateTemplateCardSelection();
        await PickFileAsync();
    }

    private async void OnDutyOvertimeTemplateClicked(object? sender, EventArgs e) =>
        await SelectMonthlyTemplateAsync(MonthlyTemplateKind.DutyAndOvertime);

    private async void OnInOutTemplateClicked(object? sender, EventArgs e) =>
        await SelectMonthlyTemplateAsync(MonthlyTemplateKind.InAndOut);

    private async Task SelectMonthlyTemplateAsync(MonthlyTemplateKind kind)
    {
        if (_selectedCategory != ReportCategory.Monthly || _selectedMonthlyTemplate != kind)
            ResetSelectedFile();

        _selectedCategory = ReportCategory.Monthly;
        _selectedMonthlyTemplate = kind;
        MonthlyOptionsPanel.IsVisible = true;
        HolidayManagerCard.IsVisible = false;
        SelectedTemplateLabel.Text = kind == MonthlyTemplateKind.DutyAndOvertime
            ? "Monthly template selected · Duty + OT summary"
            : "Monthly template selected · IN / OUT punch report";
        UploadCard.IsVisible = true;
        UpdateTemplateCardSelection();
        UpdateMonthlyTemplateSelection();
        await PickFileAsync();
    }

    private void ResetSelectedFile()
    {
        _selectedFile = null;
        FileNameLabel.Text = "";
        StatusLabel.Text = "";
        PickFileBtn.Text = "Choose report file";
        ConvertBtn.IsEnabled = false;
    }

    private void UpdateMonthlyTemplateSelection()
    {
        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        var selectedColor = GetThemeColor(dark ? "SuccessDark" : "SuccessLight");
        var borderColor = GetThemeColor(dark ? "BorderDark" : "BorderLight");
        var dutySelected = _selectedMonthlyTemplate == MonthlyTemplateKind.DutyAndOvertime;
        var inOutSelected = _selectedMonthlyTemplate == MonthlyTemplateKind.InAndOut;
        DutyOvertimeCard.Stroke = new SolidColorBrush(dutySelected ? selectedColor : borderColor);
        DutyOvertimeCard.StrokeThickness = dutySelected ? 2 : 1;
        SetThemeColor(DutyOvertimeCard, Border.BackgroundColorProperty,
            dutySelected ? "AccentSurfaceLight" : "SurfaceLight",
            dutySelected ? "AccentSurfaceDark" : "SurfaceDark");
        InOutCard.Stroke = new SolidColorBrush(inOutSelected ? selectedColor : borderColor);
        InOutCard.StrokeThickness = inOutSelected ? 2 : 1;
        SetThemeColor(InOutCard, Border.BackgroundColorProperty,
            inOutSelected ? "AccentSurfaceLight" : "SurfaceLight",
            inOutSelected ? "AccentSurfaceDark" : "SurfaceDark");
    }

    private void OnManageHolidaysClicked(object? sender, EventArgs e)
    {
        try
        {
            _holidayEntries = _holidayCalendarStore.LoadOrSeed().ToList();
            RenderHolidayRows();
            HolidaySaveStatus.Text = $"{_holidayEntries.Count} holidays saved.";
        }
        catch (Exception ex)
        {
            HolidaySaveStatus.Text = $"Could not load the holiday calendar: {ex.Message}";
            return;
        }

        MonthlyOptionsPanel.IsVisible = false;
        UploadCard.IsVisible = false;
        HolidayManagerCard.IsVisible = true;
        Dispatcher.Dispatch(() =>
        {
            foreach (var row in HolidayRowsLayout.Children.OfType<Grid>())
            foreach (var cell in row.Children.OfType<Border>())
                cell.Content?.Unfocus();
        });
    }

    private void OnHolidayManagerBackClicked(object? sender, EventArgs e)
    {
        HolidayManagerCard.IsVisible = false;
        MonthlyOptionsPanel.IsVisible = true;
        UploadCard.IsVisible = _selectedMonthlyTemplate is not null;
    }

    private void OnAddHolidayClicked(object? sender, EventArgs e)
    {
        _holidayEntries.Add(HolidayEntry.Create(DateOnly.FromDateTime(DateTime.Today), "New holiday", "Corporate Office"));
        SaveHolidayCalendar();
        RenderHolidayRows();
    }

    private void RenderHolidayRows()
    {
        HolidayRowsLayout.Children.Clear();
        NoHolidayRowsLabel.IsVisible = _holidayEntries.Count == 0;

        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        var borderColor = GetThemeColor(dark ? "BorderDark" : "BorderLight");
        var rowIndex = 0;

        foreach (var holiday in HolidayCalendarStore.Sort(_holidayEntries))
        {
            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(new GridLength(112)),
                    new ColumnDefinition(new GridLength(180)),
                    new ColumnDefinition(new GridLength(170)),
                    new ColumnDefinition(new GridLength(62)),
                },
                ColumnSpacing = 0,
                HeightRequest = 48,
            };

            var datePicker = new DatePicker
            {
                Date = holiday.Date.ToDateTime(TimeOnly.MinValue),
                Format = "dd/MM/yyyy",
                FontSize = 12,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Center,
                BackgroundColor = Colors.Transparent,
            };
            SetThemeColor(datePicker, DatePicker.TextColorProperty, "TextBodyLight", "TextBodyDark");

            var categoryPicker = new Picker
            {
                Title = "Category",
                ItemsSource = HolidayCategoryOptions.Select(option => option.Label).ToArray(),
                SelectedIndex = Array.FindIndex(HolidayCategoryOptions,
                    option => option.Value.Equals(holiday.Category, StringComparison.OrdinalIgnoreCase)),
                FontSize = 11,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Center,
                BackgroundColor = Colors.Transparent,
            };
            SetThemeColor(categoryPicker, Picker.TextColorProperty, "TextBodyLight", "TextBodyDark");

            var nameEntry = new Entry
            {
                Text = holiday.Name,
                Placeholder = "Holiday name",
                FontSize = 13,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Center,
                BackgroundColor = Colors.Transparent,
            };
            SetThemeColor(nameEntry, Entry.TextColorProperty, "TextBodyLight", "TextBodyDark");
            SetThemeColor(nameEntry, Entry.PlaceholderColorProperty, "TextSecondaryLight", "TextSecondaryDark");

            var removeButton = new Button
            {
                Text = "Delete",
                FontSize = 10,
                Padding = new Thickness(2, 0),
                CornerRadius = 0,
                BackgroundColor = Colors.Transparent,
                TextColor = GetThemeColor("DangerLight"),
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
            };
            SetThemeColor(removeButton, Button.TextColorProperty, "DangerLight", "DangerDark");
            SemanticProperties.SetDescription(removeButton, $"Delete {holiday.Name}");

            Border Cell(View content)
            {
                var cell = new Border
                {
                    Stroke = new SolidColorBrush(borderColor),
                    StrokeThickness = 0.75,
                    Padding = new Thickness(4, 2),
                    Content = content,
                };
                SetThemeColor(cell, Border.BackgroundColorProperty,
                    rowIndex % 2 == 0 ? "SurfaceLight" : "SurfaceAltLight",
                    rowIndex % 2 == 0 ? "SurfaceDark" : "SurfaceAltDark");
                return cell;
            }

            row.Add(Cell(datePicker), 0);
            row.Add(Cell(nameEntry), 1);
            row.Add(Cell(categoryPicker), 2);
            row.Add(Cell(removeButton), 3);

            datePicker.DateSelected += (_, args) =>
            {
                if (args.NewDate.HasValue)
                    UpdateHoliday(holiday.Id, entry => entry with { Date = DateOnly.FromDateTime(args.NewDate.Value) });
            };
            categoryPicker.SelectedIndexChanged += (_, _) =>
            {
                if (categoryPicker.SelectedIndex >= 0)
                {
                    var category = HolidayCategoryOptions[categoryPicker.SelectedIndex].Value;
                    UpdateHoliday(holiday.Id, entry => entry with { Category = category });
                }
            };
            nameEntry.TextChanged += (_, args) => UpdateHoliday(holiday.Id, entry => entry with { Name = args.NewTextValue ?? "" });
            removeButton.Clicked += (_, _) =>
            {
                _holidayEntries.RemoveAll(entry => entry.Id == holiday.Id);
                SaveHolidayCalendar();
                RenderHolidayRows();
            };
            HolidayRowsLayout.Children.Add(row);
            rowIndex++;
        }
    }

    private void UpdateHoliday(string id, Func<HolidayEntry, HolidayEntry> update)
    {
        var index = _holidayEntries.FindIndex(entry => entry.Id == id);
        if (index < 0) return;
        _holidayEntries[index] = update(_holidayEntries[index]);
        SaveHolidayCalendar();
    }

    private void SaveHolidayCalendar()
    {
        try
        {
            _holidayCalendarStore.Save(_holidayEntries);
            HolidaySaveStatus.Text = $"Saved · {_holidayEntries.Count} holidays";
        }
        catch (Exception ex)
        {
            HolidaySaveStatus.Text = $"Could not save the holiday calendar: {ex.Message}";
        }
    }

    private static string GetHolidayCalendarPath()
    {
#if WINDOWS
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Attendance Cleaner", "holiday-calendar.json");
#else
        return Path.Combine(FileSystem.AppDataDirectory, "holiday-calendar.json");
#endif
    }

    private static readonly (string Label, string Value)[] HolidayCategoryOptions =
    {
        ("Corporate office", "Corporate Office"),
        ("Tamil Nadu", "Tamil Nadu"),
        ("Optional · corporate", "Optional · Corporate Office"),
        ("Optional · Tamil Nadu", "Optional · Tamil Nadu"),
        ("Working Saturday", "Working Saturday · Corporate Office"),
    };

    private void UpdateTemplateCardSelection()
    {
        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        var selectedColor = GetThemeColor(dark ? "SuccessDark" : "SuccessLight");
        var borderColor = GetThemeColor(dark ? "BorderDark" : "BorderLight");

        DailyTemplateCard.Stroke = new SolidColorBrush(
            _selectedCategory == ReportCategory.Daily ? selectedColor : borderColor);
        DailyTemplateCard.StrokeThickness = _selectedCategory == ReportCategory.Daily ? 2 : 1;
        SetThemeColor(DailyTemplateCard, Border.BackgroundColorProperty,
            _selectedCategory == ReportCategory.Daily ? "AccentSurfaceLight" : "SurfaceLight",
            _selectedCategory == ReportCategory.Daily ? "AccentSurfaceDark" : "SurfaceDark");
        MonthlyTemplateCard.Stroke = new SolidColorBrush(
            _selectedCategory == ReportCategory.Monthly ? selectedColor : borderColor);
        MonthlyTemplateCard.StrokeThickness = _selectedCategory == ReportCategory.Monthly ? 2 : 1;
        SetThemeColor(MonthlyTemplateCard, Border.BackgroundColorProperty,
            _selectedCategory == ReportCategory.Monthly ? "AccentSurfaceLight" : "SurfaceLight",
            _selectedCategory == ReportCategory.Monthly ? "AccentSurfaceDark" : "SurfaceDark");
    }

    private async void OnPickFileClicked(object? sender, EventArgs e)
    {
        if (_selectedCategory is null) return;
        await PickFileAsync();
    }

    private async Task PickFileAsync()
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
            PickFileBtn.Text = "Choose another report";
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
        if (_selectedFile is null || _selectedCategory is null) return;
        var selectedCategory = _selectedCategory.Value;
        var selectedMonthlyTemplate = _selectedMonthlyTemplate;
        if (selectedCategory == ReportCategory.Monthly && selectedMonthlyTemplate is null)
        {
            StatusLabel.Text = "Choose one of the monthly templates first.";
            return;
        }

        ConvertBtn.IsEnabled = false;
        PickFileBtn.IsEnabled = false;
        DailyTemplateButton.IsEnabled = false;
        MonthlyTemplateButton.IsEnabled = false;
        MonthlyDutyButton.IsEnabled = false;
        MonthlyInOutButton.IsEnabled = false;
        ManageHolidaysButton.IsEnabled = false;
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
                var isMonthlyReport = report.Format is AttendanceExportFormat.MonthlyBlocks
                    or AttendanceExportFormat.MonthlyRows;
                if ((selectedCategory == ReportCategory.Monthly) != isMonthlyReport)
                {
                    throw new InvalidDataException(selectedCategory == ReportCategory.Monthly
                        ? "This is a daily report. Choose the Daily template, or upload a monthly report."
                        : "This is a monthly report. Choose the Monthly template, or upload a one-day report.");
                }

                var dates = report.Records.Select(r => r.Date).Distinct().OrderBy(d => d).ToList();
                if (dates.Count == 0)
                {
                    throw new InvalidDataException("No attendance rows were found in this file.");
                }

                // The selected template category determines the output file name.
                var fileName = string.Format(
                    CultureInfo.InvariantCulture,
                    selectedCategory == ReportCategory.Monthly
                        ? selectedMonthlyTemplate == MonthlyTemplateKind.DutyAndOvertime
                            ? TemplateSpec.MonthlyDutyOvertimeFileName
                            : TemplateSpec.MonthlyInOutFileName
                        : TemplateSpec.DailyFileName,
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
            var conversion = await Task.Run(() =>
            {
                var built = TemplateWriter.BuildRows(parsed.report.Records);
                using var ms = new MemoryStream();
                IReadOnlyList<MonthlyEmployeeSummary>? summaries = null;
                if (selectedCategory == ReportCategory.Monthly)
                {
                    var holidays = _holidayCalendarStore.LoadOrSeed();
                    var monthlyKind = selectedMonthlyTemplate!.Value;
                    MonthlyTemplateWriter.Write(parsed.report.Records, holidays, monthlyKind, ms);
                    summaries = MonthlyTemplateWriter.BuildSummaries(
                        parsed.report.Records, holidays, parsed.dates[0]);
                }
                else
                {
                    TemplateWriter.Write(parsed.report.Records, ms);
                }
                File.WriteAllBytes(outputPath, ms.ToArray());
                return (Rows: built, Summaries: summaries);
            });

            var rows = conversion.Rows;
            var present = conversion.Summaries is null
                ? rows.Count(r => r.Remark == TemplateSpec.RemarkPresent)
                : conversion.Summaries.Sum(summary => summary.PresentDays);
            var absent = conversion.Summaries is null
                ? rows.Count(r => r.Remark == TemplateSpec.RemarkAbsent)
                : conversion.Summaries.Sum(summary => summary.AbsentDays);
            StatTotalLabel.Text = selectedCategory == ReportCategory.Monthly ? "employees" : "rows";
            StatTotal.Text = $"{conversion.Summaries?.Count ?? rows.Count}";
            StatPresent.Text = $"{present}";
            StatAbsent.Text = $"{absent}";
            var templateName = selectedCategory == ReportCategory.Monthly
                ? selectedMonthlyTemplate == MonthlyTemplateKind.DutyAndOvertime ? "Duty + OT" : "IN / OUT"
                : "Daily";
            SavedLabel.Text = $"{templateName} · saved as {Path.GetFileName(outputPath)}  ·  {outputPath}";
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
            DailyTemplateButton.IsEnabled = true;
            MonthlyTemplateButton.IsEnabled = true;
            MonthlyDutyButton.IsEnabled = true;
            MonthlyInOutButton.IsEnabled = true;
            ManageHolidaysButton.IsEnabled = true;
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
        UploadCard.IsVisible = false;
        MonthlyOptionsPanel.IsVisible = false;
        HolidayManagerCard.IsVisible = false;
        SelectedTemplateLabel.Text = "";
        _selectedCategory = null;
        _selectedMonthlyTemplate = null;
        ResetSelectedFile();
        UpdateTemplateCardSelection();
        UpdateMonthlyTemplateSelection();
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
        SetThemeColor(TabTableBtn, Button.BackgroundColorProperty, "ActionLight", "ActionDark");
        SetThemeColor(TabTableBtn, Button.TextColorProperty, "ActionTextLight", "ActionTextDark");
        SetThemeColor(TabDashboardBtn, Button.BackgroundColorProperty, "SurfaceLight", "SurfaceDark");
        SetThemeColor(TabDashboardBtn, Button.TextColorProperty, "TextBodyLight", "TextBodyDark");
    }

    private void ShowDashboardTab()
    {
        TableCard.IsVisible = false;
        DashboardSection.IsVisible = true;
        SavePdfBtn.IsVisible = true;
        SetThemeColor(TabDashboardBtn, Button.BackgroundColorProperty, "ActionLight", "ActionDark");
        SetThemeColor(TabDashboardBtn, Button.TextColorProperty, "ActionTextLight", "ActionTextDark");
        SetThemeColor(TabTableBtn, Button.BackgroundColorProperty, "SurfaceLight", "SurfaceDark");
        SetThemeColor(TabTableBtn, Button.TextColorProperty, "TextBodyLight", "TextBodyDark");
    }

    // --- dashboard ---

    private void BuildDashboard()
    {
        if (_dashboard is null) return;
        var d = _dashboard;

        DashEmployees.Text = $"{d.EmployeeCount}";
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
                SetThemeColor(g, Grid.BackgroundColorProperty, "ActionLight", "ActionDark");
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
                    TextColor = GetThemeColor(header ? "ActionTextLight" : "TextBodyLight"),
                    Padding = new Thickness(6, header ? 10 : 8),
                    HorizontalTextAlignment = c == 1 ? TextAlignment.Start : TextAlignment.Center,
                    VerticalTextAlignment = TextAlignment.Center,
                };
                if (header)
                    SetThemeColor(label, Label.TextColorProperty, "ActionTextLight", "ActionTextDark");
                else
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
            SetThemeColor(grid, Grid.BackgroundColorProperty, "ActionLight", "ActionDark");
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
                TextColor = GetThemeColor(header ? "ActionTextLight" : "TextBodyLight"),
                Padding = new Thickness(6, header ? 10 : 8),
                HorizontalTextAlignment = Array.IndexOf(TemplateSpec.PreviewLeftAlignedColumns, c) >= 0
                    ? TextAlignment.Start
                    : TextAlignment.Center,
                LineBreakMode = LineBreakMode.NoWrap,
                VerticalTextAlignment = TextAlignment.Center,
            };
            if (header)
                SetThemeColor(label, Label.TextColorProperty, "ActionTextLight", "ActionTextDark");
            else
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
