using AttendanceCleaner.Core;

// Converts a downloaded attendance report (.xls HTML export) into the new template.
// Usage: ConverterCli <input.xls> [output.xlsx]
// Report kind is auto-detected; output defaults to <input>_converted.xlsx next to the input.

if (args.Length < 1)
{
    Console.WriteLine("Usage: ConverterCli <input.xls> [output.xlsx]");
    return 1;
}

var input = args[0];
var output = args.Length > 1 ? args[1] : Path.ChangeExtension(input, ".converted.xlsx");

var report = AttendanceParser.ParseFile(input);
Console.WriteLine($"Detected format: {report.Format}");
Console.WriteLine($"Records: {report.Records.Count} across {report.Records.Select(r => r.Date).Distinct().Count()} date(s)");

TemplateWriter.WriteToFile(report.Records, output);
Console.WriteLine($"Wrote {output}");
return 0;
