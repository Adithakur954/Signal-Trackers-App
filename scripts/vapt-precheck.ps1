param(
    [string]$ProjectPath = "SignalTracker.csproj"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "Running VAPT precheck..."

$secretPatterns = @(
    "Password\s*=",
    "password\s*=",
    "apikey\s*=",
    "SMS_API_KEY`"\s*:\s*`"[^`"]+",
    "Redis`"\s*:\s*`"[^`"]+",
    "Stracer12345",
    "Taiwan123",
    "Amit@",
    "34645a"
)

$configFiles = @(
    "appsettings.json",
    "appsettings.Production.example.json"
)

$secretFailures = @()
foreach ($file in $configFiles) {
    if (-not (Test-Path $file)) { continue }
    foreach ($pattern in $secretPatterns) {
        $matches = Select-String -Path $file -Pattern $pattern -SimpleMatch:$false
        if ($matches) {
            $secretFailures += $matches
        }
    }
}

if ($secretFailures.Count -gt 0) {
    Write-Host "Potential committed secret found:" -ForegroundColor Red
    $secretFailures | ForEach-Object { Write-Host "  $($_.Path):$($_.LineNumber)" -ForegroundColor Red }
    exit 1
}

$weakPasswordChecks = Select-String -Path "Controllers\*.cs","Services\*.cs" -Pattern "\.password\s*[!=]=\s*[^=]|Sha256Hash" -CaseSensitive:$true
if ($weakPasswordChecks) {
    Write-Host "Potential weak password check/hash found:" -ForegroundColor Red
    $weakPasswordChecks | ForEach-Object { Write-Host "  $($_.Path):$($_.LineNumber): $($_.Line.Trim())" -ForegroundColor Red }
    exit 1
}

dotnet build $ProjectPath -nologo -p:UseAppHost=false -o "bin\_vapt_precheck_build"
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Remove-Item -Recurse -Force "bin\_vapt_precheck_build"

Write-Host "VAPT precheck passed." -ForegroundColor Green
