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

        if (value.Contains(':'))
        {
            // Durations are hours:minutes[:seconds], never TimeSpan's day-based forms.
            var parts = value.Split(':');
            if (parts.Length is not (2 or 3)
                || parts[0].Length == 0 || !parts[0].All(char.IsAsciiDigit)
                || !decimal.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var wholeHours)
                || parts[1].Length != 2 || !parts[1].All(char.IsAsciiDigit)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
                || minutes > 59)
                return false;
            var seconds = 0;
            if (parts.Length == 3
                && (parts[2].Length != 2 || !parts[2].All(char.IsAsciiDigit)
                    || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out seconds)
                    || seconds > 59))
                return false;
            var fraction = minutes / 60m + seconds / 3600m;
            if (fraction > decimal.MaxValue - wholeHours) return false;
            hours = wholeHours + fraction;
            return true;
        }
        // A comma is ambiguous (decimal comma versus grouping); never silently turn 1,5 into 15.
        return decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out hours);
    }

    public static bool TryParseWorkedMinutes(string? value, out int minutes)
    {
        minutes = 0;
        if (!TryParseHours(value, out var hours) || hours <= 0 || hours > int.MaxValue / 60m)
            return false;
        var reportedMinutes = hours * 60m;
        if (value!.Contains(':'))
        {
            // Preserve exact half-minute boundaries rather than dividing by 3600 and multiplying back.
            var parts = value.Trim().Split(':');
            reportedMinutes = decimal.Parse(parts[0], CultureInfo.InvariantCulture) * 60m
                + int.Parse(parts[1], CultureInfo.InvariantCulture)
                + (parts.Length == 3 ? int.Parse(parts[2], CultureInfo.InvariantCulture) / 60m : 0m);
        }
        var rounded = decimal.Round(reportedMinutes, 0, MidpointRounding.AwayFromZero);
        if (rounded > int.MaxValue) return false;
        minutes = (int)rounded;
        return true;
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
