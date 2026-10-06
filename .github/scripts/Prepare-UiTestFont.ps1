param([string]$Destination = (Join-Path $env:RUNNER_TEMP 'Sentinel-FontAwesomeFreeSolid.otf'))
$ErrorActionPreference = 'Stop'
$url = 'https://raw.githubusercontent.com/goatcorp/DalamudAssets/a26502fb3320b852c3f35c40770f945a8c9f3c50/UIRes/FontAwesomeFreeSolid.otf'
Invoke-WebRequest -Uri $url -OutFile $Destination
if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne
    '72c42629cf97c33c19dd910185ea6aa95f34f27fb95f84ed15fe74359f910aaa') {
    throw 'The official Dalamud Font Awesome test fixture does not match its pinned hash.'
}
if ($env:GITHUB_ENV) {
    "DALAMUD_ICON_FONT=$Destination" | Out-File -LiteralPath $env:GITHUB_ENV -Append -Encoding utf8
}
Write-Host 'Verified the official Dalamud icon font fixture for native UI tests.'
