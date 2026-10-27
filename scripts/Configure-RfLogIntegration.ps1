param(
    [Parameter(Mandatory = $true)][string]$BackendPath,
    [ValidateRange(0, 2147483647)][int]$CompanyId = 0,
    [ValidatePattern('^[a-zA-Z0-9-]{1,60}$')][string]$ClientId = 'drive-app',
    [int]$UserId = 0,
    [ValidateSet('IN', 'TW')][string]$ClientRegion = 'IN',
    [string]$Endpoint = 'https://s3-noi.aces3.ai',
    [string]$Bucket = 'dcra-bucket',
    [string]$Region = 'ap-south-noi-1',
    [string]$ReportingWebhookUrl = '',
    [switch]$ExternalReporting
)
$ErrorActionPreference = 'Stop'
$resolvedBackend = (Resolve-Path -LiteralPath $BackendPath).Path
$webConfigPath = Join-Path $resolvedBackend 'web.config'
if (!(Test-Path -LiteralPath (Join-Path $resolvedBackend 'SignalTracker.dll')) -or !(Test-Path -LiteralPath $webConfigPath)) {
    throw 'BackendPath must be the published backend directory containing SignalTracker.dll and web.config.'
}
if (([uri]$Endpoint).Scheme -ne 'https') { throw 'S3 endpoint must use HTTPS.' }
if ($ReportingWebhookUrl -and ([uri]$ReportingWebhookUrl).Scheme -ne 'https') { throw 'Webhook must use HTTPS.' }

function Read-SecretText([string]$Prompt) {
    $secretValue = Read-Host $Prompt -AsSecureString
    return [System.Net.NetworkCredential]::new('', $secretValue).Password
}
function New-IntegrationKey {
    $randomBytes = New-Object byte[] 32
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($randomBytes) } finally { $generator.Dispose() }
    return [Convert]::ToBase64String($randomBytes)
}

# Deploy-RfLogIntegration.ps1 applies the schema to BOTH regional databases before starting the pool.
# Secrets are read interactively, never put in command history or source control.
$storageAccessKey = Read-SecretText 'Enter the NEW rotated S3 access key'
$storageSecretKey = Read-SecretText 'Enter the NEW rotated S3 secret key'
if (!$storageAccessKey -or !$storageSecretKey) { throw 'Storage credentials are required.' }
$uploaderKey = New-IntegrationKey
$reportingKey = New-IntegrationKey
$signingSecret = New-IntegrationKey

[xml]$webConfiguration = Get-Content -LiteralPath $webConfigPath -Raw
$aspNetCore = $webConfiguration.SelectSingleNode('/configuration/system.webServer/aspNetCore')
if (!$aspNetCore) { throw 'aspNetCore element is missing in web.config.' }
$variables = $aspNetCore.SelectSingleNode('environmentVariables')
if (!$variables) {
    $variables = $webConfiguration.CreateElement('environmentVariables')
    [void]$aspNetCore.AppendChild($variables)
}
$values = @{
    'RfLogIntegration__Enabled' = 'true'
    'RfLogIntegration__Endpoint' = $Endpoint
    'RfLogIntegration__Bucket' = $Bucket
    'RfLogIntegration__Region' = $Region
    'RfLogIntegration__ForcePathStyle' = 'true'
    'RfLogIntegration__AccessKey' = $storageAccessKey
    'RfLogIntegration__SecretKey' = $storageSecretKey
    'RfLogIntegration__ReportingApiKey' = $reportingKey
    'RfLogIntegration__ReportingWebhookUrl' = $ReportingWebhookUrl
    'RfLogIntegration__WebhookSigningSecret' = $signingSecret
    'RfLogIntegration__AutomaticReportsEnabled' = if ($ExternalReporting) { 'false' } else { 'true' }
    'RfLogIntegration__ReportEmailsEnabled' = 'true'
}
if ($CompanyId -gt 0) {
    $values["RfLogIntegration__Clients__${ClientId}__ApiKey"] = $uploaderKey
    $values["RfLogIntegration__Clients__${ClientId}__CompanyId"] = [string]$CompanyId
    $values["RfLogIntegration__Clients__${ClientId}__Region"] = $ClientRegion
}
if ($UserId -gt 0) { $values["RfLogIntegration__Clients__${ClientId}__UserId"] = [string]$UserId }
foreach ($name in $values.Keys) {
    $variable = $variables.SelectSingleNode("environmentVariable[@name='$name']")
    if (!$variable) {
        $variable = $webConfiguration.CreateElement('environmentVariable')
        $variable.SetAttribute('name', $name)
        [void]$variables.AppendChild($variable)
    }
    $variable.SetAttribute('value', $values[$name])
}
$webConfiguration.Save($webConfigPath)
$storageAccessKey = $null
$storageSecretKey = $null
Write-Host 'IIS integration settings saved. Saving web.config recycles the application.'
Write-Host 'Store the following application credentials securely; do not paste them into chat or commit them:'
if ($CompanyId -gt 0) {
    Write-Host "Optional machine uploader client ID: $ClientId"
    Write-Host "Optional machine uploader API key: $uploaderKey"
} else {
    Write-Host 'Uploads use the existing app login cookie. User/company are derived from verified claims.'
}
Write-Host "Reporting API key: $reportingKey"
Write-Host "Webhook signing secret: $signingSecret"
Write-Host 'These are application credentials. Never distribute the S3 access/secret keys to the app or reporting client.'
