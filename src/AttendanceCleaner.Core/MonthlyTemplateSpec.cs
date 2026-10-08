using System.Globalization;

namespace AttendanceCleaner.Core;

/// <summary>
/// Business rules and labels required by the supplied monthly template.
/// Keep the displayed shifts and holiday credits derived from the same schedule.
/// </summary>
public static class MonthlyTemplateSpec
{
    public const string CompanyName = "DAKSHINAK MINERAL  LLP/";
    public const string StaffLabel = "Staffs";
    public const string HolidaySheetName = "Holiday Details";
    public const string CorporateOfficeCategory = "Corporate Office";
    public const string TamilNaduCategory = "Tamil Nadu";
    public const string OptionalCorporateCategory = "Optional · Corporate Office";
    public const string OptionalTamilNaduCategory = "Optional · Tamil Nadu";
    public const string WorkingSaturdayCategory = "Working Saturday · Corporate Office";
    private const string WorkingSaturdayPrefix = "Working Saturday";

    public static TimeOnly DutyStart { get; } = new(7, 0);
    public static TimeOnly DutyEnd { get; } = new(16, 0);
    public static TimeOnly OvertimeEnd { get; } = new(19, 0);
    public static decimal DutyHours => (decimal)(DutyEnd - DutyStart).TotalHours;
    public static decimal MaximumOvertimeHours => (decimal)(OvertimeEnd - DutyEnd).TotalHours;
    public static string DutyLabel => $"Duty ({ShiftTime(DutyStart)}–{ShiftTime(DutyEnd)})";
    public static string OvertimeLabel => $"OT ({ShiftTime(DutyEnd)}–{ShiftTime(OvertimeEnd)})";

    public static bool IsMandatoryHoliday(string category) =>
        category.Equals(CorporateOfficeCategory, StringComparison.OrdinalIgnoreCase)
        || category.Equals(TamilNaduCategory, StringComparison.OrdinalIgnoreCase);

    public static bool IsWorkingSaturday(string category) =>
        category.StartsWith(WorkingSaturdayPrefix, StringComparison.OrdinalIgnoreCase);

    private static string ShiftTime(TimeOnly time) =>
        time.ToString(time.Minute == 0 ? "htt" : "h:mmtt", CultureInfo.InvariantCulture).ToLowerInvariant();
}
