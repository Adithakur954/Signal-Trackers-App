param([ValidatePattern('^[a-zA-Z0-9-]+$')][string]$ReleaseName = 'rf-log-release')
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $sourceRoot "artifacts/$ReleaseName"
$backendOutput = Join-Path $releaseRoot 'backend'
if (Test-Path -LiteralPath $releaseRoot) {
    throw 'Release folder already exists. Choose a new ReleaseName or move the previous package.'
}
New-Item -ItemType Directory -Path $backendOutput -Force | Out-Null
Push-Location -LiteralPath $sourceRoot
try {
    & dotnet publish SignalTracker.csproj -c Release --no-restore -o $backendOutput -p:UseAppHost=false --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }

    # The server keeps its own live settings. Never distribute developer DB settings.
    Get-ChildItem -LiteralPath $backendOutput -File -Filter 'appsettings*.json' | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Force
    }
    foreach ($folder in @('scripts', 'docs', 'database')) {
        New-Item -ItemType Directory -Path (Join-Path $releaseRoot $folder) -Force | Out-Null
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Configure-RfLogIntegration.ps1') -Destination (Join-Path $releaseRoot 'scripts')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Repair-RfLogStorage.ps1') -Destination (Join-Path $releaseRoot 'scripts')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Configure-RfLogReportEmail.ps1') -Destination (Join-Path $releaseRoot 'scripts')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Deploy-RfLogIntegration.ps1') -Destination (Join-Path $releaseRoot 'scripts')
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'docs/rf-log-integration.md') -Destination (Join-Path $releaseRoot 'docs')
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'docs/rf-log-integration.http') -Destination (Join-Path $releaseRoot 'docs')
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'docs/rf-log-developer-handoff.md') -Destination (Join-Path $releaseRoot 'docs')
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'docs/rf-app-developer-handoff.md') -Destination (Join-Path $releaseRoot 'docs')
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'docs/rf-app-single-api.md') -Destination (Join-Path $releaseRoot 'docs')
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'docs/rf-frontend-developer-handoff.md') -Destination (Join-Path $releaseRoot 'docs')
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'docs/rf-client-integration.http') -Destination (Join-Path $releaseRoot 'docs')
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'docs/rf-drive-auto-report.md') -Destination (Join-Path $releaseRoot 'docs')
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'docs/get-network-log-wifi.md') -Destination (Join-Path $releaseRoot 'docs')
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'docs/wifi-plot-import.md') -Destination (Join-Path $releaseRoot 'docs')
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'docs/rf-report-mailbox-setup.md') -Destination (Join-Path $releaseRoot 'docs')
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'Database/Migrations/003_rf_log_upload_events.sql') -Destination (Join-Path $releaseRoot 'database')
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'Database/Migrations/004_rf_log_report_email.sql') -Destination (Join-Path $releaseRoot 'database')
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'Database/Migrations/005_rf_log_company_email.sql') -Destination (Join-Path $releaseRoot 'database')
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'appsettings.Production.example.json') -Destination $releaseRoot
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'docs/rf-log-release-README.txt') -Destination (Join-Path $releaseRoot 'README.txt')
    $zipPath = Join-Path $sourceRoot "artifacts/$ReleaseName.zip"
    if (Test-Path -LiteralPath $zipPath) { throw 'Release ZIP already exists. Choose a new ReleaseName or move the old ZIP.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($releaseRoot, $zipPath)
    Get-FileHash -LiteralPath $zipPath -Algorithm SHA256 | Format-List
    Write-Host "Release package: $zipPath"
} finally { Pop-Location }
