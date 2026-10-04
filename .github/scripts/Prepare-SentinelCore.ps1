param([switch]$VerifyOnly, [string]$AssetsPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$pin = Get-Content -LiteralPath (Join-Path $root 'sentinelcore-packages.json') -Raw | ConvertFrom-Json
$feed = Join-Path $root '.sentinelcore-packages'
New-Item -ItemType Directory -Path $feed -Force | Out-Null
foreach ($package in $pin.packages) {
    $path = Join-Path $feed $package.file
    if (-not $VerifyOnly) {
        Invoke-WebRequest -Uri $package.url -OutFile $path
    }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing pinned package: $path" }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $package.sha256) { throw "Published package hash mismatch: $($package.file)" }
    $archive = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $nuspec = @($archive.Entries | Where-Object FullName -Like '*.nuspec')
        if ($nuspec.Count -ne 1) { throw "Invalid package metadata: $($package.file)" }
        $reader = [IO.StreamReader]::new($nuspec[0].Open())
        try { [xml]$metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ([string]$metadata.package.metadata.id -ne $package.id -or
            [string]$metadata.package.metadata.version -ne $pin.version) {
            throw "Wrong package id/version: $($package.file)"
        }
        $dll = @($archive.Entries | Where-Object { $_.FullName -like 'lib/*' -and $_.Name -eq $package.assembly })
        if ($dll.Count -ne 1) { throw "Missing/ambiguous shared assembly: $($package.file)" }
        Write-Host "Verified $($package.file) SHA256=$hash"
        Write-Host "Package contents: $($archive.Entries.FullName -join ', ')"
    }
    finally { $archive.Dispose() }
    if ($AssetsPath) {
        $assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json -AsHashtable
        $key = "$($package.id)/$($pin.version)"
        $library = $assets.libraries[$key]
        if ($null -eq $library) { throw "Restore did not resolve exact $key" }
        $sha512 = [Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData([IO.File]::ReadAllBytes($path)))
        if ($library.sha512 -ne $sha512) { throw "Restored content hash differs from published package: $key" }
        $other = @($assets.libraries.Keys | Where-Object { $_ -like "$($package.id)/*" -and $_ -ne $key })
        if ($other.Count -ne 0) { throw "Unexpected shared dependency versions: $other" }
        Write-Host "Restore verified $key against the published package content hash."
    }
}
