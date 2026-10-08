using AttendanceCleaner.Core;

namespace AttendanceCleaner;

/// <summary>Collects a complete holiday before the caller changes or persists the calendar.</summary>
public partial class HolidayEditorDialog : ContentView
{
    private TaskCompletionSource<HolidayEntry?>? _completion;
    private HolidayEntry? _original;
    private IReadOnlyList<(string Label, string Value)> _categories = [];

    public HolidayEditorDialog() => InitializeComponent();

    public Task<HolidayEntry?> ShowAsync(
        HolidayEntry? holiday,
        DateOnly defaultDate,
        IReadOnlyList<(string Label, string Value)> categories)
    {
        if (_completion is not null)
            throw new InvalidOperationException("A holiday dialog is already open.");

        _original = holiday;
        _categories = categories;
        DialogTitle.Text = holiday is null ? "New holiday" : "Edit holiday";
        SaveButton.Text = holiday is null ? "Add holiday" : "Save changes";
        NameEntry.Text = holiday?.Name ?? "";
        HolidayDatePicker.Date = (holiday?.Date ?? defaultDate).ToDateTime(TimeOnly.MinValue);
        CategoryPicker.ItemsSource = categories.Select(category => category.Label).ToArray();
        CategoryPicker.SelectedIndex = holiday is null ? 0
            : categories.ToList().FindIndex(category => category.Value.Equals(
                holiday.Category, StringComparison.OrdinalIgnoreCase));
        ValidationMessage.IsVisible = false;
        _completion = new TaskCompletionSource<HolidayEntry?>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsVisible = true;
        return _completion.Task;
    }

    public void Cancel() => Complete(null);

    private void OnCancelClicked(object? sender, EventArgs e) => Cancel();

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        var name = NameEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowValidation("Enter a holiday name.");
            NameEntry.Focus();
            return;
        }
        if (HolidayDatePicker.Date is not { } date)
        {
            ShowValidation("Choose a holiday date.");
            return;
        }
        if (CategoryPicker.SelectedIndex < 0 || CategoryPicker.SelectedIndex >= _categories.Count)
        {
            ShowValidation("Choose a holiday category.");
            return;
        }

        var category = _categories[CategoryPicker.SelectedIndex].Value;
        var selectedDate = DateOnly.FromDateTime(date);
        var entry = _original is null
            ? HolidayEntry.Create(selectedDate, name, category)
            : _original with { Date = selectedDate, Name = name, Category = category };
        try
        {
            HolidayCalendarStore.ValidateAndSort([entry]);
            Complete(entry);
        }
        catch (InvalidDataException ex)
        {
            ShowValidation(ex.Message);
        }
    }

    private void ShowValidation(string message)
    {
        ValidationMessage.Text = message;
        ValidationMessage.IsVisible = true;
    }

    private void Complete(HolidayEntry? holiday)
    {
        var completion = _completion;
        if (completion is null) return;
        _completion = null;
        NameEntry.Unfocus();
        HolidayDatePicker.Unfocus();
        CategoryPicker.Unfocus();
        IsVisible = false;
        completion.TrySetResult(holiday);
    }

    private void OnDialogSizeChanged(object? sender, EventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        DialogCard.WidthRequest = Math.Min(DialogCard.MaximumWidthRequest, Math.Max(0, Width - 48));
        DialogCard.MaximumHeightRequest = Math.Max(0, Height - 48);
    }
}
