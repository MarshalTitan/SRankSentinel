param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw "Package not found: $PackagePath"
}

if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "Version must contain four numeric components: $ExpectedVersion"
}

$staging = Join-Path $env:RUNNER_TEMP ("sentinelhunts-package-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null

try {
    Expand-Archive -LiteralPath $PackagePath -DestinationPath $staging -Force

    $requiredFiles = @(
        'SentinelHunts.dll',
        'SentinelHunts.Core.dll',
        'SentinelHunts.json',
        'SentinelHunts.deps.json'
    )

    foreach ($file in $requiredFiles) {
        $path = Join-Path $staging $file
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Package is missing required top-level file: $file"
        }
    }

    $nestedZip = Get-ChildItem -LiteralPath $staging -Recurse -File -Filter '*.zip' | Select-Object -First 1
    if ($null -ne $nestedZip) {
        throw "Package contains an unexpected nested ZIP: $($nestedZip.FullName)"
    }

    $manifestPath = Join-Path $staging 'SentinelHunts.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $expectedValues = [ordered]@{
        InternalName = 'SentinelHunts'
        Name = 'Sentinel Hunts'
        Author = 'MTitan'
        AssemblyVersion = $ExpectedVersion
        DalamudApiLevel = 15
    }

    foreach ($property in $expectedValues.Keys) {
        $actual = $manifest.$property
        $expected = $expectedValues[$property]
        if ($actual -ne $expected) {
            throw "Manifest field '$property' is '$actual'; expected '$expected'."
        }
    }

    foreach ($property in @('Punchline', 'Description')) {
        if ([string]::IsNullOrWhiteSpace([string]$manifest.$property)) {
            throw "Manifest field '$property' is required."
        }
    }

    Write-Host "Validated Sentinel Hunts $ExpectedVersion package layout and manifest."
}
finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}
