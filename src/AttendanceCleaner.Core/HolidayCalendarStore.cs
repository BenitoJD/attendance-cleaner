using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AttendanceCleaner.Core;

/// <summary>A locally editable holiday or special workday shown in monthly workbooks.</summary>
public sealed record HolidayEntry(string Id, DateOnly Date, string Name, string Category)
{
    public static HolidayEntry Create(DateOnly date, string name, string category) =>
        new(Guid.NewGuid().ToString("N"), date, name.Trim(), category.Trim());
}

/// <summary>Reads and writes the per-user holiday calendar without tying the core to MAUI.</summary>
public sealed class HolidayCalendarStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        RespectRequiredConstructorParameters = true,
    };
    private static readonly HashSet<string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        MonthlyTemplateSpec.CorporateOfficeCategory,
        MonthlyTemplateSpec.TamilNaduCategory,
        MonthlyTemplateSpec.OptionalCorporateCategory,
        MonthlyTemplateSpec.OptionalTamilNaduCategory,
        MonthlyTemplateSpec.WorkingSaturdayCategory,
    };
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private readonly string _path;
    private readonly string _lockName;

    public HolidayCalendarStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        var lockPath = OperatingSystem.IsWindows() ? _path.ToUpperInvariant() : _path;
        _lockName = "AttendanceReportStudio.HolidayCalendar."
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(lockPath)));
    }

    public IReadOnlyList<HolidayEntry> LoadOrSeed() => WithCalendarLock(LoadOrSeedCore);

    /// <summary>Replaces the whole calendar. Use Upsert or Delete for an individual editor change.</summary>
    public void Save(IEnumerable<HolidayEntry> entries)
    {
        var validated = ValidateAndSort(entries);
        WithCalendarLock(() =>
        {
            SaveCore(validated);
            return true;
        });
    }

    /// <summary>Applies an editor change to the latest calendar without losing other saved entries.</summary>
    public IReadOnlyList<HolidayEntry> Upsert(HolidayEntry entry, HolidayEntry? expectedEntry = null)
    {
        ValidateAndSort([entry]);
        return WithCalendarLock(() =>
        {
            var entries = LoadOrSeedCore();
            EnsureUnchanged(entries, entry.Id, expectedEntry);
            var updated = ValidateAndSort(entries.Where(saved => saved.Id != entry.Id).Append(entry));
            SaveCore(updated);
            return updated;
        });
    }

    /// <summary>Removes an entry from the latest calendar and returns the saved snapshot.</summary>
    public IReadOnlyList<HolidayEntry> Delete(string id, HolidayEntry? expectedEntry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return WithCalendarLock(() =>
        {
            var entries = LoadOrSeedCore();
            EnsureUnchanged(entries, id, expectedEntry);
            var updated = ValidateAndSort(entries.Where(entry => entry.Id != id));
            SaveCore(updated);
            return updated;
        });
    }

    private IReadOnlyList<HolidayEntry> LoadOrSeedCore()
    {
        if (!File.Exists(_path))
        {
            var seeded = Seed2026();
            SaveCore(seeded);
            return seeded;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var entries = JsonSerializer.Deserialize<List<HolidayEntry>>(json, JsonOptions)
                ?? throw new InvalidDataException("The saved holiday calendar is empty or invalid.");
            return ValidateAndSort(entries);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The saved holiday calendar contains invalid JSON or incomplete entries.", ex);
        }
    }

    private void SaveCore(IReadOnlyList<HolidayEntry> entries)
    {
        var folder = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(folder);
        var temporaryPath = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entries, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private T WithCalendarLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(initiallyOwned: false, _lockName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(LockTimeout);
            }
            catch (AbandonedMutexException)
            {
                // The previous process exited during an operation; atomic saves preserve the last complete file.
                acquired = true;
            }
            if (!acquired)
                throw new IOException("The holiday calendar is busy in another window. Try again.");
            return action();
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    private static void EnsureUnchanged(IReadOnlyList<HolidayEntry> entries, string id, HolidayEntry? expectedEntry)
    {
        if (expectedEntry is null) return;
        if (expectedEntry.Id != id)
            throw new ArgumentException("The expected holiday must have the same identifier.", nameof(expectedEntry));
        if (entries.FirstOrDefault(entry => entry.Id == id) != expectedEntry)
            throw new InvalidOperationException("This holiday was changed or deleted in another window. Reopen Manage holidays and try again.");
    }

    public static IReadOnlyList<HolidayEntry> Seed2026()
    {
        using var stream = typeof(HolidayCalendarStore).Assembly
            .GetManifestResourceStream("AttendanceCleaner.Core.Holidays2026.json")
            ?? throw new InvalidOperationException("The bundled holiday calendar is missing.");
        var entries = JsonSerializer.Deserialize<List<SeedHoliday>>(stream, JsonOptions)
            ?? throw new InvalidDataException("The bundled holiday calendar is invalid.");
        return ValidateAndSort(entries.Select(entry => HolidayEntry.Create(entry.Date, entry.Name, entry.Category)));
    }

    private sealed record SeedHoliday(DateOnly Date, string Name, string Category);

    public static string GetYearWarning(IReadOnlyCollection<HolidayEntry> entries, int year) =>
        entries.Any(entry => entry.Date.Year == year)
            ? ""
            : $"No holiday or working Saturday entries are configured for {year}. Review that year's calendar in Manage holidays; holiday credits and working Saturdays for this year have not been applied.";

    public static IReadOnlyList<HolidayEntry> Sort(IEnumerable<HolidayEntry> entries) =>
        entries.OrderBy(entry => entry.Date)
            .ThenBy(entry => entry.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Validates shared calendar rules before editing, saving, or generating a workbook.</summary>
    public static IReadOnlyList<HolidayEntry> ValidateAndSort(IEnumerable<HolidayEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var validated = entries.ToList();
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < validated.Count; index++)
        {
            var entry = validated[index];
            if (entry is null)
                throw new InvalidDataException($"The holiday calendar contains an empty entry at position {index + 1}.");
            if (string.IsNullOrWhiteSpace(entry.Id))
                throw new InvalidDataException("Each holiday must have an identifier.");
            if (!identifiers.Add(entry.Id))
                throw new InvalidDataException($"The holiday calendar contains a duplicate identifier: {entry.Id}.");
            if (string.IsNullOrWhiteSpace(entry.Name))
                throw new InvalidDataException("Enter a holiday name.");
            if (entry.Date == DateOnly.MinValue)
                throw new InvalidDataException("Choose a holiday date.");
            if (string.IsNullOrWhiteSpace(entry.Category) || !Categories.Contains(entry.Category))
                throw new InvalidDataException($"Choose a valid category for holiday “{entry.Name}”.");
            if (MonthlyTemplateSpec.IsWorkingSaturday(entry.Category) && entry.Date.DayOfWeek != DayOfWeek.Saturday)
                throw new InvalidDataException("Working Saturday entries must fall on a Saturday.");
        }
        return Sort(validated);
    }
}
