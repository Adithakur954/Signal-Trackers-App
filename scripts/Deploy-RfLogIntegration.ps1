param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$BackendPath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-zA-Z0-9 _.-]+$')][string]$AppPoolName,
    [switch]$ConfigureStorage,
    [string]$SigningRegion = 'ap-south-noi-1'
)
$ErrorActionPreference = 'Stop'
Import-Module WebAdministration
$packageRoot = (Resolve-Path -LiteralPath $PackagePath).Path
$backendRoot = (Resolve-Path -LiteralPath $BackendPath).Path
if ($packageRoot -eq $backendRoot -or
    $packageRoot.StartsWith($backendRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $backendRoot.StartsWith($packageRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'PackagePath and BackendPath must be separate, non-overlapping directories.'
}
if (!(Test-Path -LiteralPath (Join-Path $packageRoot 'SignalTracker.dll'))) { throw 'Package does not contain SignalTracker.dll.' }
if (!(Test-Path "IIS:\AppPools\$AppPoolName")) { throw 'Application Pool was not found.' }
if (!(Test-Path -LiteralPath (Join-Path $backendRoot 'appsettings.json'))) {
    throw 'Existing backend appsettings.json is required. Configure both regional DB connections before deployment.'
}
$backupRoot = Join-Path $env:ProgramData ('SignalTracker\DeploymentBackups\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
# Restrict copies of deployed settings to administrators and SYSTEM.
& icacls.exe $backupRoot /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not restrict deployment backup permissions.' }
Get-ChildItem -LiteralPath $backendRoot -File | Where-Object {
    $_.Name -eq 'web.config' -or $_.Name -like 'appsettings*.json'
} | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $backupRoot }

$wasRunning = (Get-WebAppPoolState -Name $AppPoolName).Value -eq 'Started'
if ($wasRunning) { Stop-WebAppPool -Name $AppPoolName }
try {
    $stopDeadline = (Get-Date).AddSeconds(30)
    while ((Get-WebAppPoolState -Name $AppPoolName).Value -ne 'Stopped') {
        if ((Get-Date) -gt $stopDeadline) { throw 'Application pool did not stop within 30 seconds. No binaries were copied.' }
        Start-Sleep -Milliseconds 250
    }
    # Preserve live configuration, uploads and key material. Do not delete old data.
    Get-ChildItem -LiteralPath $packageRoot -Force | Where-Object {
        $_.Name -notlike 'appsettings*.json' -and $_.Name -notin @('web.config', 'App_Data', 'UploadedExcels', 'logs')
    } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $backendRoot -Recurse -Force }
    $liveWebConfig = Join-Path $backendRoot 'web.config'
    if (!(Test-Path -LiteralPath $liveWebConfig)) {
        Copy-Item -LiteralPath (Join-Path $packageRoot 'web.config') -Destination $liveWebConfig
    }
    [xml]$web = Get-Content -LiteralPath $liveWebConfig -Raw
    $hosting = $web.SelectSingleNode('/configuration/system.webServer/aspNetCore')
    if (!$hosting) { throw 'aspNetCore element missing from live web.config.' }
    $hosting.SetAttribute('processPath', 'dotnet')
    $hosting.SetAttribute('arguments', '.\SignalTracker.dll')
    $web.Save($liveWebConfig)

    if ($ConfigureStorage) {
        $configureScript = Join-Path $PSScriptRoot 'Configure-RfLogIntegration.ps1'
        & $configureScript -BackendPath $backendRoot -Region $SigningRegion
        [xml]$web = Get-Content -LiteralPath $liveWebConfig -Raw
        $hosting = $web.SelectSingleNode('/configuration/system.webServer/aspNetCore')
    }
    $pool = Get-Item "IIS:\AppPools\$AppPoolName"
    $identity = switch ([string]$pool.processModel.identityType) {
        'SpecificUser' { [string]$pool.processModel.userName }
        'NetworkService' { 'NT AUTHORITY\NETWORK SERVICE' }
        'LocalService' { 'NT AUTHORITY\LOCAL SERVICE' }
        'LocalSystem' { 'NT AUTHORITY\SYSTEM' }
        default { "IIS AppPool\$AppPoolName" }
    }
    foreach ($folderName in @('logs','App_Data','UploadedExcels')) {
        $folder = Join-Path $backendRoot $folderName
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        & icacls.exe $folder /grant "${identity}:(OI)(CI)M" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Could not grant app-pool access to $folderName." }
    }
    # web.config environment variables normally exist only under IIS. Import
    # them for this CLI subprocess, then restore the operator's environment.
    $savedEnvironment = @{}
    try {
        foreach ($item in $hosting.SelectNodes('environmentVariables/environmentVariable')) {
            $name = $item.GetAttribute('name')
            $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            [Environment]::SetEnvironmentVariable($name, $item.GetAttribute('value'), 'Process')
        }
        Push-Location -LiteralPath $backendRoot
        try {
            & dotnet '.\SignalTracker.dll' --rf-log-db-migrate --environment Production
            if ($LASTEXITCODE -ne 0) { throw 'Regional RF migration failed. Pool remains stopped; inspect configuration and retry.' }
            & dotnet '.\SignalTracker.dll' --rf-log-storage-check --environment Production
            if ($LASTEXITCODE -ne 0) { throw 'Ace Cloud storage check failed. Pool remains stopped; inspect the printed check result and retry.' }
        } finally { Pop-Location }
    } finally {
        foreach ($name in $savedEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
        }
    }
    # Keep queued jobs active even while no browser has the site open.
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name startMode -Value AlwaysRunning
    Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
    Start-WebAppPool -Name $AppPoolName
    Write-Host 'Backend deployed. Both regional RF tables and the live Ace Cloud storage round trip passed.'
    Write-Host 'Open the site once to start the hosted workers. Enable IIS Application Initialization/preload for automatic startup after server restarts.'
    Write-Host "Configuration backup: $backupRoot"
} catch {
    Write-Error "Deployment did not finish. Backend pool may be stopped. Configuration backup: $backupRoot. $($_.Exception.Message)"
    throw
}
