param(
    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "Version must contain four numeric components: $Version"
}

$entries = @(Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json)
$entry = $entries | Where-Object { $_.InternalName -eq 'SRankSentinel' } | Select-Object -First 1
if ($null -eq $entry) {
    throw 'repo.json does not contain the SRankSentinel entry.'
}

$assetUrl = "https://github.com/MarshalTitan/SRankSentinel/releases/download/v$Version/SRankSentinel.zip"
$entry.AssemblyVersion = $Version
$entry.TestingAssemblyVersion = $Version
$entry.DalamudApiLevel = 15
$entry.TestingDalamudApiLevel = 15
$entry.DownloadLinkInstall = $assetUrl
$entry.DownloadLinkUpdate = $assetUrl
$entry.DownloadLinkTesting = $assetUrl
$entry.IconUrl = 'https://raw.githubusercontent.com/MarshalTitan/SRankSentinel/main/assets/icon.png'
$entry.IsHide = $false
$entry.IsTestingExclusive = $false
$entry.LastUpdate = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()

$json = ConvertTo-Json -InputObject @($entries) -Depth 20
[System.IO.File]::WriteAllText(
    (Resolve-Path -LiteralPath $ManifestPath),
    $json + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false)
)

Write-Host "Updated repo.json for SRankSentinel $Version."
