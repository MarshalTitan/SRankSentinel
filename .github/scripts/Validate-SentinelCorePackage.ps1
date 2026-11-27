param([Parameter(Mandatory = $true)][string]$StagingPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
& (Join-Path $PSScriptRoot 'Prepare-SentinelCore.ps1') -VerifyOnly
$pin = Get-Content -LiteralPath (Join-Path $root 'sentinelcore-packages.json') -Raw | ConvertFrom-Json
$developmentFiles = @(Get-ChildItem -LiteralPath $StagingPath -Recurse -File | Where-Object {
    $_.Extension -in @('.exe', '.pdb', '.xml', '.nupkg', '.cs', '.csproj', '.sln', '.slnx') -or
    $_.Name -match '(?i)UiTests|StateTests|testhost|cimgui|Dalamud\.Bindings|LocalValidation|packages\.lock'
})
if ($developmentFiles.Count) { throw "ZIP contains development-only files: $($developmentFiles.Name -join ', ')" }
$deps = Get-Content -LiteralPath (Join-Path $StagingPath 'SRankSentinel.deps.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($package in $pin.packages) {
    $dllPath = Join-Path $StagingPath $package.assembly
    if (-not (Test-Path -LiteralPath $dllPath -PathType Leaf)) { throw "ZIP missing $($package.assembly)" }
    $version = [Reflection.AssemblyName]::GetAssemblyName($dllPath).Version.ToString()
    if ($version -ne '0.4.1.0') { throw "Wrong assembly version for $($package.assembly): $version" }
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $root ".sentinelcore-packages/$($package.file)"))
    try {
        $entry = @($archive.Entries | Where-Object { $_.FullName -like 'lib/*' -and $_.Name -eq $package.assembly })[0]
        $stream = $entry.Open()
        try { $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        $actual = (Get-FileHash -LiteralPath $dllPath -Algorithm SHA256).Hash
        if ($actual -ne $expected) { throw "Bundled shared assembly differs from published package: $($package.assembly)" }
        Write-Host "Bundled $($package.assembly) version=$version SHA256=$actual"
    }
    finally { $archive.Dispose() }
    $key = "$($package.id)/$($pin.version)"
    if (-not $deps.libraries.ContainsKey($key)) { throw "Runtime dependency manifest missing $key" }
    $targets = @($deps.targets.Values | ForEach-Object { $_[$key] })
    if (-not ($targets | Where-Object { $_ -and @($_.runtime.Keys | Where-Object { $_ -like "*/$($package.assembly)" }).Count -eq 1 })) {
        throw "Runtime dependency does not include $($package.assembly)"
    }
}
$unexpected = @($deps.libraries.Keys | Where-Object { $_ -like 'MarshalTitan.SentinelCore*/*' -and $_ -notin @($pin.packages | ForEach-Object { "$($_.id)/$($pin.version)" }) })
if ($unexpected.Count) { throw "Unexpected SentinelCore package dependencies: $unexpected" }
if (Test-Path -LiteralPath (Join-Path $StagingPath 'SentinelCore.json')) { throw 'SentinelCore must be a bundled library, not an installed plugin.' }
Write-Host "ZIP contents: $((Get-ChildItem -LiteralPath $StagingPath -File | Sort-Object Name).Name -join ', ')"
