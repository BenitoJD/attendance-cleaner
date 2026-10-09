namespace AttendanceCleaner.Core;

/// <summary>Saves a complete report before replacing an existing destination.</summary>
public static class ReportFileWriter
{
    public static void WriteBytes(string path, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        Write(path, stream => stream.Write(content));
    }

    public static void Write(string path, Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        var destination = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!,
            $".attendance-report-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // Cleanup must not replace the original save error.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup must not replace the original save error.
            }
        }
    }
}
