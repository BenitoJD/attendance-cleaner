using System.Text.Json;

namespace AttendanceCleaner.Core;

/// <summary>A locally editable holiday or special workday shown in monthly workbooks.</summary>
public sealed record HolidayEntry(string Id, DateOnly Date, string Name, string Category)
{
    public static HolidayEntry Create(DateOnly date, string name, string category) =>
        new(Guid.NewGuid().ToString("N"), date, name.Trim(), category.Trim());
}

/// <summary>Reads and writes the per-user holiday calendar without tying the core to MAUI.</summary>
public sealed class HolidayCalendarStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public IReadOnlyList<HolidayEntry> LoadOrSeed()
    {
        if (!File.Exists(path))
        {
            var seeded = Seed2026();
            Save(seeded);
            return seeded;
        }

        var json = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize<List<HolidayEntry>>(json, JsonOptions)
            ?? throw new InvalidDataException("The saved holiday calendar is empty or invalid.");
        return Sort(entries);
    }

    public void Save(IEnumerable<HolidayEntry> entries)
    {
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException("The holiday calendar path has no parent folder.");

        Directory.CreateDirectory(folder);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(Sort(entries), JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    public static IReadOnlyList<HolidayEntry> Seed2026()
    {
        var rows = new List<(int Month, int Day, string Name, string Category)>
        {
            (1, 1, "New Year Day", "Corporate Office"),
            (1, 14, "Bhogi", "Corporate Office"),
            (1, 15, "Pongal", "Corporate Office"),
            (1, 26, "Republic Day", "Corporate Office"),
            (3, 3, "Holi", "Corporate Office"),
            (3, 19, "Ugadi", "Corporate Office"),
            (5, 1, "Labour Day / May Day", "Corporate Office"),
            (6, 2, "Telangana Formation Day", "Corporate Office"),
            (8, 15, "Independence Day", "Corporate Office"),
            (9, 14, "Vinayak Chaturthi", "Corporate Office"),
            (10, 2, "Gandhi Jayanti", "Corporate Office"),
            (10, 20, "Dusherra", "Corporate Office"),
            (10, 21, "Following Day of Duesherra", "Corporate Office"),
            (11, 9, "Deepavali", "Corporate Office"),

            (1, 1, "New Year Day", "Tamil Nadu"),
            (1, 14, "Thai Pongal", "Tamil Nadu"),
            (1, 15, "Mattu Pongal", "Tamil Nadu"),
            (1, 26, "Republic Day", "Tamil Nadu"),
            (5, 1, "Labour Day / May Day", "Tamil Nadu"),
            (8, 15, "Independence Day", "Tamil Nadu"),
            (9, 14, "Vinayak Chaturthi", "Tamil Nadu"),
            (10, 2, "Gandhi Jayanti", "Tamil Nadu"),
            (10, 19, "Ayudha Puja", "Tamil Nadu"),
            (10, 20, "Vijaya Dasami", "Tamil Nadu"),
            (11, 9, "Deepavali", "Tamil Nadu"),

            (3, 21, "Ramzan", "Optional · Corporate Office"),
            (4, 3, "Good Friday", "Optional · Corporate Office"),
            (12, 25, "Christmas", "Optional · Corporate Office"),
            (5, 27, "Bakird", "Optional · Tamil Nadu"),
            (12, 25, "Christmas", "Optional · Tamil Nadu"),

            (1, 31, "Working Day", "Working Saturday · Corporate Office"),
            (10, 24, "Working Day", "Working Saturday · Corporate Office"),
        };

        return Sort(rows.Select(row => HolidayEntry.Create(
            new DateOnly(2026, row.Month, row.Day), row.Name, row.Category)));
    }

    public static IReadOnlyList<HolidayEntry> Sort(IEnumerable<HolidayEntry> entries) =>
        entries.OrderBy(entry => entry.Date)
            .ThenBy(entry => entry.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
