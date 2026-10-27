param(
    [Parameter(Mandatory = $true)][string]$BackendPath,
    [Parameter(Mandatory = $true)][ValidateSet('IN','TW')][string]$Region,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-zA-Z0-9.-]+$')][string]$SmtpHost,
    [ValidateRange(1,65535)][int]$Port = 587,
    [Parameter(Mandatory = $true)][string]$FromAddress,
    [Parameter(Mandatory = $true)][string]$UserName,
    [bool]$EnableSsl = $true
)
$ErrorActionPreference = 'Stop'
$backendRoot = (Resolve-Path -LiteralPath $BackendPath).Path
$webConfigPath = Join-Path $backendRoot 'web.config'
if (!(Test-Path -LiteralPath (Join-Path $backendRoot 'SignalTracker.dll')) -or !(Test-Path -LiteralPath $webConfigPath)) {
    throw 'BackendPath must be the published backend directory with SignalTracker.dll and web.config.'
}
$sender = [System.Net.Mail.MailAddress]::new($FromAddress)
if ($sender.Address -ne $FromAddress -or $UserName.Contains("`r") -or $UserName.Contains("`n")) {
    throw 'Supply one plain sender address and a valid SMTP username.'
}
$privatePassword = Read-Host "Enter the SMTP password/app password for $Region (not your S3 secret)" -AsSecureString
$passwordText = [System.Net.NetworkCredential]::new('', $privatePassword).Password
if (!$passwordText) { throw 'SMTP password is required.' }
try {
    [xml]$web = Get-Content -LiteralPath $webConfigPath -Raw
    $hosting = $web.SelectSingleNode('/configuration/system.webServer/aspNetCore')
    if (!$hosting) { throw 'aspNetCore element is missing from web.config.' }
    $variables = $hosting.SelectSingleNode('environmentVariables')
    if (!$variables) {
        $variables = $web.CreateElement('environmentVariables')
        [void]$hosting.AppendChild($variables)
    }
    $prefix = "RfLogIntegration__Smtp__${Region}__"
    $values = @{
        "${prefix}Host" = $SmtpHost
        "${prefix}Port" = [string]$Port
        "${prefix}UserName" = $UserName
        "${prefix}Password" = $passwordText
        "${prefix}FromAddress" = $FromAddress
        "${prefix}EnableSsl" = $EnableSsl.ToString().ToLowerInvariant()
        'RfLogIntegration__ReportEmailsEnabled' = 'true'
    }
    foreach ($name in $values.Keys) {
        $variable = $variables.SelectSingleNode("environmentVariable[@name='$name']")
        if (!$variable) {
            $variable = $web.CreateElement('environmentVariable')
            $variable.SetAttribute('name', $name)
            [void]$variables.AppendChild($variable)
        }
        $variable.SetAttribute('value', $values[$name])
    }
    $web.Save($webConfigPath)
    Write-Host "SMTP settings saved for $Region only. Protect deployed web.config with administrator/app-pool permissions. Saving web.config recycles the app."
    Write-Host 'This command does not send a test email. Register approved recipients before registering their drive uploads.'
} finally {
    $passwordText = $null
    $privatePassword = $null
}
