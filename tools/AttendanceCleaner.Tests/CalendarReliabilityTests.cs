using AttendanceCleaner.Core;
using Xunit;

namespace AttendanceCleaner.Tests;

public sealed class CalendarReliabilityTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public CalendarReliabilityTests()
    {
        Directory.CreateDirectory(_folder);
        _path = Path.Combine(_folder, "holidays.json");
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("[{\"Date\":\"2026-10-11\",\"Name\":\"Missing fields\"}]")]
    [InlineData("[{\"Id\":\"1\",\"Name\":\"Missing date\",\"Category\":\"Corporate Office\"}]")]
    [InlineData("[{\"Id\":null,\"Date\":\"2026-10-11\",\"Name\":\"Null ID\",\"Category\":\"Corporate Office\"}]")]
    [InlineData("[{\"Id\":\"1\",\"Date\":\"2026-10-11\",\"Name\":null,\"Category\":\"Corporate Office\"}]")]
    [InlineData("[{\"Id\":\"1\",\"Date\":\"2026-10-11\",\"Name\":\"Null category\",\"Category\":null}]")]
    [InlineData("[{\"Id\":\"1\",\"Date\":\"2026-10-11\",\"Name\":\"Unknown category\",\"Category\":\"Typo\"}]")]
    [InlineData("[{\"Id\":\"1\",\"Date\":\"2026-10-11\",\"Name\":\"Wrong weekday\",\"Category\":\"Working Saturday · Corporate Office\"}]")]
    public void Invalid_saved_calendars_are_rejected_without_replacing_the_file(string json)
    {
        File.WriteAllText(_path, json);

        Assert.Throws<InvalidDataException>(() => new HolidayCalendarStore(_path).LoadOrSeed());

        Assert.Equal(json, File.ReadAllText(_path));
    }

    [Theory]
    [InlineData("missing ID")]
    [InlineData("blank ID")]
    [InlineData("missing name")]
    [InlineData("blank name")]
    [InlineData("missing date")]
    [InlineData("missing category")]
    [InlineData("unknown category")]
    [InlineData("wrong weekday")]
    [InlineData("null entry")]
    [InlineData("duplicate ID")]
    public void Invalid_changes_preserve_the_previous_calendar(string invalidCase)
    {
        var valid = Entry("Original holiday");
        var store = new HolidayCalendarStore(_path);
        store.Save([valid]);
        var savedJson = File.ReadAllText(_path);
        var invalid = invalidCase switch
        {
            "missing ID" => valid with { Id = null! },
            "blank ID" => valid with { Id = " " },
            "missing name" => valid with { Name = null! },
            "blank name" => valid with { Name = " " },
            "missing date" => valid with { Date = default },
            "missing category" => valid with { Category = null! },
            "unknown category" => valid with { Category = "Corporate Ofice" },
            "wrong weekday" => valid with { Date = new DateOnly(2026, 10, 11), Category = MonthlyTemplateSpec.WorkingSaturdayCategory },
            "null entry" => null!,
            _ => valid with { Name = "Same identifier" },
        };
        HolidayEntry[] entries = invalidCase == "duplicate ID" ? [valid, invalid] : [invalid];

        Assert.Throws<InvalidDataException>(() => store.Save(entries));

        Assert.Equal(savedJson, File.ReadAllText(_path));
        Assert.Equal(valid, Assert.Single(store.LoadOrSeed()));
    }

    [Fact]
    public void Independent_editors_preserve_each_others_additions_edits_and_deletions()
    {
        var firstStore = new HolidayCalendarStore(_path);
        var secondStore = new HolidayCalendarStore(_path);
        firstStore.Save([]);
        firstStore.LoadOrSeed();
        secondStore.LoadOrSeed();
        var first = Entry("First addition");
        var second = Entry("Second addition");

        firstStore.Upsert(first);
        var snapshot = secondStore.Upsert(second);

        Assert.Equal(2, snapshot.Count);
        Assert.Contains(first, snapshot);
        Assert.Contains(second, snapshot);
        var editedFirst = first with { Name = "Edited first addition" };
        firstStore.Upsert(editedFirst, first);
        var afterDeletion = secondStore.Delete(second.Id, second);

        Assert.Equal(editedFirst, Assert.Single(afterDeletion));
        Assert.Equal(editedFirst, Assert.Single(firstStore.LoadOrSeed()));
    }

    [Fact]
    public void Conflicting_changes_and_deleted_entry_edits_are_rejected()
    {
        var firstStore = new HolidayCalendarStore(_path);
        var secondStore = new HolidayCalendarStore(_path);
        var original = Entry("Original");
        firstStore.Save([original]);
        var edited = original with { Name = "Already edited" };
        firstStore.Upsert(edited, original);
        var staleEdit = original with { Name = "Stale edit" };

        Assert.Throws<InvalidOperationException>(() => secondStore.Upsert(staleEdit, original));
        Assert.Throws<InvalidOperationException>(() => secondStore.Delete(original.Id, original));
        Assert.Equal(edited, Assert.Single(firstStore.LoadOrSeed()));

        firstStore.Delete(edited.Id, edited);

        Assert.Throws<InvalidOperationException>(() => secondStore.Upsert(staleEdit, edited));
        Assert.Empty(firstStore.LoadOrSeed());
    }

    [Fact]
    public async Task Concurrent_mutations_preserve_all_entries_and_leave_no_temporary_files()
    {
        var entries = Enumerable.Range(0, 16).Select(index => Entry($"Holiday {index}")).ToArray();
        new HolidayCalendarStore(_path).Save([]);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saves = entries.Select(entry => Task.Run(async () =>
        {
            await start.Task;
            new HolidayCalendarStore(_path).Upsert(entry);
        })).ToArray();

        start.SetResult();
        await Task.WhenAll(saves);

        var reloaded = new HolidayCalendarStore(_path).LoadOrSeed();
        Assert.Equal(entries.Length, reloaded.Count);
        Assert.All(entries, entry => Assert.Contains(entry, reloaded));
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Concurrent_first_loads_share_one_seeded_calendar()
    {
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => new HolidayCalendarStore(_path).LoadOrSeed())));
        var saved = snapshots[0];

        Assert.Equal(32, saved.Count);
        Assert.All(snapshots, snapshot => Assert.Equal(saved, snapshot));
        Assert.Equal(saved, new HolidayCalendarStore(_path).LoadOrSeed());
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void Working_saturday_and_same_date_regional_entries_remain_supported()
    {
        var saturday = HolidayEntry.Create(new DateOnly(2026, 10, 10), "Working day", MonthlyTemplateSpec.WorkingSaturdayCategory);
        var corporate = Entry("Corporate holiday");
        var regional = corporate with { Id = Guid.NewGuid().ToString("N"), Category = MonthlyTemplateSpec.TamilNaduCategory };
        var store = new HolidayCalendarStore(_path);

        store.Save([regional, saturday, corporate]);

        var entries = store.LoadOrSeed();
        Assert.Equal(3, entries.Count);
        Assert.Equal(saturday, entries[0]);
        Assert.Contains(corporate, entries);
        Assert.Contains(regional, entries);
    }

    private static HolidayEntry Entry(string name) =>
        HolidayEntry.Create(new DateOnly(2026, 10, 20), name, MonthlyTemplateSpec.CorporateOfficeCategory);

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
