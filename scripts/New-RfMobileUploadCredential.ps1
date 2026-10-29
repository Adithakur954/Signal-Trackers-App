# Local IIS-server provisioning. No portal login or HTTP request is performed.
param(
    [Parameter(Mandatory = $true)][string]$MobileNumber,
    [Parameter(Mandatory = $true)][string]$CompanyCode,
    [Parameter(Mandatory = $true)][string]$DeviceLabel,
    [string]$BackendPath = 'C:\inetpub\wwwroot\Backend',
    [ValidateSet('IN', 'TW')][string]$Region = 'IN',
    [ValidateRange(1, 365)][int]$ValidDays = 90,
    [ValidateRange(1, 2147483647)][int]$UserId
)
$ErrorActionPreference = 'Stop'
$operatorIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $isAdministrator = ([Security.Principal.WindowsPrincipal]::new($operatorIdentity)).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
} finally { $operatorIdentity.Dispose() }
if (!$isAdministrator) { throw 'Open PowerShell as Administrator on the backend server. No portal email/password is needed.' }
$backendRoot = (Resolve-Path -LiteralPath $BackendPath).Path
$assemblyPath = Join-Path $backendRoot 'SignalTracker.dll'
$webConfigPath = Join-Path $backendRoot 'web.config'
if (!(Test-Path -LiteralPath $assemblyPath) -or !(Test-Path -LiteralPath $webConfigPath)) {
    throw 'BackendPath must be the deployed API folder containing SignalTracker.dll and web.config.'
}
# Old binaries must not start the web host when given an unknown CLI flag.
$assemblyBytes = [IO.File]::ReadAllBytes($assemblyPath)
$supportsCommand = [Text.Encoding]::Unicode.GetString($assemblyBytes).Contains('--rf-mobile-credential-create')
if (!$supportsCommand -and $assemblyBytes.Length -gt 1) {
    $supportsCommand = [Text.Encoding]::Unicode.GetString($assemblyBytes, 1, $assemblyBytes.Length - 1).Contains('--rf-mobile-credential-create')
}
if (!$supportsCommand) { throw 'Deploy the updated backend with the server-local credential command first. Replacing the script alone is insufficient.' }
$null = Get-Command dotnet -ErrorAction Stop
[xml]$web = Get-Content -LiteralPath $webConfigPath -Raw
$hosting = $web.SelectSingleNode('/configuration/system.webServer/aspNetCore')
if (!$hosting) { throw 'The deployed web.config has no aspNetCore hosting element.' }
$savedEnvironment = @{}
try {
    # IIS variables are not otherwise visible to this CLI subprocess.
    foreach ($item in $hosting.SelectNodes('environmentVariables/environmentVariable')) {
        $name = $item.GetAttribute('name')
        if (!$savedEnvironment.ContainsKey($name)) {
            $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        }
        [Environment]::SetEnvironmentVariable($name, $item.GetAttribute('value'), 'Process')
    }
    $commandArgs = @($assemblyPath, '--rf-mobile-credential-create', '--environment', 'Production',
        '--rf-mobile-number', $MobileNumber, '--rf-mobile-company-code', $CompanyCode,
        '--rf-mobile-device-label', $DeviceLabel, '--rf-mobile-region', $Region,
        '--rf-mobile-valid-days', [string]$ValidDays)
    if ($PSBoundParameters.ContainsKey('UserId')) { $commandArgs += @('--rf-mobile-user-id', [string]$UserId) }
    Write-Host 'Creating an installation-specific upload credential using the server database settings.'
    Write-Host 'The uploadToken is shown once. Transfer it privately; do not log, commit or share it in chat.'
    Push-Location -LiteralPath $backendRoot
    try {
        & dotnet @commandArgs
        if ($LASTEXITCODE -ne 0) { throw 'Credential creation failed. Follow the error above; no portal login is required.' }
    } finally { Pop-Location }
} finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
    $web = $null
}
