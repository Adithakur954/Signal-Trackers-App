param(
    [int]$Port = 5224
)

$ErrorActionPreference = "Stop"

Set-Location (Resolve-Path "$PSScriptRoot\..")

$listeners = netstat -ano |
    Select-String ":$Port\s+.*LISTENING\s+(\d+)" |
    ForEach-Object { [int]$_.Matches[0].Groups[1].Value } |
    Sort-Object -Unique

foreach ($pid in $listeners) {
    Write-Host "Stopping existing backend listener on port $Port (PID $pid)..."
    Stop-Process -Id $pid -Force -ErrorAction SilentlyContinue
}

Write-Host "Building backend..."
dotnet build SignalTracker.csproj -nologo -p:UseAppHost=false

Write-Host "Starting backend on http://0.0.0.0:$Port"
Write-Host "Keep this PowerShell window open while using the app."
dotnet run --project SignalTracker.csproj --urls "http://0.0.0.0:$Port"
