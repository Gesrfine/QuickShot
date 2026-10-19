param([switch]$SkipTests, [switch]$NativeFocusTests, [switch]$IncludeInstaller, [switch]$FastArchive, [switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Set-Location -LiteralPath $repoRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_NOLOGO = 'true'
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.tools\nuget\packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $repoRoot '.tools\nuget\http-cache'
$dotnet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
$restoreOptions = @()
if ($NoRestore) { $restoreOptions = @('--no-restore') }
if (-not (Test-Path -LiteralPath $dotnet)) {
    & (Join-Path $PSScriptRoot 'bootstrap-sdk.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'SDK bootstrap failed.' }
}
& node 'scripts/generate-assets.mjs'
if ($LASTEXITCODE -ne 0) { throw 'Asset generation failed.' }
if (-not $SkipTests) {
    & node --test 'tests/extension.test.mjs'
    if ($LASTEXITCODE -ne 0) { throw 'Extension tests failed.' }
    & node --test 'tests/connect-page.test.mjs'
    if ($LASTEXITCODE -ne 0) { throw 'Silent connection page checks failed.' }
    & $dotnet run --project 'tests/QuickShot.Core.Tests' --configuration Release @restoreOptions
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    & $dotnet run --project 'tests/QuickShot.Ui.Tests' --configuration Release @restoreOptions -- (Join-Path $repoRoot 'artifacts\ui-checks') --background
    if ($LASTEXITCODE -ne 0) { throw 'UI lifecycle and optional help checks failed.' }
    & $dotnet run --project 'tests/QuickShot.Runtime.Tests' --configuration Release @restoreOptions -- (Join-Path $repoRoot 'artifacts\runtime-checks') --background
    if ($LASTEXITCODE -ne 0) { throw 'Runtime event checks failed.' }
    & $dotnet run --project 'tests/QuickShot.Unregister.Tests' --configuration Release @restoreOptions
    if ($LASTEXITCODE -ne 0) { throw 'Portable layout and built-in connection removal checks failed.' }
    & node 'tests/browser.mjs'
    if ($LASTEXITCODE -ne 0) { throw 'Browser checks failed.' }
    & node 'tests/cold-start.mjs'
    if ($LASTEXITCODE -ne 0) { throw 'Cold-start browser checks failed.' }
    & node 'tests/cold-start.mjs' --late-navigation
    if ($LASTEXITCODE -ne 0) { throw 'Cold-start late-navigation browser checks failed.' }
    if ($NativeFocusTests) {
        & $dotnet build 'tests/QuickShot.App.Tests' --configuration Release @restoreOptions
        if ($LASTEXITCODE -ne 0) { throw 'Focus helper build failed.' }
        & node 'tests/native-focus.mjs'
        if ($LASTEXITCODE -ne 0) { throw 'Native focus checks failed.' }
    }
}
$artifactRoot = Join-Path $repoRoot 'artifacts'
$stagingRoot = Join-Path $artifactRoot 'staging'
$installerDirectory = Join-Path $stagingRoot 'installer'
$portableDirectory = Join-Path $stagingRoot 'portable'
$appDirectory = Join-Path $artifactRoot 'build\app'
New-Item -ItemType Directory -Path $appDirectory -Force | Out-Null
# Leave the executable's managed assemblies uncompressed so idle pages stay file-backed/shared.
# Compressing the single-file bundle cut disk size but more than doubled measured idle memory.
& $dotnet publish 'src/QuickShot.App/QuickShot.App.csproj' --configuration Release --runtime win-x64 --self-contained true --output $appDirectory @restoreOptions '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=false' '-p:DebugType=None' '-p:DebugSymbols=false'
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
$stages = @($portableDirectory)
if ($IncludeInstaller) { $stages += $installerDirectory }
foreach ($stage in $stages) {
    $checkedStage = [IO.Path]::GetFullPath($stage)
    if ([IO.Path]::GetDirectoryName($checkedStage) -ne $stagingRoot -or [IO.Path]::GetFileName($checkedStage) -notin @('installer','portable')) { throw 'Unsafe package staging path.' }
    if (-not $checkedStage.StartsWith($repoRoot + '\artifacts\staging\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Staging path escaped the workspace.' }
    if (Test-Path -LiteralPath $checkedStage) {
        if ((Get-Item -LiteralPath $checkedStage).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package staging cannot be a reparse point.' }
        # Only rebuild these exact, checked workspace staging directories, never an installed or portable user copy.
        Remove-Item -LiteralPath $checkedStage -Recurse -Force
    }
    New-Item -ItemType Directory -Path $checkedStage -Force | Out-Null
    # Publish output can retain obsolete files from earlier builds. Ship only the required executable.
    if ($checkedStage -eq $portableDirectory) {
        Copy-Item -LiteralPath (Join-Path $appDirectory 'QuickShot.exe') -Destination $checkedStage -Force
        Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\使用说明.html') -Destination $checkedStage -Force
    }
    else {
        New-Item -ItemType Directory -Path (Join-Path $checkedStage 'app') -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $appDirectory 'QuickShot.exe') -Destination (Join-Path $checkedStage 'app') -Force
        Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\使用说明.html') -Destination (Join-Path $checkedStage 'app') -Force
        foreach ($name in 'Uninstall.ps1','Uninstall.cmd','Start.cmd') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $checkedStage -Force }
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'extension') -Destination $checkedStage -Recurse -Force
}
$archive = Join-Path $artifactRoot 'QuickShot-Portable-win-x64.zip'
$payloadArchive = Join-Path $artifactRoot 'build\QuickShot-installer-payload.zip'
$setupExecutable = Join-Path $artifactRoot 'QuickShot-Setup-win-x64.exe'
$zipTool = $null
if (-not $FastArchive) {
    $zipTool = & (Join-Path $PSScriptRoot 'bootstrap-zip.ps1')
    if (-not $zipTool -or -not (Test-Path -LiteralPath $zipTool -PathType Leaf)) { throw 'Pinned ZIP compression tool is missing.' }
}
function New-DistributionArchive([string]$SourceDirectory, [string]$Destination) {
    # Both paths produce ordinary Deflate ZIP files readable by Windows Explorer.
    # Compress only the download: the executable and its runtime components stay unchanged.
    $temporaryArchive = $Destination + '.building.zip'
    try {
        if (Test-Path -LiteralPath $temporaryArchive) { Remove-Item -LiteralPath $temporaryArchive -Force }
        if ($FastArchive) {
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $level = [IO.Compression.CompressionLevel]::Optimal
            if ('SmallestSize' -in [Enum]::GetNames([IO.Compression.CompressionLevel])) {
                $level = [Enum]::Parse([IO.Compression.CompressionLevel], 'SmallestSize')
            }
            [IO.Compression.ZipFile]::CreateFromDirectory($SourceDirectory, $temporaryArchive, $level, $false, [Text.Encoding]::UTF8)
        }
        else {
            Push-Location -LiteralPath $SourceDirectory
            try {
                # Spend more build time reducing the download; this never changes the executable or runtime memory.
                $zipOutput = & $zipTool a -tzip $temporaryArchive '*' -mm=Deflate -mx=9 -mfb=258 -mpass=15 -mmt=1 -mcu=on -bd -bb0 2>&1
                if ($LASTEXITCODE -ne 0) { throw ('Distribution ZIP compression failed: ' + ($zipOutput -join [Environment]::NewLine)) }
            }
            finally { Pop-Location }
        }
        if (-not (Test-Path -LiteralPath $temporaryArchive -PathType Leaf)) { throw 'Distribution ZIP was not produced.' }
        Move-Item -LiteralPath $temporaryArchive -Destination $Destination -Force
    }
    finally { if (Test-Path -LiteralPath $temporaryArchive) { Remove-Item -LiteralPath $temporaryArchive -Force } }
}
New-DistributionArchive $portableDirectory $archive
if (-not (Test-Path -LiteralPath $archive)) { throw 'Portable distribution package is required.' }
if ($IncludeInstaller) {
    # The future installer still reads these setup inputs. They are not part of the portable package.
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md'),(Join-Path $repoRoot 'extension-id.txt') -Destination $installerDirectory -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.ps1') -Destination $installerDirectory -Force
    New-DistributionArchive $installerDirectory $payloadArchive
    & (Join-Path $PSScriptRoot 'build-setup.ps1') -PayloadArchive $payloadArchive -OutputPath $setupExecutable
    if (-not (Test-Path -LiteralPath $setupExecutable)) { throw 'Installer generation failed.' }
}
# Retire the obsolete mixed archive after the replacement portable package exists.
$legacyArchive = [IO.Path]::GetFullPath((Join-Path $artifactRoot 'QuickShot-win-x64.zip'))
if ([IO.Path]::GetDirectoryName($legacyArchive) -ne $artifactRoot) { throw 'Unsafe legacy archive path.' }
if (Test-Path -LiteralPath $legacyArchive) { Remove-Item -LiteralPath $legacyArchive -Force }
if (-not $SkipTests) {
    & (Join-Path $repoRoot 'tests\distributions-smoke.ps1') -IncludeInstaller:$IncludeInstaller
    if ($IncludeInstaller) {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'tests\portable-uninstall-smoke.ps1')
        if ($LASTEXITCODE -ne 0) { throw 'Legacy script removal checks failed.' }
        $previousUninstallSource = $env:QUICKSHOT_UNINSTALL_SOURCE
        try {
            $env:QUICKSHOT_UNINSTALL_SOURCE = $installerDirectory
            & node 'tests/uninstaller-launch.mjs'
            if ($LASTEXITCODE -ne 0) { throw 'Installed uninstaller launch checks failed.' }
        }
        finally { $env:QUICKSHOT_UNINSTALL_SOURCE = $previousUninstallSource }
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'tests\setup-smoke.ps1') -SetupPath $setupExecutable
        if ($LASTEXITCODE -ne 0) { throw 'Standalone setup checks failed.' }
    }
}
Write-Output "Portable: $archive"
if ($IncludeInstaller) { Write-Output "Setup: $setupExecutable" }

