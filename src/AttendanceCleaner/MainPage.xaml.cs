using System.Globalization;
using System.Collections.ObjectModel;
using System.Text;
using AttendanceCleaner.Core;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using RoundRectangle = Microsoft.Maui.Controls.Shapes.RoundRectangle;

namespace AttendanceCleaner;

public partial class MainPage : ContentPage
{
    private readonly IndiaTimeClock _indiaTimeClock = new();
    private readonly HolidayCalendarStore _holidayCalendarStore;
    private FileResult? _selectedFile;
    private Dashboard? _dashboard;
    private IReadOnlyList<TemplateRow>? _convertedRows;
    private MonthlyWorkbookPreview? _monthlyWorkbookPreview;
    private string? _lastOutputPath;
    private MonthlySheetDrawable? _monthlySheetDrawable;
    private string _sourceFileName = "";
    private string _outputDirectory = "";
    private ReportCategory? _selectedCategory;
    private MonthlyTemplateKind? _selectedMonthlyTemplate;
    private ObservableCollection<HolidayEntry> _holidayEntries = new();
    private bool _holidaysLoaded;
    private bool _holidayOperationInProgress;
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
        using (var logoStream = typeof(MonthlyTemplateWriter).Assembly
            .GetManifestResourceStream("AttendanceCleaner.Core.DakshinakTemplateLogo.png"))
        {
            if (logoStream is not null)
            {
                using var logoBuffer = new MemoryStream();
                logoStream.CopyTo(logoBuffer);
                var logoBytes = logoBuffer.ToArray();
                MonthlyTemplateLogoPreview.Source = ImageSource.FromStream(
                    () => new MemoryStream(logoBytes, writable: false));
            }
        }
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
            if (_convertedRows is not null || _monthlyWorkbookPreview is not null)
                BuildTable(_convertedRows ?? Array.Empty<TemplateRow>());
            if (_dashboard is not null)
                BuildDashboard();

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

    private async void OnManageHolidaysClicked(object? sender, EventArgs e)
    {
        if (_holidayOperationInProgress) return;
        MonthlyOptionsPanel.IsVisible = false;
        UploadCard.IsVisible = false;
        ConvertSection.IsVisible = false;
        HolidaySection.IsVisible = true;
        HolidayManagerCard.IsVisible = true;
        HolidayList.IsVisible = _holidaysLoaded;
        SetHolidayBusy(true, showProgress: true);
        HolidaySaveStatus.Text = "Loading holidays…";
        try
        {
            if (!_holidaysLoaded)
            {
                var entries = await Task.Run(_holidayCalendarStore.LoadOrSeed);
                _holidayEntries = new ObservableCollection<HolidayEntry>(entries);
                HolidayList.ItemsSource = _holidayEntries;
                _holidaysLoaded = true;
                HolidayList.IsVisible = true;
            }
            HolidaySaveStatus.Text = $"{_holidayEntries.Count} holidays saved.";
            UpdateHolidayTableWidth();
        }
        catch (Exception ex)
        {
            HolidaySaveStatus.Text = $"Could not load the holiday calendar: {ex.Message}";
        }
        finally
        {
            SetHolidayBusy(false);
        }
    }

    private void OnHolidayManagerBackClicked(object? sender, EventArgs e)
    {
        if (_holidayOperationInProgress) return;
        HolidaySection.IsVisible = false;
        HolidayManagerCard.IsVisible = false;
        ConvertSection.IsVisible = true;
        MonthlyOptionsPanel.IsVisible = true;
        UploadCard.IsVisible = _selectedMonthlyTemplate is not null;
    }

    protected override bool OnBackButtonPressed()
    {
        if (HolidayDialog.IsVisible)
        {
            HolidayDialog.Cancel();
            return true;
        }
        if (HolidaySection.IsVisible)
        {
            OnHolidayManagerBackClicked(this, EventArgs.Empty);
            return true;
        }
        return base.OnBackButtonPressed();
    }

    private async void OnAddHolidayClicked(object? sender, EventArgs e) => await EditHolidayAsync(null);

    private async void OnEditHolidayClicked(object? sender, EventArgs e)
    {
        if (sender is BindableObject { BindingContext: HolidayEntry holiday })
            await EditHolidayAsync(holiday);
    }

