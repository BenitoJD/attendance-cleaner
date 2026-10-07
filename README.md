# attendance-cleaner

A .NET MAUI app that converts attendance reports downloaded from the attendance software into a clean Excel template.

**Flow:** open the app → pick the `.xls` file downloaded from the attendance software → choose Daily or Monthly → Convert & save. The cleaned file is written next to the input as an `.xlsx` in the new template format:

```
Sl.No | ID No | Name | Genter | Date | Day | In punch | Out punch | Total hours | Remarks
```

Remarks are derived from the punches: both punches → **Present**, check-in only → **In punch only**, none → **Absent**.

## Supported inputs

The attendance software exports HTML tables with an `.xls` extension. Three layouts are recognised (auto-detected):

| Export | Layout |
| --- | --- |
| Daily Report | one row per employee |
| Monthly report (block style) | one block of rows per employee, days as columns |
| Monthly Performance report | metric rows per employee, days as columns |

The `Sep Monthly Over View Report` (statuses only, no punch times) is not supported yet.

## Development

Requires the .NET SDK with the MAUI workload:

```bash
dotnet workload install maui
```

Build/run for Android (works out of the box on this Mac; the SDK lives in `~/.android-sdk`):

```bash
dotnet build src/AttendanceCleaner -f net10.0-android -p:AndroidSdkDirectory="$HOME/.android-sdk"
dotnet build -t:Run -f net10.0-android -p:AndroidSdkDirectory="$HOME/.android-sdk"   # run on emulator/device
```

Build for Mac Catalyst / iOS requires full Xcode (not just Command Line Tools) installed on this Mac:

```bash
dotnet build src/AttendanceCleaner -f net10.0-maccatalyst
```

The Windows target (`net10.0-windows10.0.19041.0`) only builds on Windows — use CI (below).

### Core logic without the UI

`tools/ConverterCli` is a console harness around the same parser/writer (`src/AttendanceCleaner/Core`), handy for testing against real exports:

```bash
dotnet run --project tools/ConverterCli -- "path/to/6 Daily Report.xls" output.xlsx
```

## Windows executable

The GitHub Actions workflow (`.github/workflows/windows-build.yml`) builds a self-contained win-x64 exe on every push to `main` (or manually via *Run workflow*). Download it from the run's **Artifacts** section on the [Actions page](https://github.com/BenitoJD/attendance-cleaner/actions) — it's a zip you can extract and run on any Windows 10/11 PC, no .NET install needed.

## Layout

```
AttendanceCleaner.slnx
src/AttendanceCleaner/    # the MAUI app (Core/ holds the parser + Excel writer)
tools/ConverterCli/       # console harness for the core logic (not in the solution)
```
