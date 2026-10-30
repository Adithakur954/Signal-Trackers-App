# Run locally on the backend server as Windows Administrator. No portal login.
param(
    [Parameter(Mandatory = $true)][guid]$EnrollmentId,
    [ValidateSet('IN','TW')][string]$Region = 'IN',
    [ValidateSet('approve','deny')][string]$Decision = 'approve',
    [string]$BackendPath = 'C:\inetpub\wwwroot\Backend'
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
$supportsCommand = [Text.Encoding]::Unicode.GetString($assemblyBytes).Contains('--rf-mobile-enrollment-review')
if (!$supportsCommand -and $assemblyBytes.Length -gt 1) {
    $supportsCommand = [Text.Encoding]::Unicode.GetString($assemblyBytes, 1, $assemblyBytes.Length - 1).Contains('--rf-mobile-enrollment-review')
}
if (!$supportsCommand) { throw 'Deploy the updated backend with the server-local enrollment-review command first. Replacing the script alone is insufficient.' }
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
    $commandArgs = @($assemblyPath, '--rf-mobile-enrollment-review', '--environment', 'Production',
        '--rf-mobile-enrollment-id', $EnrollmentId.ToString('N'), '--rf-mobile-region', $Region,
        '--rf-mobile-enrollment-decision', $Decision)
    Write-Host 'Review only a recognized physical installation. Confirm its displayed device verification code first.'
    Write-Host 'Saving device approval/denial. No upload token will be printed or manually shared.'
    Push-Location -LiteralPath $backendRoot
    try {
        & dotnet @commandArgs
        if ($LASTEXITCODE -ne 0) { throw 'Device review failed. Follow the error above; no portal login is required.' }
    } finally { Pop-Location }
} finally {
    foreach ($name in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
    $web = $null
}