    private async Task EditHolidayAsync(HolidayEntry? holiday)
    {
        if (_holidayOperationInProgress || !_holidaysLoaded) return;
        SetHolidayBusy(true);
        try
        {
            var updated = await HolidayDialog.ShowAsync(holiday,
                DateOnly.FromDateTime(_indiaTimeClock.CurrentTime.DateTime), HolidayCategoryOptions);
            if (updated is null) return;

            var snapshot = HolidayCalendarStore.Sort(
                _holidayEntries.Where(entry => entry.Id != updated.Id).Append(updated));
            if (!await SaveHolidayCalendarAsync(snapshot)) return;

            var destination = snapshot.ToList().FindIndex(entry => entry.Id == updated.Id);
            var previous = holiday is null ? -1 : _holidayEntries.IndexOf(holiday);
            if (previous < 0)
                _holidayEntries.Insert(destination, updated);
            else
            {
                _holidayEntries[previous] = updated;
                if (previous != destination) _holidayEntries.Move(previous, destination);
            }
        }
        finally
        {
            SetHolidayBusy(false);
        }
    }

    private async void OnDeleteHolidayClicked(object? sender, EventArgs e)
    {
        if (_holidayOperationInProgress || sender is not BindableObject { BindingContext: HolidayEntry holiday }) return;
        SetHolidayBusy(true);
        try
        {
            var confirmed = await DisplayAlertAsync("Delete holiday?",
                $"Delete “{holiday.Name}” on {holiday.Date:dd MMM yyyy}? This will remove it from your holiday calendar.",
                "Delete", "Cancel");
            if (!confirmed) return;

            var snapshot = _holidayEntries.Where(entry => entry.Id != holiday.Id).ToArray();
            if (await SaveHolidayCalendarAsync(snapshot))
                _holidayEntries.Remove(holiday);
        }
        finally
        {
            SetHolidayBusy(false);
        }
    }

    private async Task<bool> SaveHolidayCalendarAsync(IReadOnlyCollection<HolidayEntry> entries)
    {
        SetHolidayBusy(true, showProgress: true);
        HolidaySaveStatus.Text = "Saving…";
        try
        {
            await Task.Run(() => _holidayCalendarStore.Save(entries));
            HolidaySaveStatus.Text = $"Saved · {entries.Count} holidays";
            return true;
        }
        catch (Exception ex)
        {
            HolidaySaveStatus.Text = $"Could not save the holiday calendar: {ex.Message}";
            await DisplayAlertAsync("Could not save holiday", ex.Message, "OK");
            return false;
        }
    }

    private void SetHolidayBusy(bool busy, bool showProgress = false)
    {
        _holidayOperationInProgress = busy;
        AddHolidayButton.IsEnabled = !busy && _holidaysLoaded;
        HolidayBackButton.IsEnabled = !busy;
        HolidayList.IsEnabled = !busy && _holidaysLoaded;
        HolidayBusyIndicator.IsRunning = busy && showProgress;
        HolidayBusyIndicator.IsVisible = busy && showProgress;
    }

    private void OnHolidayTableSizeChanged(object? sender, EventArgs e) => UpdateHolidayTableWidth();

    private void UpdateHolidayTableWidth()
    {
        if (HolidayTableScroll.Width > 0)
        {
            var width = Math.Max(HolidayTableLayout.MinimumWidthRequest, HolidayTableScroll.Width);
            if (Math.Abs(HolidayTableLayout.WidthRequest - width) > 0.5)
                HolidayTableLayout.WidthRequest = width;
        }
    }

    private static string GetHolidayCalendarPath()
    {
#if WINDOWS
        // Keep the historical folder so existing user-managed holiday calendars survive the rebrand.
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Attendance Cleaner", "holiday-calendar.json");
#else
        return Path.Combine(FileSystem.AppDataDirectory, "holiday-calendar.json");
#endif
    }

