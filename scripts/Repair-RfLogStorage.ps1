# Run in administrator PowerShell on the IIS server, against the live API folder.
# Repairs storage settings only. Existing reporting/uploader keys are preserved.
param(
    [Parameter(Mandatory = $true)][string]$BackendPath,
    [string]$Endpoint = 'https://s3-noi.aces3.ai',
    [string]$Bucket = 'dcra-bucket',
    [string]$Region = 'ap-south-noi-1',
    [switch]$ReplaceCredentials
)
$ErrorActionPreference = 'Stop'
$backendRoot = (Resolve-Path -LiteralPath $BackendPath).Path
$configPath = Join-Path $backendRoot 'web.config'
$assemblyPath = Join-Path $backendRoot 'SignalTracker.dll'
if (!(Test-Path -LiteralPath $configPath) -or !(Test-Path -LiteralPath $assemblyPath)) {
    throw 'BackendPath must be the live published API folder containing web.config and SignalTracker.dll.'
}
$endpointUri = $null
if (![uri]::TryCreate($Endpoint, [UriKind]::Absolute, [ref]$endpointUri) -or
    $endpointUri.Scheme -ne 'https' -or $endpointUri.Query -or $endpointUri.UserInfo -or
    [string]::IsNullOrWhiteSpace($Bucket) -or [string]::IsNullOrWhiteSpace($Region)) {
    throw 'Provide an HTTPS storage endpoint without a query/credentials, a bucket and a signing region.'
}
# Older binaries must not accidentally start the web host instead of a CLI check.
$assemblyBytes = [IO.File]::ReadAllBytes($assemblyPath)
$hasStorageCheck = [Text.Encoding]::Unicode.GetString($assemblyBytes).Contains('--rf-log-storage-check')
if (!$hasStorageCheck -and $assemblyBytes.Length -gt 1) {
    $hasStorageCheck = [Text.Encoding]::Unicode.GetString($assemblyBytes, 1, $assemblyBytes.Length - 1).Contains('--rf-log-storage-check')
}
if (!$hasStorageCheck) {
    throw 'The deployed API lacks the storage-check command. Deploy the current RF release first.'
}
$null = Get-Command dotnet -ErrorAction Stop
[xml]$web = Get-Content -LiteralPath $configPath -Raw
$hosting = $web.SelectSingleNode('/configuration/system.webServer/aspNetCore')
if (!$hosting) { throw 'The live web.config has no aspNetCore hosting element.' }
$originalConfigHash = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash
$variables = $hosting.SelectSingleNode('environmentVariables')
if (!$variables) {
    $variables = $web.CreateElement('environmentVariables')
    [void]$hosting.AppendChild($variables)
}
$savedEnvironment = @{}
function Set-CheckEnvironment([string]$Name, [string]$Value) {
    if (!$savedEnvironment.ContainsKey($Name)) {
        $savedEnvironment[$Name] = [Environment]::GetEnvironmentVariable($Name, 'Process')
    }
    [Environment]::SetEnvironmentVariable($Name, $Value, 'Process')
}
function Read-StorageSecret([string]$Prompt) {
    $secretValue = Read-Host $Prompt -AsSecureString
    return [Net.NetworkCredential]::new('', $secretValue).Password
}
try {
    # Reproduce the site's IIS environment for the verification subprocess.
    foreach ($item in $variables.SelectNodes('environmentVariable')) {
        Set-CheckEnvironment $item.GetAttribute('name') $item.GetAttribute('value')
    }
    $accessKey = [Environment]::GetEnvironmentVariable('RfLogIntegration__AccessKey', 'Process')
    $secretKey = [Environment]::GetEnvironmentVariable('RfLogIntegration__SecretKey', 'Process')
    if ([string]::IsNullOrWhiteSpace($accessKey)) { $accessKey = $env:AWS_ACCESS_KEY_ID }
    if ([string]::IsNullOrWhiteSpace($secretKey)) { $secretKey = $env:AWS_SECRET_ACCESS_KEY }
    if ($ReplaceCredentials -or [string]::IsNullOrWhiteSpace($accessKey) -or [string]::IsNullOrWhiteSpace($secretKey)) {
        # Ask for a matching pair if either credential is absent.
        $accessKey = Read-StorageSecret 'Enter the S3 access key (hidden)'
        $secretKey = Read-StorageSecret 'Enter the S3 secret key (hidden)'
    }
    if ([string]::IsNullOrWhiteSpace($accessKey) -or [string]::IsNullOrWhiteSpace($secretKey)) {
        throw 'Both S3 credentials are required. Live configuration was not changed.'
    }
    $settings = @{
        'RfLogIntegration__Enabled' = 'true'
        'RfLogIntegration__Endpoint' = $Endpoint
        'RfLogIntegration__Bucket' = $Bucket
        'RfLogIntegration__Region' = $Region
        'RfLogIntegration__ForcePathStyle' = 'true'
        'RfLogIntegration__AccessKey' = $accessKey
        'RfLogIntegration__SecretKey' = $secretKey
    }
    foreach ($name in $settings.Keys) { Set-CheckEnvironment $name $settings[$name] }
    Write-Host 'Checking S3 upload, verification, copy, report and download. Temporary check objects are removed.'
    Push-Location -LiteralPath $backendRoot
    try {
        & dotnet $assemblyPath --rf-log-storage-check --environment Production
        if ($LASTEXITCODE -ne 0) {
            throw 'Storage verification failed. Live configuration was not changed. Check the reported S3 error; use -ReplaceCredentials to enter a new pair.'
        }
    } finally { Pop-Location }
    if ((Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash -ne $originalConfigHash) {
        throw 'web.config changed during verification. Run the repair again to preserve the latest settings.'
    }
    $backupRoot = Join-Path $env:ProgramData ('SignalTracker\DeploymentBackups\rf-storage-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $backupRoot | Out-Null
    & icacls.exe $backupRoot /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to secure the configuration backup. Run as administrator. Live configuration was not changed.' }
    Copy-Item -LiteralPath $configPath -Destination (Join-Path $backupRoot 'web.config')
    foreach ($name in $settings.Keys) {
        $variable = $variables.SelectSingleNode("environmentVariable[@name='$name']")
        if (!$variable) {
            $variable = $web.CreateElement('environmentVariable')
            $variable.SetAttribute('name', $name)
            [void]$variables.AppendChild($variable)
        }
        $variable.SetAttribute('value', $settings[$name])
    }
    $web.Save($configPath)
    Write-Host 'Storage configuration repaired and S3 round trip passed. IIS reloads the API after web.config changes.'
    Write-Host 'Retry the final-drive upload after the API restarts. Reporting and uploader API keys were preserved.'
    Write-Host "Configuration backup: $backupRoot"
} finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
    $accessKey = $null
    $secretKey = $null
    $settings = $null
}
