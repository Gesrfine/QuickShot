param([string]$ReleaseRoot)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $ReleaseRoot) { $ReleaseRoot = Join-Path $repoRoot 'artifacts\staging\installer' }
$releaseRoot = [IO.Path]::GetFullPath($ReleaseRoot)
$testRoot = Join-Path $repoRoot ('artifacts\installer-test-' + [Guid]::NewGuid().ToString('N'))
if (-not $testRoot.StartsWith($repoRoot + '\artifacts\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test directory.' }
$previousTestRegistry = $env:QUICKSHOT_TEST_REGISTRY_ROOT
$env:QUICKSHOT_TEST_REGISTRY_ROOT = 'HKCU:\Software\QuickShot\Tests\' + [Guid]::NewGuid().ToString('N')
$registryPath = $env:QUICKSHOT_TEST_REGISTRY_ROOT + '\NativeHost'
$runKey = $env:QUICKSHOT_TEST_REGISTRY_ROOT + '\Run'
$realHost = 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.quickshot.edge'
$realRun = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
function Value([string]$Path, [string]$Name = '') { if (Test-Path -LiteralPath $Path) { (Get-Item -LiteralPath $Path).GetValue($Name) } }
$hostBefore = Value $realHost
$startupBefore = Value $realRun 'QuickShot'
$first = Join-Path $testRoot ('Custom ' + [char]0x5FEB + [char]0x6295 + ' install A')
$second = Join-Path $testRoot 'Custom install B'
$portable = Join-Path $testRoot 'Portable package'
function Check([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message }; Write-Output ('PASS: ' + $Message) }
try {
    & (Join-Path $releaseRoot 'Install.ps1') -InstallDirectory $first -NoLaunch
    $manifestPath = Join-Path $first 'native-host.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $id = (Get-Content -LiteralPath (Join-Path $releaseRoot 'extension-id.txt') -Raw).Trim()
    Check ($manifest.path -eq (Join-Path $first 'app\QuickShot.exe') -and $manifest.allowed_origins[0] -eq "chrome-extension://$id/") 'custom installation path and native manifest'
    Check ((Value $registryPath) -eq $manifestPath -and -not (Test-Path -LiteralPath (Join-Path $first 'portable.flag'))) 'installed copy has its own registration and installed data mode'
    Check (-not (Test-Path -LiteralPath (Join-Path $first 'Install.cmd')) -and -not (Test-Path -LiteralPath (Join-Path $first 'Install.ps1'))) 'installed copy contains no installer entry points'
    Check ($null -eq (Value $runKey 'QuickShot')) 'fresh installation leaves login startup off'
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -LiteralPath $runKey -Name QuickShot -Value ('"' + (Join-Path $first 'app\QuickShot.exe') + '" --background')
    & (Join-Path $releaseRoot 'Install.ps1') -InstallDirectory $second -NoLaunch
    Check ((Value $runKey 'QuickShot') -eq ('"' + (Join-Path $second 'app\QuickShot.exe') + '" --background')) 'moving installation migrates an existing opt-in startup path'
    & (Join-Path $releaseRoot 'Uninstall.ps1') -InstallDirectory $first
    Check (-not (Test-Path -LiteralPath $first) -and (Value $registryPath) -eq (Join-Path $second 'native-host.json')) 'uninstalling an old copy preserves the active connection'
    & (Join-Path $releaseRoot 'Uninstall.ps1') -InstallDirectory $second
    Check (-not (Test-Path -LiteralPath $second) -and -not (Test-Path -LiteralPath $registryPath) -and $null -eq (Value $runKey 'QuickShot')) 'installed uninstall removes its folder, connection and matching startup'
    New-Item -ItemType Directory -Path (Join-Path $portable 'app') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $portable 'extension') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $releaseRoot 'extension\manifest.json') -Destination (Join-Path $portable 'extension\manifest.json')
    foreach ($name in 'portable.flag','app\QuickShot.exe','native-host.json','keep-my-file.txt') { [IO.File]::WriteAllText((Join-Path $portable $name), 'test') }
    New-Item -Path $registryPath -Force | Out-Null
    Set-Item -LiteralPath $registryPath -Value (Join-Path $portable 'native-host.json')
    & (Join-Path $releaseRoot 'Uninstall.ps1') -InstallDirectory $portable
    Check ((Test-Path -LiteralPath (Join-Path $portable 'keep-my-file.txt')) -and -not (Test-Path -LiteralPath $registryPath)) 'portable uninstall unregisters without deleting the extracted folder'
    $unmarked = Join-Path $testRoot 'Unrelated folder'
    New-Item -ItemType Directory -Path $unmarked -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $unmarked 'keep.txt'), 'keep')
    $refused = $false
    try { & (Join-Path $releaseRoot 'Uninstall.ps1') -InstallDirectory $unmarked } catch { $refused = $true }
    Check ($refused -and (Test-Path -LiteralPath (Join-Path $unmarked 'keep.txt'))) 'unmarked folders are never removed'
    $refused = $false
    try { & (Join-Path $releaseRoot 'Install.ps1') -InstallDirectory $unmarked -NoLaunch } catch { $refused = $true }
    Check ($refused -and (Test-Path -LiteralPath (Join-Path $unmarked 'keep.txt'))) 'installation refuses nonempty unrelated folders'
    Check ((Value $realHost) -eq $hostBefore -and (Value $realRun 'QuickShot') -eq $startupBefore) 'real user registration and startup settings remain untouched by tests'
}
finally {
    foreach ($candidate in @($first, $second)) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'quickshot.install.json')) { & (Join-Path $releaseRoot 'Uninstall.ps1') -InstallDirectory $candidate }
    }
    # Every recursive removal is confined to the exact, newly created test namespaces above.
    if ($env:QUICKSHOT_TEST_REGISTRY_ROOT -notmatch '^HKCU:\\Software\\QuickShot\\Tests\\[a-zA-Z0-9-]{10,64}$') { throw 'Unsafe registry cleanup.' }
    if (Test-Path -LiteralPath $env:QUICKSHOT_TEST_REGISTRY_ROOT) { Remove-Item -LiteralPath $env:QUICKSHOT_TEST_REGISTRY_ROOT -Recurse -Force }
    $env:QUICKSHOT_TEST_REGISTRY_ROOT = $previousTestRegistry
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolvedTestRoot.StartsWith($repoRoot + '\artifacts\installer-test-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe filesystem cleanup.' }
    if (Test-Path -LiteralPath $resolvedTestRoot) { Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force }
}
