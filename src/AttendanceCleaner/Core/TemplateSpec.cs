namespace AttendanceCleaner.Core;

/// <summary>
/// Everything about the output template and the conversion rules in one place.
/// If the template layout or the business rules ever change, change them here only.
/// Nothing about specific employees, dates or months is hardcoded anywhere.
/// </summary>
public static class TemplateSpec
{
    // --- output layout (mirrors Attendance_New_Template) ---

    public const string SheetName = "Attendance";

    public static readonly string[] Headers =
    {
        "Sl.No", "ID No", "Name", "Genter", "Date", "Day", "In punch", "Out punch", "Total hours", "Remarks",
    };

    /// <summary>Non-zero entries set an explicit column width; others auto-fit.</summary>
    public static readonly (int Column, double Width)[] ColumnWidths =
    {
        (1, 8), (2, 10), (3, 26), (4, 10), (5, 14), (6, 10), (7, 12), (9, 13), (10, 18),
    };

    public const string DateFormat = "dd-MM-yyyy";
    public const string TimeFormat = "HH:mm";
    public const string MonthNameFormat = "MMMM 'yyyy'";

    // --- conversion rules ---

    public const string RemarkPresent = "Present";
    public const string RemarkInPunchOnly = "In punch only";
    public const string RemarkAbsent = "Absent";

    /// <summary>Written when the source report does not state the gender (monthly exports don't).</summary>
    public const string UnknownGender = "-";

    // --- output file naming ---

    public const string DailyFileName = "Attendance_{0:dd-MM-yyyy}_Template.xlsx";
    public const string MonthlyFileName = "Attendance_{0:MMMM 'yyyy'}_Template.xlsx";
}
