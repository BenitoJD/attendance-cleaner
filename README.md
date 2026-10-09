# Attendance Report Studio

A .NET MAUI app that turns raw attendance exports into clear daily and monthly Excel or PDF reports, with summaries and a PDF dashboard.

**Flow:** choose a Daily or Monthly template → select the `.xls` export from the attendance software → choose Excel (`.xlsx`) or PDF (`.pdf`) → convert and save. Excel is selected by default. Monthly reports can use either the Duty/OT or IN/OUT layout and include an editable Holiday Details sheet.

```
Sl.No | ID No | Name | Gender | Date | Day | In punch | Out punch | Total hours | Remarks
```

Remarks are derived from the punches: both punches → **Present**, check-in only → **In punch only**, none → **Absent**.

## Dashboard

After converting, the results screen has two tabs:

- **Table** — the converted rows, shown like the Excel sheet (columns auto-sized to content).
- **Dashboard** — daily uploads show a daily dashboard only. Monthly uploads open in Monthly and also offer a Daily view with a selector containing only exported dates (initially the latest exported date). Changing the view rebuilds the employee count, averages, chart and employee summaries from that scope; returning to Monthly restores the entire report. A monthly file with just one exported date still offers both views.

Dashboard counts describe punch evidence: complete pairs, incomplete pairs and no-punch records. The chart includes all three states. Pair completeness is complete pairs divided by observed employee-date records, not a schedule-based attendance rate. No punches alone does not establish absence or leave; unreported dates are not selectable and are not invented as absent. Average hours covers complete pairs only.

**Save dashboard as PDF** exports the currently selected daily or monthly scope as a landscape A4 report (KPIs, chart, employee table, page numbers), labeled with the original uploaded filename. Suggested names are `Daily Attendance Dashboard YYYY-MM-DD.pdf` and `Monthly Attendance Dashboard YYYY-MM.pdf`. PDF export is available in the Windows build.

Daily reports and monthly IN/OUT Total Hours use the punch interval whenever both punches exist, including overnight shifts. Those report exports use a positive reported Attended duration only when a punch pair is missing; dashboard average hours includes complete pairs only. Monthly calculations retain whole minutes until export; numeric hours are displayed as decimals (8.50 means 8 hours 30 minutes). Monthly duty/OT limits apply separately and do not cap elapsed Total Hours; overtime never exceeds the available duration. Mandatory holidays retain their nine-hour payroll credit; IN/OUT retains actual complete punches and elapsed time, otherwise showing the credited shift. Average punch times account for midnight and overnight departures. PDF trends use panels of at most 31 dates so longer ranges remain readable.

Daily Excel and PDF reports include the company name and logo above the table and use the monthly palette: pastel headers, green IN punches, blue OUT punches, and yellow total hours. Remarks use green for Present, blue for Absent, and peach for an incomplete punch pair. The daily columns and calculations remain the same.

Template PDFs preserve the selected daily or monthly layout and include Holiday Details. Monthly PDFs offer **Full month (A1 landscape)**, selected by default in the app, or **Weekly (A4 landscape)**. Full month keeps every date and summary column together on a large page, with selectable text and vector table lines for sharp zooming. Larger employee lists continue vertically with repeated headers. Weekly shows seven days per section with repeated employee details and totals. Holiday Details uses standard A4 pages in either layout. Daily PDFs remain A4 landscape.

Cancelling the Windows Save As dialog creates no file. Other platforms save to the app's fallback directory.

Workbook and PDF saves write a complete temporary file before replacing the destination. If generation or saving fails, an existing report is preserved.

## Supported inputs

The attendance software exports HTML tables with an `.xls` extension. Three layouts are recognised (auto-detected):

| Export | Layout |
| --- | --- |
| Daily Report | one row per employee |
| Monthly report (block style) | one block of rows per employee, days as columns |
| Monthly Performance report | metric rows per employee, days as columns |

The `Sep Monthly Over View Report` contains attendance statuses but no punch times, so it cannot fill the monthly punch templates. Choose a detailed monthly report instead.

The importer handles every calendar month, including 28/29-day February and 30/31-day months, using the dates inside the export. Numeric dates use **day-month-year** (with `-`, `/`, or `.`), or ISO **year-month-day**; single-digit days/months and timestamps are accepted. Monthly dates are checked against the report's declared From/To range. Invalid dates and incomplete attendance rows produce an import error instead of silently dropping data or inventing dates.

Both monthly layouts can pad unused date slots with blanks or `-` (for example, the 31st slot in September). These trailing slots are ignored only when their attendance values are also empty or zero. Padding between numbered date columns and attendance data without a date still produce an error.

