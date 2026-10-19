param()
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$testName = 'portable-uninstall-test-' + [Guid]::NewGuid().ToString('N')
$testRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot $testName))
$registryRoot = 'HKCU:\Software\QuickShot\Tests\' + [Guid]::NewGuid().ToString('N')
$hostKey = $registryRoot + '\NativeHost'
$runKey = $registryRoot + '\Run'
$realHostKey = 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.quickshot.edge'
$realRunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$oldTestRegistryRoot = $env:QUICKSHOT_TEST_REGISTRY_ROOT
$powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$script:checks = 0

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
    Write-Output ('PASS: ' + $Message)
}

function RegistryValueSnapshot([string]$Path, [string]$Name = '') {
    if (-not (Test-Path -LiteralPath $Path)) { return '<absent-key>' }
    $key = Get-Item -LiteralPath $Path
    try {
        if ($key.GetValueNames() -notcontains $Name) { return '<absent-value>' }
        return (($key.GetValueKind($Name).ToString()) + ':' + (ConvertTo-Json -InputObject $key.GetValue($Name) -Compress))
    }
    finally { $key.Dispose() }
}

function ReadValue([string]$Path, [string]$Name = '') {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $key = Get-Item -LiteralPath $Path
    try { return $key.GetValue($Name) }
    finally { $key.Dispose() }
}

function SnapshotFiles([string]$Directory, [switch]$ExcludeNativeManifest) {
    $lines = @(Get-ChildItem -LiteralPath $Directory -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($Directory.Length + 1)
        if (-not ($ExcludeNativeManifest -and $relative -eq 'native-host.json')) {
            $relative + '|' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
    return ($lines -join "`n")
}

function NewFixture([string]$Name, [string]$ManifestCase) {
    $fixture = Join-Path $testRoot $Name
    foreach ($folder in 'app','extension','data') { New-Item -ItemType Directory -Path (Join-Path $fixture $folder) -Force | Out-Null }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\Uninstall.ps1') -Destination (Join-Path $fixture 'Uninstall.ps1')
    [IO.File]::WriteAllText((Join-Path $fixture 'portable.flag'), 'test portable marker')
    # Text rather than a PE image: this fixture can never be launched as QuickShot.
    [IO.File]::WriteAllText((Join-Path $fixture 'app\QuickShot.exe'), 'NOT AN EXECUTABLE - portable uninstall test only')
    [IO.File]::WriteAllText((Join-Path $fixture 'data\keep.txt'), 'keep this portable cache')
    [IO.File]::WriteAllText((Join-Path $fixture 'native-host.json'), '{"name":"test-only"}')
    $manifest = Join-Path $fixture 'extension\manifest.json'
    switch ($ManifestCase) {
        'valid' { Copy-Item -LiteralPath (Join-Path $repoRoot 'extension\manifest.json') -Destination $manifest }
        'malformed' { [IO.File]::WriteAllText($manifest, '{ invalid JSON') }
        'wrong-key' { [IO.File]::WriteAllText($manifest, '{"manifest_version":3,"key":"bm90LXF1aWNrc2hvdA=="}') }
        'missing' { }
        default { throw 'Unknown portable uninstall fixture.' }
    }
    New-Item -Path $hostKey -Force | Out-Null
    Set-Item -LiteralPath $hostKey -Value (Join-Path $fixture 'native-host.json')
    New-Item -Path $runKey -Force | Out-Null
    Set-ItemProperty -LiteralPath $runKey -Name QuickShot -Value ('"' + (Join-Path $fixture 'app\QuickShot.exe') + '" --background')
    Set-ItemProperty -LiteralPath $runKey -Name OtherApplication -Value 'unrelated-test-registration'
    return $fixture
}

function InvokeFixtureUninstall([string]$Fixture) {
    $exe = Join-Path $Fixture 'app\QuickShot.exe'
    $owned = @(Get-Process -Name QuickShot -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
    if ($owned.Count -ne 0) { throw 'A fixture path unexpectedly owns a process; refusing to invoke the uninstaller.' }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $powershell
    $start.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $Fixture 'Uninstall.ps1') + '" -InstallDirectory "' + $Fixture + '"'
    $start.WorkingDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['QUICKSHOT_TEST_REGISTRY_ROOT'] = $registryRoot
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Could not start isolated Windows PowerShell 5 uninstaller check.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(10000)) {
            # This is only the exact PowerShell child created above, never a QuickShot user process.
            $process.Kill()
            $process.WaitForExit()
            throw 'Isolated portable uninstaller timed out.'
        }
        return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $stdout.GetAwaiter().GetResult(); Error = $stderr.GetAwaiter().GetResult() }
    }
    finally { $process.Dispose() }
}

