param([string]$KeysFile = 'consent-keys.json')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$keysPath = Join-Path $root (Join-Path '.work' $KeysFile)
$locDir = Join-Path $root 'src\PcMonitor\Localization'

$keys = Get-Content -LiteralPath $keysPath -Raw -Encoding UTF8 | ConvertFrom-Json
$map = [ordered]@{ 'ru' = 'ru.json'; 'en-US' = 'en-US.json'; 'en-GB' = 'en-GB.json'; 'zh' = 'zh.json' }
$utf8Bom = New-Object System.Text.UTF8Encoding($true)

foreach ($lang in $map.Keys) {
    $path = Join-Path $locDir $map[$lang]
    $text = [System.IO.File]::ReadAllText($path)
    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($p in $keys.$lang.PSObject.Properties) {
        if ($text -match ('"' + [regex]::Escape($p.Name) + '"\s*:')) { continue }
        $escaped = $p.Value.Replace('\', '\\').Replace('"', '\"')
        $lines.Add('  "' + $p.Name + '": "' + $escaped + '",')
    }
    if ($lines.Count -eq 0) { Write-Host "$lang : nothing to add"; continue }
    $idx = $text.LastIndexOf('}')
    $head = $text.Substring(0, $idx).TrimEnd()
    if (-not $head.EndsWith(',')) { $head = $head + ',' }
    $result = $head + "`r`n" + ($lines -join "`r`n") + "`r`n}" + "`r`n"
    $result = [regex]::Replace($result, ',(\s*)\}\s*$', '$1}' + "`r`n")
    [System.IO.File]::WriteAllText($path, $result, $utf8Bom)
    Write-Host "$lang : added $($lines.Count)"
}