The app and CLI share decoding for Unicode BOMs and declared HTML character sets. Employee identifiers may contain letters or leading zeros. Monthly metric rows can appear before or after Status; conflicting repeated metrics are rejected. Leave totals include only the Leave header's columns.

Daily imports follow labelled columns even when repeated page headers change their order, and preserve overtime and status values. Valid 24-hour punches, seconds, and AM/PM punches are normalized to the template's minute precision. Malformed nonempty punches and durations report the employee, date, and field instead of silently becoming missing attendance. Durations accept decimal hours such as `1.5` or hours/minutes/seconds such as `01:30:00`; ambiguous comma values are rejected. Reported durations round to the nearest minute, with 30 seconds rounding up.

Dashboard and monthly summaries count identical employee/date records once. Conflicting records for the same employee/date produce a clear error instead of discarding a shift or counting two attendance days. The daily writer continues to preserve source rows.

Automated calendar regression tests cover every month from **2026 through 2036** (the current year plus ten future years) across all three import formats and both monthly workbook layouts. They check exact dates and punches, month lengths and weekdays, partial exports, skipped days at every month/year boundary, and acceptance or rejection of February 29. This validates the supported export layouts; each year's holidays and working Saturdays still come from the editable calendar.

Partial monthly exports count **only the exported dates** in attendance totals. Unreported dates stay blank, and the Remarks column shows the number of reported dates. Monthly templates accept one calendar month at a time; export separate files for ranges spanning multiple months. The parser can map day columns correctly across month/year boundaries, including skipped days.

Monthly attendance totals are always calculated from unique exported dates using per-date punches, status codes, and the configured holiday and weekly-off calendar. Employee-level source summary counts do not override these calculations. This prevents a 30-day export from producing more than 30 attendance days and removes repeated source-total inconsistency messages from Remarks. The app shows a neutral calculation note; when daily status codes are unavailable, absence and weekly off are inferred from punches and the calendar, and approved leave dates cannot be established from aggregate counts alone. Partial-export and missing-calendar warnings remain available.

The bundled holiday calendar is for **2026**. For another year, configure that year's holidays and working Saturdays in **Manage holidays**. If no calendar entries exist for the report year, the app and the workbook's Holiday Details sheet show a warning.

Working Saturday entries must fall on Saturdays. Calendar edits preserve changes to other entries from another app window; competing edits to the same entry require reloading the manager. Invalid calendar entries produce a validation error without replacing the saved calendar.

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

`tools/ConverterCli` is a console harness around the same parser/writer (`src/AttendanceCleaner.Core`), handy for testing against real exports:

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
src/AttendanceCleaner/    # the MAUI app
src/AttendanceCleaner.Core/ # shared parsing, reporting, calendar, and analytics library
tools/ConverterCli/       # console harness for the core logic
tools/AttendanceCleaner.Tests/  # test suite, run in CI
```

### Coding standards

The app, CLI, and tests reference one shared core library. Keep MAUI and platform
APIs in the app; put parsing, calculations, and report generation in the core.
Package versions live in `Directory.Packages.props`; nullable checks and .NET
analyzers are enabled across projects by `Directory.Build.props`.

Use `.editorconfig` formatting, descriptive names, and small functions with a single
purpose. Reuse `AttendanceTime` for normalized punch/duration arithmetic and
`SpreadsheetValues` for employee IDs. Preserve leading zeros and long identifiers.
Keep parsing and exported values independent of the machine's culture.

`TemplateSpec` defines daily layout rules. `MonthlyTemplateSpec` defines the
supplied monthly schedule, company labels, and holiday categories; duty limits,
shift labels, and holiday credits derive from that schedule. These are required
business rules, so changes should include behavior tests. Holiday dates are data:
edit `src/AttendanceCleaner.Core/Data/holidays-2026.json` for the bundled seed, or
use the app's holiday manager for an existing calendar. The 2026 seed is explicitly
year-specific; do not extrapolate movable holidays into other years.

Keep constants for meaningful shared rules, rather than extracting every one-off
layout value. Avoid duplicate implementations and delimiter-based composite keys.
Register shared resources once safely when used from concurrent callers.

Run these checks before submitting changes (MAUI workloads are unnecessary):

```bash
dotnet format whitespace src/AttendanceCleaner.Core/AttendanceCleaner.Core.csproj --verify-no-changes
dotnet format whitespace tools/ConverterCli/ConverterCli.csproj --verify-no-changes
dotnet format whitespace tools/AttendanceCleaner.Tests/AttendanceCleaner.Tests.csproj --verify-no-changes
dotnet build tools/ConverterCli --configuration Release -warnaserror
dotnet test tools/AttendanceCleaner.Tests --configuration Release -warnaserror
```

The `code-quality` workflow runs these checks on pull requests and pushes to main.
Platform builds and installer checks remain in the Windows workflows.
