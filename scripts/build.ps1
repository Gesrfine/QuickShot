param([switch]$SkipTests, [switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $repoRoot
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.tools\nuget\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = 'true'
$dotnet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { & (Join-Path $PSScriptRoot 'bootstrap-sdk.ps1') }
$restoreOptions = @()
if ($NoRestore) { $restoreOptions = @('--no-restore') }
if (-not $SkipTests) {
    & (Join-Path $PSScriptRoot 'test.ps1') -NoRestore:$NoRestore
}
& node scripts/generate-assets.mjs
if ($LASTEXITCODE -ne 0) { throw 'Asset generation failed.' }
$version = ([xml](Get-Content -LiteralPath 'src\QuickShot.App\QuickShot.App.csproj' -Raw)).Project.PropertyGroup.Version
$publish = Join-Path $repoRoot ('artifacts\build\' + $version)
& $dotnet publish src/QuickShot.App -c Release -r win-x64 --self-contained false --output $publish @restoreOptions '-p:PublishSingleFile=true' '-p:UseAppHost=true' '-p:EnableCompressionInSingleFile=false' '-p:DebugType=None' '-p:DebugSymbols=false'
if ($LASTEXITCODE -ne 0) { throw 'Framework-dependent publish failed.' }
$stage = [IO.Path]::GetFullPath((Join-Path $repoRoot ('artifacts\staging\QuickShot-' + $version)))
if (-not $stage.StartsWith($repoRoot + '\artifacts\staging\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid staging directory.' }
if (Test-Path -LiteralPath $stage) {
    if ((Get-Item -LiteralPath $stage).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Staging cannot be a reparse point.' }
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $publish 'QuickShot.exe') -Destination $stage
Copy-Item -LiteralPath (Join-Path $repoRoot 'extension') -Destination $stage -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\使用教程.html') -Destination $stage
$archive = Join-Path $repoRoot ('artifacts\QuickShot-' + $version + '-win-x64.zip')
$temporary = $archive + '.building.zip'
if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
try {
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $temporary, [IO.Compression.CompressionLevel]::SmallestSize, $false, [Text.Encoding]::UTF8)
    Move-Item -LiteralPath $temporary -Destination $archive -Force
} finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
& (Join-Path $repoRoot 'tests\distributions-smoke.ps1') -PortableArchive $archive
if (-not $?) { throw 'Package validation failed.' }
Write-Output ('Portable: ' + $archive)
