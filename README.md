# attendance-cleaner

A .NET MAUI app that converts attendance reports downloaded from the attendance software into a clean Excel template.

**Flow:** open the app → pick the `.xls` file downloaded from the attendance software → Convert & save → choose where to save (Windows shows a Save-as dialog). The cleaned `.xlsx` uses the template format:

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

## Downloads

**[Releases](https://github.com/BenitoJD/attendance-cleaner/releases)** — every version's installers live here. Grab the latest, unzip, run `AttendanceCleaner.exe`:

- `AttendanceCleaner-win-x64.zip` — 64-bit Windows 10 (1809+) / Windows 11 (almost every modern PC)
- `AttendanceCleaner-win-x86.zip` — 32-bit Windows 10 (1809+); also runs on 64-bit Windows, so it's the safe pick when unsure

To publish a new version: tag a commit (`git tag v1.0.1 && git push --tags`) or run the `release` workflow from the Actions tab — it runs the tests, builds both installers, and creates the Release automatically.

## Layout

```
AttendanceCleaner.slnx
src/AttendanceCleaner/    # the MAUI app (Core/ holds the parser + Excel writer)
tools/ConverterCli/       # console harness for the core logic (not in the solution)
tools/AttendanceCleaner.Tests/  # 65-test suite, run in CI
```
