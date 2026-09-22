param(
    [switch]$SkipDependencyAudit
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$buildDirectory = Join-Path $root 'artifacts/vapt-build'
$reportDirectory = Join-Path $root 'artifacts/vapt-baseline'
$project = Join-Path $root 'CallAnalyzerRegression/CallAnalyzerRegression.csproj'
$runner = Join-Path $buildDirectory 'CallAnalyzerRegression.dll'

function Invoke-RecordedDotnet {
    param([string[]]$Arguments, [string]$LogName)
    $logPath = Join-Path $reportDirectory $LogName
    & dotnet @Arguments *> $logPath
    $commandExit = $LASTEXITCODE
    Get-Content -LiteralPath $logPath -Tail 16
    if ($commandExit -ne 0) {
        throw "dotnet command failed with exit code $commandExit. See $logPath"
    }
}

Push-Location $root
try {
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
    # Build to isolated output; never start the app or run startup database changes.
    Invoke-RecordedDotnet -Arguments @('build', $project, '-nologo', '-p:UseAppHost=false', '-o', $buildDirectory) -LogName 'build.log'
    Invoke-RecordedDotnet -Arguments @($runner, '--security-baseline', 'docs/security/endpoint-inventory.csv') -LogName 'security.log'
    Invoke-RecordedDotnet -Arguments @($runner, '--diagnostic-time-index') -LogName 'diagnostic.log'
    Invoke-RecordedDotnet -Arguments @($runner, '--site-csv', 'Template-Files/Site_Template.csv') -LogName 'site-csv.log'
    if (-not $SkipDependencyAudit) {
        Invoke-RecordedDotnet -Arguments @('list', 'SignalTracker.csproj', 'package', '--vulnerable', '--include-transitive') -LogName 'dependencies.log'
        Write-Host 'Review dependencies.log: this command can exit zero even when vulnerabilities are listed.'
    }
    Write-Host 'Baseline checks completed. This is not VAPT certification; review docs/security/BASELINE.md and the open findings.'
}
finally {
    Pop-Location
}
