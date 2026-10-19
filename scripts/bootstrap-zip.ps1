param()
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$zipRepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$zipToolsRoot = Join-Path $zipRepoRoot '.tools\7zip\26.04'
$zipExtractRoot = Join-Path $zipToolsRoot 'extra'
$zipCli = Join-Path $zipExtractRoot 'x64\7za.exe'
$zipCliHash = '15d4c788c148e3677e2fc1c4a01f191fb6669eba4f6acf8a465f0f1a6f1e1260'

function Test-ZipToolHash([string]$Path, [string]$ExpectedHash) {
    return ((Test-Path -LiteralPath $Path -PathType Leaf) -and
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $ExpectedHash)
}

function Get-VerifiedZipAsset([string]$Name, [string]$ExpectedHash) {
    $destination = Join-Path $zipToolsRoot $Name
    if (Test-ZipToolHash $destination $ExpectedHash) { return $destination }
    $partial = $destination + '.' + [Guid]::NewGuid().ToString('N') + '.partial'
    try {
        Write-Host ('Downloading official 7-Zip build tool: ' + $Name)
        Invoke-WebRequest -Uri ('https://github.com/ip7z/7zip/releases/download/26.04/' + $Name) -OutFile $partial -UseBasicParsing
        if (-not (Test-ZipToolHash $partial $ExpectedHash)) { throw ('7-Zip asset SHA256 mismatch: ' + $Name) }
        Move-Item -LiteralPath $partial -Destination $destination -Force
        return $destination
    }
    finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force } }
}

# Reuse only the exact executable extracted from the pinned, digest-checked official release.
# No system installation or registry changes; this tool and its license never enter the distribution ZIP.
if ((Test-ZipToolHash $zipCli $zipCliHash) -and
    (Test-Path -LiteralPath (Join-Path $zipExtractRoot 'License.txt') -PathType Leaf) -and
    (Test-Path -LiteralPath (Join-Path $zipExtractRoot 'readme.txt') -PathType Leaf)) {
    Write-Output $zipCli
    return
}
New-Item -ItemType Directory -Path $zipToolsRoot -Force | Out-Null
$zipExtra = Get-VerifiedZipAsset '7z2604-extra.7z' 'dc4b11d3399db18b063630137145f5585d8f7ac847bf3639bd1185d7d1f7cee0'
$zipExtractor = Get-VerifiedZipAsset '7zr.exe' '256feca8e274e5da655e2a284fabafd9f554365eb164862089dacd4e8276d282'
$zipExtractOutput = & $zipExtractor x $zipExtra ('-o' + $zipExtractRoot) -y 'x64/7za.exe' 'License.txt' 'readme.txt' 2>&1
if ($LASTEXITCODE -ne 0) { throw ('7-Zip build tool extraction failed: ' + ($zipExtractOutput -join [Environment]::NewLine)) }
if (-not (Test-ZipToolHash $zipCli $zipCliHash)) { throw 'Extracted 7-Zip x64 executable SHA256 mismatch.' }
foreach ($zipDocument in @('License.txt','readme.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $zipExtractRoot $zipDocument) -PathType Leaf)) { throw ('7-Zip build tool is missing ' + $zipDocument) }
}
Write-Output $zipCli
