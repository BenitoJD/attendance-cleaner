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
        using var stream = typeof(HolidayCalendarStore).Assembly
            .GetManifestResourceStream("AttendanceCleaner.Core.Holidays2026.json")
            ?? throw new InvalidOperationException("The bundled holiday calendar is missing.");
        var entries = JsonSerializer.Deserialize<List<SeedHoliday>>(stream, JsonOptions)
            ?? throw new InvalidDataException("The bundled holiday calendar is invalid.");
        return Sort(entries.Select(entry => HolidayEntry.Create(entry.Date, entry.Name, entry.Category)));
    }

    private sealed record SeedHoliday(DateOnly Date, string Name, string Category);

    public static IReadOnlyList<HolidayEntry> Sort(IEnumerable<HolidayEntry> entries) =>
        entries.OrderBy(entry => entry.Date)
            .ThenBy(entry => entry.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
