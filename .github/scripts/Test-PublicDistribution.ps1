param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryManifestUrl,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion
)

$ErrorActionPreference = 'Stop'

if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "Version must contain four numeric components: $ExpectedVersion"
}

$testRoot = Join-Path $env:RUNNER_TEMP ("sranksentinel-fresh-install-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null

try {
    $manifestPath = Join-Path $testRoot 'repo.json'
    $cacheBuster = [Uri]::EscapeDataString("$ExpectedVersion-$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())")
    $separator = if ($RepositoryManifestUrl.Contains('?')) { '&' } else { '?' }
    $manifestRequestUrl = "$RepositoryManifestUrl${separator}freshInstall=$cacheBuster"

    $entry = $null
    for ($attempt = 1; $attempt -le 12; $attempt++) {
        Invoke-WebRequest -Uri $manifestRequestUrl -OutFile $manifestPath
        $entries = @(Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)
        $entry = $entries | Where-Object { $_.InternalName -eq 'SRankSentinel' } | Select-Object -First 1
        if ($null -ne $entry -and $entry.AssemblyVersion -eq $ExpectedVersion) {
            break
        }

        if ($attempt -eq 12) {
            $publishedVersion = if ($null -eq $entry) { '<missing>' } else { $entry.AssemblyVersion }
            throw "Public repo.json still reports '$publishedVersion'; expected '$ExpectedVersion'."
        }

        Start-Sleep -Seconds 5
    }

    $expectedAssetUrl = "https://github.com/MarshalTitan/SRankSentinel/releases/download/v$ExpectedVersion/SRankSentinel.zip"
    foreach ($property in @('DownloadLinkInstall', 'DownloadLinkUpdate', 'DownloadLinkTesting')) {
        if ($entry.$property -ne $expectedAssetUrl) {
            throw "repo.json field '$property' is '$($entry.$property)'; expected '$expectedAssetUrl'."
        }
    }

    if ($entry.DalamudApiLevel -ne 15) {
        throw "repo.json DalamudApiLevel is '$($entry.DalamudApiLevel)'; expected '15'."
    }

    if ($entry.IconUrl -ne 'https://raw.githubusercontent.com/MarshalTitan/SRankSentinel/main/assets/icon.png') {
        throw "repo.json IconUrl is incorrect: $($entry.IconUrl)"
    }

    $iconPath = Join-Path $testRoot 'icon.png'
    Invoke-WebRequest -Uri $entry.IconUrl -OutFile $iconPath
    $iconBytes = [System.IO.File]::ReadAllBytes($iconPath)
    if ($iconBytes.Length -lt 24 -or $iconBytes[0] -ne 0x89 -or $iconBytes[1] -ne 0x50 -or $iconBytes[2] -ne 0x4E -or $iconBytes[3] -ne 0x47) {
        throw 'The public icon is not a valid PNG file.'
    }

    $iconWidth = [System.Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($iconBytes, 16))
    $iconHeight = [System.Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($iconBytes, 20))
    if ($iconWidth -ne 512 -or $iconHeight -ne 512) {
        throw "The public icon is ${iconWidth}x${iconHeight}; expected 512x512."
    }

    $packagePath = Join-Path $testRoot 'SRankSentinel.zip'
    Invoke-WebRequest -Uri $entry.DownloadLinkInstall -OutFile $packagePath -MaximumRedirection 10

    & (Join-Path $PSScriptRoot 'Validate-Package.ps1') `
        -PackagePath $packagePath `
        -ExpectedVersion $ExpectedVersion

    $emptyInstallDirectory = Join-Path $testRoot 'empty-plugin-directory'
    New-Item -ItemType Directory -Path $emptyInstallDirectory | Out-Null
    Expand-Archive -LiteralPath $packagePath -DestinationPath $emptyInstallDirectory -Force

    $topLevelNames = @(Get-ChildItem -LiteralPath $emptyInstallDirectory | ForEach-Object Name)
    foreach ($required in @('SRankSentinel.dll', 'SRankSentinel.json', 'SRankSentinel.deps.json')) {
        if ($required -notin $topLevelNames) {
            throw "Fresh extraction did not produce required top-level file: $required"
        }
    }

    Write-Host "Fresh-install simulation passed for public SRankSentinel $ExpectedVersion."
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
