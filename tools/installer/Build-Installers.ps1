param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$AppVersion,

    [string]$OutputDirectory = 'build-artifacts'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$outputPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

$compiler = Get-Command 'makensis.exe' -ErrorAction SilentlyContinue
$compilerPath = if ($compiler) { $compiler.Source } else { $null }
if (-not $compilerPath) {
    foreach ($candidate in @(
        'C:\Program Files (x86)\NSIS\makensis.exe',
        'C:\Program Files\NSIS\makensis.exe'
    )) {
        if (Test-Path $candidate) {
            $compilerPath = $candidate
            break
        }
    }
}
if (-not $compilerPath) {
    throw 'NSIS makensis.exe was not found. Install NSIS 3.13 before building installers.'
}

foreach ($architecture in @('x64', 'x86')) {
    $arguments = @(
        "/DAPP_VERSION=$AppVersion",
        "/DAPP_ARCH=$architecture",
        "/DOUTPUT_DIR=$outputPath",
        (Join-Path $PSScriptRoot 'AttendanceCleaner.nsi')
    )
    & $compilerPath @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "NSIS failed to build the $architecture installer (exit code $LASTEXITCODE)."
    }
}