$hostBefore = RegistryValueSnapshot $realHostKey
$runBefore = RegistryValueSnapshot $realRunKey 'QuickShot'
if ((Split-Path -Parent $testRoot) -ne $artifactsRoot -or $testName -notmatch '^portable-uninstall-test-[a-f0-9]{32}$') { throw 'Unsafe portable uninstall test path.' }
if (Test-Path -LiteralPath $testRoot) { throw 'Portable uninstall test directory already exists.' }
if (Test-Path -LiteralPath $registryRoot) { throw 'Portable uninstall test registry namespace already exists.' }
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$env:QUICKSHOT_TEST_REGISTRY_ROOT = $registryRoot
try {
    $valid = NewFixture 'valid' 'valid'
    $preservedBefore = SnapshotFiles $valid -ExcludeNativeManifest
    Check (-not (Test-Path -LiteralPath (Join-Path $valid 'extension-id.txt'))) 'portable fixture deliberately has no extension-id.txt'
    Check ([IO.File]::ReadAllText((Join-Path $valid 'app\QuickShot.exe')).StartsWith('NOT AN EXECUTABLE')) 'portable fixture cannot start a real QuickShot process'
    $result = InvokeFixtureUninstall $valid
    Check ($result.ExitCode -eq 0) ('valid portable package uninstalls with manifest-derived identity: ' + $result.Error)
    Check (-not (Test-Path -LiteralPath $hostKey)) 'matching native host registration is removed'
    Check ($null -eq (ReadValue $runKey 'QuickShot') -and (ReadValue $runKey 'OtherApplication') -eq 'unrelated-test-registration') 'only matching startup value is removed'
    Check (-not (Test-Path -LiteralPath (Join-Path $valid 'native-host.json'))) 'portable native manifest is removed'
    Check ((Test-Path -LiteralPath $valid) -and [IO.File]::ReadAllText((Join-Path $valid 'data\keep.txt')) -eq 'keep this portable cache') 'portable directory and data are retained'
    Check ((SnapshotFiles $valid -ExcludeNativeManifest) -eq $preservedBefore) 'app, extension, marker, script and portable cache remain byte-for-byte unchanged'

    foreach ($invalidCase in 'malformed','wrong-key','missing') {
        $fixture = NewFixture $invalidCase $invalidCase
        $filesBefore = SnapshotFiles $fixture
        $hostSnapshot = RegistryValueSnapshot $hostKey
        $startupSnapshot = RegistryValueSnapshot $runKey 'QuickShot'
        $result = InvokeFixtureUninstall $fixture
        Check ($result.ExitCode -ne 0) ($invalidCase + ' manifest is rejected')
        Check ((RegistryValueSnapshot $hostKey) -eq $hostSnapshot -and (RegistryValueSnapshot $runKey 'QuickShot') -eq $startupSnapshot -and (ReadValue $runKey 'OtherApplication') -eq 'unrelated-test-registration') ($invalidCase + ' rejection preserves registrations')
        Check (Test-Path -LiteralPath (Join-Path $fixture 'native-host.json')) ($invalidCase + ' rejection preserves the native manifest')
        Check ((SnapshotFiles $fixture) -eq $filesBefore) ($invalidCase + ' rejection preserves every fixture file')
    }
    Check ((RegistryValueSnapshot $realHostKey) -eq $hostBefore -and (RegistryValueSnapshot $realRunKey 'QuickShot') -eq $runBefore) 'real user native-host and startup registrations remain unchanged'
    Write-Output ('Portable uninstall smoke passed: ' + $script:checks + ' checks (Windows PowerShell 5).')
}
finally {
    # Recursively remove only these freshly created, exact GUID namespaces.
    if ($registryRoot -notmatch '^HKCU:\\Software\\QuickShot\\Tests\\[a-f0-9]{32}$') { throw 'Unsafe portable uninstall registry cleanup.' }
    if (Test-Path -LiteralPath $registryRoot) { Remove-Item -LiteralPath $registryRoot -Recurse -Force }
    $env:QUICKSHOT_TEST_REGISTRY_ROOT = $oldTestRegistryRoot
    $resolvedRoot = [IO.Path]::GetFullPath($testRoot)
    if ((Split-Path -Parent $resolvedRoot) -ne $artifactsRoot -or (Split-Path -Leaf $resolvedRoot) -ne $testName -or $testName -notmatch '^portable-uninstall-test-[a-f0-9]{32}$') { throw 'Unsafe portable uninstall filesystem cleanup.' }
    if (Test-Path -LiteralPath $resolvedRoot) {
        if ((Get-Item -LiteralPath $resolvedRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Portable uninstall cleanup refuses linked directories.' }
        if (Get-ChildItem -LiteralPath $resolvedRoot -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Portable uninstall cleanup refuses nested linked files.' }
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
    if ((RegistryValueSnapshot $realHostKey) -ne $hostBefore -or (RegistryValueSnapshot $realRunKey 'QuickShot') -ne $runBefore) { throw 'Real user registration changed during the test.' }
}
