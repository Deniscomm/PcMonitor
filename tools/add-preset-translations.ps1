[CmdletBinding()]
param(
    [string]$LocalizationRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($LocalizationRoot)) {
    $LocalizationRoot = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\src\PcMonitor\Localization'
}

$entries = @(
    @{ Key = 'Low — 1280 × 720'; Ru = 'Низкое — 1280 × 720'; En = 'Low — 1280 × 720'; Zh = '低 — 1280 × 720' },
    @{ Key = 'Medium — 1920 × 1080'; Ru = 'Среднее — 1920 × 1080'; En = 'Medium — 1920 × 1080'; Zh = '中 — 1920 × 1080' },
    @{ Key = 'High — 2560 × 1440'; Ru = 'Высокое — 2560 × 1440'; En = 'High — 2560 × 1440'; Zh = '高 — 2560 × 1440' },
    @{ Key = 'Extreme — 3840 × 2160'; Ru = 'Максимальное — 3840 × 2160'; En = 'Extreme — 3840 × 2160'; Zh = '极高 — 3840 × 2160' },
    @{ Key = 'Low (720p)'; Ru = 'Низкий (720p)'; En = 'Low (720p)'; Zh = '低 (720p)' },
    @{ Key = 'Medium (1080p)'; Ru = 'Средний (1080p)'; En = 'Medium (1080p)'; Zh = '中 (1080p)' },
    @{ Key = 'High (1440p)'; Ru = 'Высокий (1440p)'; En = 'High (1440p)'; Zh = '高 (1440p)' },
    @{ Key = 'Extreme (4K)'; Ru = 'Максимальный (4K)'; En = 'Extreme (4K)'; Zh = '极高 (4K)' }
)

$files = @{ 'ru.json' = 'Ru'; 'en-GB.json' = 'En'; 'en-US.json' = 'En'; 'zh.json' = 'Zh' }

foreach ($file in $files.Keys) {
    $path = Join-Path $LocalizationRoot $file
    $text = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
    $map = $text | ConvertFrom-Json
    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $entries) {
        if ($null -ne $map.PSObject.Properties[$entry.Key]) { continue }
        $value = $entry[$files[$file]]
        $lines.Add(('  "{0}": "{1}"' -f $entry.Key, $value))
    }
    if ($lines.Count -eq 0) { Write-Host "${file}: nothing to add"; continue }

    $trimmed = $text.TrimEnd()
    if (-not $trimmed.EndsWith('}')) { throw "Unexpected JSON tail in $path" }
    $body = $trimmed.Substring(0, $trimmed.Length - 1).TrimEnd()
    $builder = [System.Text.StringBuilder]::new()
    [void]$builder.Append($body)
    if (-not $body.EndsWith(',')) { [void]$builder.Append(',') }
    [void]$builder.Append([Environment]::NewLine)
    [void]$builder.Append(($lines -join (',' + [Environment]::NewLine)))
    [void]$builder.Append([Environment]::NewLine)
    [void]$builder.Append('}')
    [void]$builder.Append([Environment]::NewLine)

    $result = $builder.ToString()
    $null = $result | ConvertFrom-Json
    [System.IO.File]::WriteAllText($path, $result, [System.Text.UTF8Encoding]::new($false))
    Write-Host "${file}: added $($lines.Count) entries"
}
