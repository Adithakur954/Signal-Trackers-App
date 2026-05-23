Get-ChildItem -Path . -Filter *.cs -Recurse | ForEach-Object {
    $path = $_.FullName
    $text = Get-Content -Raw -Path $path

    $text = [regex]::Replace($text, '\bex\.InnerException\s*!=\s*null\s*\?\s*ex\.InnerException\.Message\s*:\s*ex\.Message', 'SafeException.GetInnermost(ex)')
    $text = [regex]::Replace($text, '\bex\.InnerException\?\.Message\b', 'SafeException.Get(ex?.InnerException)')
    $text = [regex]::Replace($text, '\bex\.Message\b', 'SafeException.Get(ex)')

    Set-Content -Path $path -Value $text -Encoding UTF8
}
Write-Host 'Sanitize complete.'
