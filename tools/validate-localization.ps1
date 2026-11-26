[CmdletBinding()]
param(
    [string]$LocalizationRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($LocalizationRoot)) {
    $LocalizationRoot = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\src\PcMonitor\Localization'
}
$files = @('ru.json', 'en-GB.json', 'en-US.json', 'zh.json')
$maps = @{}

foreach ($file in $files) {
    $path = Join-Path $LocalizationRoot $file
    if (-not (Test-Path $path)) { throw "Missing localization file: $path" }
    $object = Get-Content -Raw -Encoding UTF8 $path | ConvertFrom-Json
    $map = @{}
    foreach ($property in $object.PSObject.Properties) { $map[[string]$property.Name] = [string]$property.Value }
    $maps[$file] = $map
}

$allKeys = [System.Collections.Generic.HashSet[string]]::new()
foreach ($map in $maps.Values) { foreach ($key in $map.Keys) { [void]$allKeys.Add([string]$key) } }
$failed = $false

foreach ($file in $files) {
    $missing = @($allKeys | Where-Object { -not $maps[$file].ContainsKey($_) })
    $empty = @($maps[$file].GetEnumerator() | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.Value) })
    if ($missing.Count -gt 0) {
        $failed = $true
        Write-Warning "${file}: missing $($missing.Count) key(s): $($missing -join ', ')"
    }
    if ($empty.Count -gt 0) {
        $failed = $true
        Write-Warning "${file}: empty translation value(s): $((@($empty | ForEach-Object Key) -join ', '))"
    }
    Write-Host "${file}: $($maps[$file].Count) entries"
}

if ($failed) { exit 1 }
Write-Host "Localization validation passed: $($allKeys.Count) shared keys across $($files.Count) languages." -ForegroundColor Green
