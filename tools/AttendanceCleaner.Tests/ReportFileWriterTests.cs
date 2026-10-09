using AttendanceCleaner.Core;
using ClosedXML.Excel;
using Xunit;

namespace AttendanceCleaner.Tests;

public sealed class ReportFileWriterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Partial_write_failure_keeps_the_previous_report_and_leaves_no_temporary_file(bool existing)
    {
        var directory = CreateDirectory();
        var path = Path.Combine(directory, "report.xlsx");
        var previous = new byte[] { 1, 2, 3, 4 };
        if (existing) File.WriteAllBytes(path, previous);
        try
        {
            var failure = new IOException("Synthetic disk write failure.");
            var error = Assert.Throws<IOException>(() => ReportFileWriter.Write(path, stream =>
            {
                stream.Write(new byte[] { 9, 8 });
                throw failure;
            }));

            Assert.Same(failure, error);
            if (existing) Assert.Equal(previous, File.ReadAllBytes(path));
            else Assert.False(File.Exists(path));
            Assert.Equal(existing ? [path] : Array.Empty<string>(), Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Replacement_failure_preserves_the_destination_and_cleans_up()
    {
        var directory = CreateDirectory();
        var path = Path.Combine(directory, "destination");
        Directory.CreateDirectory(path);
        var previousPath = Path.Combine(path, "previous.xlsx");
        var previous = new byte[] { 1, 2, 3 };
        File.WriteAllBytes(previousPath, previous);
        try
        {
            var error = Record.Exception(() => ReportFileWriter.WriteBytes(path, [9, 8, 7]));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Equal(previous, File.ReadAllBytes(previousPath));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Windows_locked_report_is_preserved_when_replacement_is_denied()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = CreateDirectory();
        var path = Path.Combine(directory, "locked.xlsx");
        var previous = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(path, previous);
        try
        {
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var error = Record.Exception(() => ReportFileWriter.WriteBytes(path, [9, 8, 7]));
                Assert.True(error is IOException or UnauthorizedAccessException);
            }
            Assert.Equal(previous, File.ReadAllBytes(path));
            Assert.Equal([path], Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Successful_save_replaces_an_existing_report_with_a_readable_workbook()
    {
        var directory = CreateDirectory();
        var path = Path.Combine(directory, "report.xlsx");
        File.WriteAllText(path, "Previous file");
        try
        {
            TemplateWriter.WriteToFile(
                [new("007", "Example", "", new DateOnly(2026, 9, 1), "07:00", "16:00", 0)], path);
            using var workbook = new XLWorkbook(path);
            Assert.Equal("007", workbook.Worksheets.Single().Cell(2, 2).GetString());
            Assert.Equal("09:00", workbook.Worksheets.Single().Cell(2, 9).GetString());
            Assert.Equal([path], Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Existing_report_survives_generation_failure()
    {
        var directory = CreateDirectory();
        var path = Path.Combine(directory, "existing.xlsx");
        var previous = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(path, previous);
        try
        {
            Assert.Throws<InvalidDataException>(() => TemplateWriter.WriteToFile(FailingRecords(), path));
            Assert.Equal(previous, File.ReadAllBytes(path));
            Assert.Equal([path], Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static IEnumerable<AttendanceRecord> FailingRecords()
    {
        yield return new("007", "Example", "", new DateOnly(2026, 9, 1), "07:00", "16:00", 0);
        throw new InvalidDataException("Synthetic source failure.");
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "attendance-save-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
