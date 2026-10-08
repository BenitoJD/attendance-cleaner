param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$AppVersion,

    [string]$InstallerDirectory = 'build-artifacts'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$installerRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $InstallerDirectory))
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\Attendance Cleaner'
$startMenuShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Attendance Cleaner\Attendance Cleaner.lnk'
$uninstallSubKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\AttendanceCleaner'

foreach ($architecture in @('x64', 'x86')) {
    $installer = Join-Path $installerRoot "AttendanceCleaner-Setup-$architecture.exe"
    if (-not (Test-Path $installer)) {
        throw "Missing $architecture installer: $installer"
    }
    if (Test-Path $installDirectory) {
        throw "A previous Attendance Cleaner install exists at $installDirectory."
    }

    Write-Host "Installing and smoke-testing the $architecture setup..."
    $setup = Start-Process -FilePath $installer -ArgumentList '/S' -Wait -PassThru
    if ($setup.ExitCode -ne 0) {
        throw "$architecture setup failed with exit code $($setup.ExitCode)."
    }

    $appExecutable = Join-Path $installDirectory 'AttendanceCleaner.exe'
    $uninstaller = Join-Path $installDirectory 'Uninstall.exe'
    if (-not (Test-Path $appExecutable)) {
        throw "$architecture setup did not install AttendanceCleaner.exe to $installDirectory."
    }
    if (-not (Test-Path $uninstaller)) {
        throw "$architecture setup did not install its uninstaller."
    }
    if (-not (Test-Path $startMenuShortcut)) {
        throw "$architecture setup did not create the Start menu shortcut."
    }
    $registryView = if ($architecture -eq 'x64') {
        [Microsoft.Win32.RegistryView]::Registry64
    } else {
        [Microsoft.Win32.RegistryView]::Registry32
    }
    $registryHive = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::CurrentUser,
        $registryView
    )
    $uninstallEntry = $registryHive.OpenSubKey($uninstallSubKey)
    if (-not $uninstallEntry) {
        $registryHive.Dispose()
        throw "$architecture setup did not register an Add/Remove Programs entry."
    }
    $installedVersion = $uninstallEntry.GetValue('DisplayVersion')
    $uninstallEntry.Dispose()
    $registryHive.Dispose()
    if ($installedVersion -ne $AppVersion) {
        throw "Installed version '$installedVersion' does not match '$AppVersion'."
    }

    $app = Start-Process -FilePath $appExecutable -PassThru
    $windowOpened = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Seconds 1
        $app.Refresh()
        if ($app.HasExited) { break }
        if ($app.MainWindowHandle -ne 0) {
            $windowOpened = $true
            break
        }
    }
    if (-not $windowOpened) {
        if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
        throw "Installed $architecture app did not open a window."
    }
    Stop-Process -Id $app.Id -Force

    $remove = Start-Process -FilePath $uninstaller -ArgumentList '/S' -Wait -PassThru
    if ($remove.ExitCode -ne 0) {
        throw "$architecture uninstaller failed with exit code $($remove.ExitCode)."
    }
    if (Test-Path $installDirectory) {
        throw "$architecture uninstaller left the application install folder behind."
    }
    if (Test-Path $startMenuShortcut) {
        throw "$architecture uninstaller left the Start menu shortcut behind."
    }
    $registryHive = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::CurrentUser,
        $registryView
    )
    $uninstallEntry = $registryHive.OpenSubKey($uninstallSubKey)
    $registrationRemains = $null -ne $uninstallEntry
    if ($uninstallEntry) { $uninstallEntry.Dispose() }
    $registryHive.Dispose()
    if ($registrationRemains) {
        throw "$architecture uninstaller left the Add/Remove Programs entry behind."
    }

    Write-Host "$architecture install, launch, and uninstall passed."
}
