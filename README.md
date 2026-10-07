# attendance-cleaner

A .NET MAUI app that cleans attendance Excel files: pick a file, strip unwanted columns/rows, and save a new, clean Excel file.

## Status

Scaffolded with .NET MAUI on .NET 10 (`src/AttendanceCleaner`). Excel cleanup logic (file picking, column/row removal) comes next.

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

The Windows target (`net10.0-windows10.0.19041.0`) only builds on Windows.

## Layout

```
AttendanceCleaner.slnx
src/AttendanceCleaner/    # the MAUI app
```
