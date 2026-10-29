param(
    [Parameter(Mandatory = $true)][string]$MobileNumber,
    [Parameter(Mandatory = $true)][string]$CompanyCode,
    [Parameter(Mandatory = $true)][string]$DeviceLabel,
    [string]$BaseUrl = 'https://s-traccceer.vinfocom.co.in',
    [ValidateSet('IN', 'TW')][string]$Region = 'IN',
    [ValidateRange(1, 365)][int]$ValidDays = 90,
    [ValidateRange(1, 2147483647)][int]$UserId
)
$ErrorActionPreference = 'Stop'
$apiUri = $null
if (![uri]::TryCreate($BaseUrl, [UriKind]::Absolute, [ref]$apiUri) -or
    $apiUri.Scheme -ne 'https' -or $apiUri.UserInfo -or $apiUri.Query -or $apiUri.Fragment -or $apiUri.AbsolutePath -ne '/') {
    throw 'BaseUrl must be the HTTPS backend origin without a path, credentials, query or fragment.'
}
$BaseUrl = $BaseUrl.TrimEnd('/')
$adminEmail = Read-Host 'Company administrator portal email'
$adminPassword = Read-Host 'Company administrator portal password' -AsSecureString
$loginBody = $null
$issueResponse = $null
$result = $null
try {
    $loginBody = @{
        email = $adminEmail
        password = [Net.NetworkCredential]::new('', $adminPassword).Password
        country_code = $Region
    } | ConvertTo-Json -Compress
    # No ForceLogin: this tool must not silently take over an existing portal session.
    $null = Invoke-WebRequest -Uri "$BaseUrl/api/auth/login" -Method Post -ContentType 'application/json' `
        -Body $loginBody -SessionVariable rfAdminSession -UseBasicParsing -MaximumRedirection 0
    $loginBody = $null
    $csrf = $rfAdminSession.Cookies.GetCookies($apiUri) | Where-Object Name -eq 'st.csrf' | Select-Object -First 1
    if (!$csrf) { throw 'Login did not return the st.csrf cookie. No mobile credential was requested.' }
    $requestBody = @{
        mobileNumber = $MobileNumber
        companyCode = $CompanyCode
        deviceLabel = $DeviceLabel
        validDays = $ValidDays
    }
    if ($PSBoundParameters.ContainsKey('UserId')) { $requestBody.userId = $UserId }
    $issueResponse = Invoke-WebRequest -Uri "$BaseUrl/api/rf-logs/mobile-credentials" -Method Post `
        -ContentType 'application/json' -Headers @{ 'X-CSRF-TOKEN' = $csrf.Value } `
        -Body ($requestBody | ConvertTo-Json -Compress) -WebSession $rfAdminSession -UseBasicParsing -MaximumRedirection 0
    $result = $issueResponse.Content | ConvertFrom-Json
    Write-Host 'Installation credential issued. Transfer it privately to this app installation; do not commit it or put it in the APK.'
    Write-Host ('Credential ID: ' + $result.credential.credentialId)
    Write-Host ('User ID: ' + $result.credential.userId)
    Write-Host ('Company ID: ' + $result.credential.companyId)
    Write-Host ('Region: ' + $result.credential.region)
    Write-Host ('Expires: ' + $result.credential.expiresAtUtc)
    # This one-time output is intentional. The server stores only its digest.
    Write-Host ('Upload token (shown once): ' + $result.uploadToken)
} finally {
    $loginBody = $null
    $adminPassword = $null
    $result = $null
    $issueResponse = $null
    if ($null -ne $rfAdminSession) {
        try {
            $csrf = $rfAdminSession.Cookies.GetCookies($apiUri) | Where-Object Name -eq 'st.csrf' | Select-Object -First 1
            $null = Invoke-WebRequest -Uri "$BaseUrl/api/auth/logout" -Method Post -WebSession $rfAdminSession `
                -Headers @{ 'X-CSRF-TOKEN' = $csrf.Value } -UseBasicParsing -MaximumRedirection 0
        } catch { Write-Warning 'Could not close the provisioning portal session; sign out through the portal.' }
    }
}
