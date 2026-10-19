param([string]$Version = '10.0.402')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolsDirectory = Join-Path $repoRoot '.tools'
$sdkDirectory = Join-Path $toolsDirectory 'dotnet'
if (Test-Path -LiteralPath (Join-Path $sdkDirectory "sdk\$Version\Sdks\Microsoft.NET.Sdk\Sdk\Sdk.props")) { & (Join-Path $sdkDirectory 'dotnet.exe') --version; exit $LASTEXITCODE }
New-Item -ItemType Directory -Path $sdkDirectory -Force | Out-Null
if ($Version -eq '10.0.402') {
    & node (Join-Path $PSScriptRoot 'download-sdk.mjs')
    if ($LASTEXITCODE -ne 0) { throw 'SDK download failed.' }
    $archive = Join-Path $toolsDirectory 'dotnet-sdk-10.0.402.zip'
    & tar.exe -xf $archive -C $sdkDirectory
    if ($LASTEXITCODE -ne 0) { throw 'SDK extraction failed.' }
    & (Join-Path $sdkDirectory 'dotnet.exe') --version
    exit $LASTEXITCODE
}
$metadata = Invoke-RestMethod -Uri 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json'
$sdk = $metadata.releases | ForEach-Object { $_.sdks } | Where-Object { $_.version -eq $Version } | Select-Object -First 1
if (-not $sdk) { $sdk = $metadata.releases | ForEach-Object { $_.sdk } | Where-Object { $_.version -eq $Version } | Select-Object -First 1 }
$file = $sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name.EndsWith('.zip') } | Select-Object -First 1
if (-not $file) { throw "Cannot locate Microsoft .NET SDK $Version (win-x64)." }
$archive = Join-Path $toolsDirectory 'dotnet-sdk.zip'
Invoke-WebRequest -Uri $file.url -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $file.hash) { throw 'SDK SHA512 mismatch.' }
Expand-Archive -LiteralPath $archive -DestinationPath $sdkDirectory -Force
& (Join-Path $sdkDirectory 'dotnet.exe') --version
exit $LASTEXITCODE

