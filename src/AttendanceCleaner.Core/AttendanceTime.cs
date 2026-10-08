using System.Globalization;

namespace AttendanceCleaner.Core;

/// <summary>Shared parsing and arithmetic for normalized punches and reported durations.</summary>
internal static class AttendanceTime
{
    private const int MinutesPerHour = 60;
    private const int MinutesPerDay = 24 * MinutesPerHour;

    public static bool TryParsePunch(string? value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, TemplateSpec.TimeFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out time);

    public static bool TryParseHours(string? value, out decimal hours)
    {
        value = value?.Trim();
        hours = 0;
        if (string.IsNullOrEmpty(value) || value == "-") return false;

        if (value.Contains(':') && TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span))
        {
            hours = (decimal)span.TotalHours;
            return true;
        }
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out hours);
    }

    public static int? PunchDurationMinutes(string? inPunch, string? outPunch)
    {
        if (!TryParsePunch(inPunch, out var start) || !TryParsePunch(outPunch, out var end))
            return null;
        var minutes = (int)(end - start).TotalMinutes;
        return minutes < 0 ? minutes + MinutesPerDay : minutes;
    }

    public static string FormatMinutes(int minutes) =>
        string.Create(CultureInfo.InvariantCulture, $"{minutes / MinutesPerHour:00}:{minutes % MinutesPerHour:00}");
}