    private static readonly (string Label, string Value)[] HolidayCategoryOptions =
    {
        ("Corporate office", MonthlyTemplateSpec.CorporateOfficeCategory),
        ("Tamil Nadu", MonthlyTemplateSpec.TamilNaduCategory),
        ("Optional · corporate", MonthlyTemplateSpec.OptionalCorporateCategory),
        ("Optional · Tamil Nadu", MonthlyTemplateSpec.OptionalTamilNaduCategory),
        ("Working Saturday", MonthlyTemplateSpec.WorkingSaturdayCategory),
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
            [DevicePlatform.WinUI] = new[] { ".xls" },
            [DevicePlatform.Android] = new[] { "application/vnd.ms-excel" },
            [DevicePlatform.iOS] = new[] { "com.microsoft.excel.xls" },
            [DevicePlatform.MacCatalyst] = new[] { "com.microsoft.excel.xls" },
        });

        try
        {
            var result = await FilePicker.PickAsync(new PickOptions
            {
                PickerTitle = "Choose the attendance report file",
                FileTypes = fileTypes,
            });
            if (result == null) return;

            if (!string.Equals(Path.GetExtension(result.FileName), ".xls", StringComparison.OrdinalIgnoreCase))
            {
                StatusLabel.Text = "Only .xls files are allowed. Choose the attendance report exported from your software.";
                return;
            }

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
        ConversionWarningLabel.Text = "";
        ConversionWarningLabel.IsVisible = false;

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

                var monthlyWarning = selectedCategory == ReportCategory.Monthly
                    && report.Format == AttendanceExportFormat.MonthlyRows
                    && report.Records.All(record => string.IsNullOrWhiteSpace(record.Status))
                    ? report.EmployeeTotals?.Any(total => total.AbsentDays is not null
                        || total.AttendedDays is not null
                        || total.LeaveDays is not null) == true
                        ? "Monthly totals use the report's employee counts. No daily leave/absence codes are available, so dates cannot be identified and totals may not match the calendar."
                        : "This report has no daily leave/absence codes or summary counts. Monthly absence and leave are inferred from punches and the calendar."
                    : "";

                return (report, dates, fileName, monthlyWarning);
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
                MonthlyWorkbookPreview? monthlyPreview = null;
                if (selectedCategory == ReportCategory.Monthly)
                {
                    var holidays = _holidayCalendarStore.LoadOrSeed();
                    var monthlyKind = selectedMonthlyTemplate!.Value;
                    MonthlyTemplateWriter.Write(parsed.report.Records, holidays, monthlyKind, ms,
                        parsed.report.EmployeeTotals);
                    var workbookBytes = ms.ToArray();
                    using var previewStream = new MemoryStream(workbookBytes, writable: false);
                    monthlyPreview = MonthlyTemplateWriter.ReadPreview(previewStream);
                    summaries = MonthlyTemplateWriter.BuildSummaries(
                        parsed.report.Records, holidays, parsed.dates[0], parsed.report.EmployeeTotals);
                    File.WriteAllBytes(outputPath, workbookBytes);
                }
                else
                {
                    TemplateWriter.Write(parsed.report.Records, ms);
                    File.WriteAllBytes(outputPath, ms.ToArray());
                }
                return (Rows: built, Summaries: summaries, MonthlyPreview: monthlyPreview);
            });

            var rows = conversion.Rows;
            StatTotalLabel.Text = selectedCategory == ReportCategory.Monthly ? "employees" : "rows";
            StatTotal.Text = $"{conversion.Summaries?.Count ?? rows.Count}";
            var templateName = selectedCategory == ReportCategory.Monthly
                ? selectedMonthlyTemplate == MonthlyTemplateKind.DutyAndOvertime ? "Duty + OT" : "IN / OUT"
                : "Daily";
            SavedLabel.Text = $"{templateName} · {Path.GetFileName(outputPath)}";
            SavedLocationLabel.Text = DeviceInfo.Platform == DevicePlatform.Android
                ? "Tap to share or save a copy"
                : "Tap to open the saved workbook";
            SavedPathLabel.Text = $"Saved at: {outputPath}";
            ConversionWarningLabel.Text = parsed.monthlyWarning;
            ConversionWarningLabel.IsVisible = !string.IsNullOrWhiteSpace(parsed.monthlyWarning);
            _lastOutputPath = outputPath;
            _monthlyWorkbookPreview = conversion.MonthlyPreview;
            _monthlySheetDrawable = null;
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

    private async void OnSavedLocationTapped(object? sender, TappedEventArgs e)
    {
        var outputPath = _lastOutputPath;
        if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
            return;

        try
        {
            var pathToOpen = outputPath;
#if ANDROID
            var sharingDirectory = Path.Combine(FileSystem.CacheDirectory, "attendance-report-sharing");
            Directory.CreateDirectory(sharingDirectory);
            pathToOpen = Path.Combine(sharingDirectory, Path.GetFileName(outputPath));
            File.Copy(outputPath, pathToOpen, overwrite: true);
            await Share.Default.RequestAsync(new ShareFileRequest(
                "Save or share attendance workbook",
                new ShareFile(pathToOpen)));
#else
            await Launcher.Default.OpenAsync(new OpenFileRequest(
                "Attendance workbook",
                new ReadOnlyFile(pathToOpen)));
#endif
        }
        catch (Exception error)
        {
            await DisplayAlertAsync("Could not open the workbook", error.Message, "OK");
        }
    }

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
        _monthlyWorkbookPreview = null;
        _lastOutputPath = null;
        _monthlySheetDrawable = null;
        SavedLabel.Text = "";
        SavedLocationLabel.Text = "";
        SavedPathLabel.Text = "";
        ConversionWarningLabel.Text = "";
        ConversionWarningLabel.IsVisible = false;
        _dashboard = null;
        ShowTableTab();
    }

    // --- tabs ---

    private void OnTabTableClicked(object? sender, EventArgs e) => ShowTableTab();

    private void OnTabDashboardClicked(object? sender, EventArgs e) => ShowDashboardTab();

    private void OnMonthlyAttendanceSheetClicked(object? sender, EventArgs e) => ShowMonthlySheet(0);

    private void OnMonthlyHolidaySheetClicked(object? sender, EventArgs e) => ShowMonthlySheet(1);

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
        if (_monthlyWorkbookPreview is { Sheets.Count: > 0 })
        {
            DailyTableScroll.IsVisible = false;
            MonthlyTableContainer.IsVisible = true;
            ShowMonthlySheet(Math.Clamp(_monthlySheetDrawable?.SheetIndex ?? 0, 0, _monthlyWorkbookPreview.Sheets.Count - 1));
            return;
        }

        DailyTableScroll.IsVisible = true;
        MonthlyTableContainer.IsVisible = false;
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

    private void ShowMonthlySheet(int index)
    {
        if (_monthlyWorkbookPreview is not { Sheets.Count: > 0 })
            return;

        index = Math.Clamp(index, 0, _monthlyWorkbookPreview.Sheets.Count - 1);
        var sheet = _monthlyWorkbookPreview.Sheets[index];
        _monthlySheetDrawable = new MonthlySheetDrawable(sheet, index);
        MonthlyPreviewCanvas.Drawable = _monthlySheetDrawable;
        MonthlyPreviewCanvas.WidthRequest = _monthlySheetDrawable.TotalWidth;
        MonthlyPreviewCanvas.HeightRequest = _monthlySheetDrawable.TotalHeight;
        MonthlyPreviewCanvas.Invalidate();
        MonthlyTemplateLogoPreview.IsVisible = sheet.Name == "Attendance";
        SetThemeColor(MonthlyAttendanceSheetBtn, Button.BackgroundColorProperty,
            index == 0 ? "ActionLight" : "SurfaceLight", index == 0 ? "ActionDark" : "SurfaceDark");
        SetThemeColor(MonthlyAttendanceSheetBtn, Button.TextColorProperty,
            index == 0 ? "ActionTextLight" : "TextBodyLight", index == 0 ? "ActionTextDark" : "TextBodyDark");
        SetThemeColor(MonthlyHolidaySheetBtn, Button.BackgroundColorProperty,
            index == 1 ? "ActionLight" : "SurfaceLight", index == 1 ? "ActionDark" : "SurfaceDark");
        SetThemeColor(MonthlyHolidaySheetBtn, Button.TextColorProperty,
            index == 1 ? "ActionTextLight" : "TextBodyLight", index == 1 ? "ActionTextDark" : "TextBodyDark");
    }

    private sealed class MonthlySheetDrawable : Microsoft.Maui.Graphics.IDrawable
    {
        private readonly MonthlySheetPreview _sheet;
        private readonly float[] _columnPositions;
        private readonly float[] _rowPositions;

        public int SheetIndex { get; }
        public float TotalWidth => _columnPositions[^1];
        public float TotalHeight => _rowPositions[^1];

        public MonthlySheetDrawable(MonthlySheetPreview sheet, int sheetIndex)
        {
            _sheet = sheet;
            SheetIndex = sheetIndex;
            _columnPositions = new float[sheet.ColumnCount + 1];
            _rowPositions = new float[sheet.RowCount + 1];

            for (var column = 0; column < sheet.ColumnCount; column++)
                _columnPositions[column + 1] = _columnPositions[column] + ToDeviceColumnWidth(sheet.ColumnWidths[column]);
            for (var row = 0; row < sheet.RowCount; row++)
                _rowPositions[row + 1] = _rowPositions[row] + ToDeviceRowHeight(sheet.RowHeights[row]);
        }

        public void Draw(Microsoft.Maui.Graphics.ICanvas canvas, Microsoft.Maui.Graphics.RectF dirtyRect)
        {
            canvas.SaveState();
            canvas.FillColor = Microsoft.Maui.Graphics.Colors.White;
            canvas.FillRectangle(0, 0, TotalWidth, TotalHeight);
            foreach (var cell in _sheet.Cells)
            {
                var x = _columnPositions[cell.Column - 1];
                var y = _rowPositions[cell.Row - 1];
                var width = _columnPositions[Math.Min(_sheet.ColumnCount, cell.Column - 1 + cell.ColumnSpan)] - x;
                var height = _rowPositions[Math.Min(_sheet.RowCount, cell.Row - 1 + cell.RowSpan)] - y;
                var rect = new Microsoft.Maui.Graphics.RectF(x, y, width, height);

                canvas.FillColor = ParseColor(cell.FillColor, "#FFFFFF");
                canvas.FillRectangle(rect);
                canvas.StrokeColor = Microsoft.Maui.Graphics.Color.FromArgb("#A6A6A6");
                canvas.StrokeSize = 0.7f;
                canvas.DrawRectangle(rect);

                if (string.IsNullOrEmpty(cell.Text))
                    continue;

                canvas.Font = cell.Bold
                    ? Microsoft.Maui.Graphics.Font.DefaultBold
                    : Microsoft.Maui.Graphics.Font.Default;
                canvas.FontSize = (float)Math.Clamp(cell.FontSize * 1.05, 9, 16);
                canvas.FontColor = ParseColor(cell.TextColor, "#111111");
                var horizontal = cell.HorizontalAlignment switch
                {
                    "Center" => Microsoft.Maui.Graphics.HorizontalAlignment.Center,
                    "Right" => Microsoft.Maui.Graphics.HorizontalAlignment.Right,
                    _ => Microsoft.Maui.Graphics.HorizontalAlignment.Left,
                };
                var vertical = cell.VerticalAlignment switch
                {
                    "Top" => Microsoft.Maui.Graphics.VerticalAlignment.Top,
                    "Bottom" => Microsoft.Maui.Graphics.VerticalAlignment.Bottom,
                    _ => Microsoft.Maui.Graphics.VerticalAlignment.Center,
                };
                canvas.DrawString(cell.Text,
                    x + 3, y + 1, Math.Max(0, width - 6), Math.Max(0, height - 2), horizontal, vertical);
            }
            canvas.RestoreState();
        }

        private static float ToDeviceColumnWidth(double excelWidth) =>
            (float)Math.Clamp(excelWidth * 7.1, 38, 190);

        private static float ToDeviceRowHeight(double excelHeight) =>
            (float)Math.Clamp(excelHeight * 1.35, 22, 44);

        private static Microsoft.Maui.Graphics.Color ParseColor(string? color, string fallback) =>
            Microsoft.Maui.Graphics.Color.FromArgb(string.IsNullOrWhiteSpace(color) ? fallback : color);
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
