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
        "Sl.No", "ID No", "Name", "Gender", "Date", "Day", "In punch", "Out punch", "Total hours", "Remarks",
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

    // --- on-screen spreadsheet preview (driven by the same Headers/rows as the file) ---

    /// <summary>Preview column widths are computed from the actual content, clamped to these bounds,
    /// so long names are never truncated and narrow columns stay tidy.</summary>
    public const double PreviewMinColumnWidth = 40;
    public const double PreviewMaxColumnWidth = 220;
    public const double PreviewCharWidth = 7.4;
    public const double PreviewColumnPadding = 16;

    /// <summary>Header indexes shown left-aligned; the rest are centred.</summary>
    public static readonly int[] PreviewLeftAlignedColumns = { 2, 9 };

    // --- output file naming (suggested to the user; they can rename in the save dialog) ---

    public const string DailyFileName = "Attendance_{0:dd-MM-yyyy}.xlsx";
    public const string MonthlyFileName = "Attendance_{0:MMMM yyyy}.xlsx";
}
