param([switch]$NoRestore, [switch]$Browser, [switch]$NativeFocus, [switch]$Registration, [ValidateSet('Core', 'Runtime', 'Ui')][string[]]$Suites = @('Core', 'Runtime', 'Ui'), [switch]$SkipExtension)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $repoRoot
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.tools\nuget\packages'
$env:DOTNET_ROOT = Join-Path $repoRoot '.tools\dotnet'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
$restoreOptions = @()
if ($NoRestore) { $restoreOptions = @('--no-restore') }
if (-not $SkipExtension) {
    & node --test tests/extension.test.mjs tests/connect-page.test.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Extension tests failed.' }
}
foreach ($project in $Suites) {
    & $dotnet run --project ('tests/QuickShot.' + $project + '.Tests') -c Release @restoreOptions -- (Join-Path $repoRoot ('artifacts\2.0-' + $project)) --background
    if ($LASTEXITCODE -ne 0) { throw ($project + ' checks failed.') }
}
if ($Browser) {
    & node tests/browser.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Browser checks failed.' }
    & node tests/cold-start.mjs --late-navigation
    if ($LASTEXITCODE -ne 0) { throw 'Cold-start checks failed.' }
}
if ($NativeFocus) {
    & $dotnet build tests/QuickShot.App.Tests -c Release @restoreOptions
    if ($LASTEXITCODE -ne 0) { throw 'Focus helper build failed.' }
    & node tests/native-focus.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Native focus checks failed.' }
}
if ($Registration) {
    & $dotnet run --project tests/QuickShot.Unregister.Tests -c Release @restoreOptions
    if ($LASTEXITCODE -ne 0) { throw 'Isolated connection-removal checks failed.' }
}
