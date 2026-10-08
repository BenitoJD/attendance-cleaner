namespace AttendanceCleaner.Tests;

/// <summary>Current year plus ten future years, kept fixed so CI coverage cannot drift.</summary>
internal static class CalendarValidationRange
{
    public const int FirstYear = 2026;
    public const int LastYear = 2036;

    public static IEnumerable<int> Years => Enumerable.Range(FirstYear, LastYear - FirstYear + 1);
}
