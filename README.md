# Attendance Report Studio

A .NET MAUI app that turns raw attendance exports into clear daily and monthly Excel reports, with summaries and a PDF dashboard.

**Flow:** choose a Daily or Monthly template → select the `.xls` export from the attendance software → convert and save the finished `.xlsx`. Monthly reports can use either the Duty/OT or IN/OUT layout and include an editable Holiday Details sheet.

```
Sl.No | ID No | Name | Gender | Date | Day | In punch | Out punch | Total hours | Remarks
```

Remarks are derived from the punches: both punches → **Present**, check-in only → **In punch only**, none → **Absent**.

## Dashboard

After converting, the results screen has two tabs:

- **Table** — the converted rows, shown like the Excel sheet (columns auto-sized to content).
- **Dashboard** — derived analytics: employee count, average in/out times, average worked hours, a per-day attendance chart (present vs absent), and a per-employee summary (present/absent/in-only days, personal averages, attendance rate). Everything is computed from the converted data — no schedules or work rules are assumed.

**Save dashboard as PDF** generates a landscape A4 report with all of the above (KPIs, chart, employee table, page numbers) and opens the save dialog — suggested name `Attendance Dashboard <Month Year>.pdf`. PDF export is available in the Windows build.

## Supported inputs

The attendance software exports HTML tables with an `.xls` extension. Three layouts are recognised (auto-detected):

| Export | Layout |
| --- | --- |
| Daily Report | one row per employee |
| Monthly report (block style) | one block of rows per employee, days as columns |
| Monthly Performance report | metric rows per employee, days as columns |

The `Sep Monthly Over View Report` contains attendance statuses but no punch times, so it cannot fill the monthly punch templates. Choose a detailed monthly report instead.

## Removing the Windows warning (code signing)

Windows 11's **Smart App Control** (and SmartScreen on Windows 10) block unsigned apps. The permanent fix is signing the exes with a code signing certificate; there is no app-side trick that avoids it.

This repo is ready for **Azure Trusted Signing** (about 10 USD/month, individual developers accepted). One-time setup:

1. Create an Azure Trusted Signing account and certificate profile (Azure portal, Identity validation required)
2. Register an Entra ID app with the "Code Signing Developer" role on that account
3. Add these repository secrets (Settings, Secrets and variables, Actions): `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`, `AZURE_SIGNING_ENDPOINT`, `AZURE_SIGNING_ACCOUNT`, `AZURE_CERT_PROFILE`

Every release after that signs both exes automatically and the warnings disappear. Until then, users see the one-time SmartScreen "Run anyway" prompt, and on machines with Smart App Control fully On an administrator must turn it off for that PC (Settings, Privacy and security, Windows Security, App and browser control).

## Development

Requires the .NET SDK with the MAUI workload:

```bash
dotnet workload install maui
```

Build/run for Android (requires an installed Android SDK and an emulator or connected device):

```bash
export ANDROID_HOME="/path/to/android-sdk"
dotnet build src/AttendanceCleaner -f net10.0-android -p:AndroidSdkDirectory="$ANDROID_HOME"
dotnet build -t:Run -f net10.0-android -p:AndroidSdkDirectory="$ANDROID_HOME"   # run on emulator/device
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

**[Releases](https://github.com/BenitoJD/attendance-cleaner/releases)** — download and run the setup EXE for your Windows architecture. It installs Attendance Report Studio for the current Windows user, adds a Start menu shortcut, and registers an uninstaller; it does not require a separate .NET installation.

- `AttendanceReportStudio-Setup-x64.exe` — recommended for 64-bit Windows 10 (1809+) / Windows 11
- `AttendanceReportStudio-Setup-x86.exe` — for 32-bit Windows 10 (1809+)
- `AttendanceReportStudio-win-x64.zip` / `AttendanceReportStudio-win-x86.zip` — portable alternatives; extract the entire folder and run `AttendanceReportStudio.exe` from inside it

The release workflow runs the tests, builds both setup installers and portable ZIPs, then installs, launches, and uninstalls each setup on a Windows runner before creating a Release.

## Layout

```
AttendanceCleaner.slnx
src/AttendanceCleaner/    # the MAUI app (Core/ holds the parser + Excel writer)
tools/ConverterCli/       # console harness for the core logic (not in the solution)
tools/AttendanceCleaner.Tests/  # test suite, run in CI
```
