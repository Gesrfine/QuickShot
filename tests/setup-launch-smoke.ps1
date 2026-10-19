param([string]$SetupPath = (Join-Path $PSScriptRoot '..\artifacts\QuickShot-Setup-win-x64.exe'))
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceSetup = [IO.Path]::GetFullPath($SetupPath)
if (-not (Test-Path -LiteralPath $sourceSetup -PathType Leaf)) { throw 'Standalone setup EXE is missing.' }
$testName = 'setup-launch-smoke-' + [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $repoRoot ('artifacts\' + $testName)
$ownedSetup = Join-Path $testRoot 'Setup.exe'
$temporaryParent = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'QuickShot-Setup')).TrimEnd('\')
$installerPathPattern = '(?i)^' + [Regex]::Escape($temporaryParent) + '\\(?<PayloadId>[a-f0-9]{32})\\app\\QuickShot\.exe$'
$realHost = 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.quickshot.edge'
$realRun = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$bootstrap = $null
$installer = $null
$installerPath = $null
$payloadRoot = $null
$previousNoRegister = $env:QUICKSHOT_NO_REGISTER

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    Write-Output ('PASS: ' + $Message)
}

function RegistrySnapshot([string]$Path, [string]$Name = '') {
    $exists = Test-Path -LiteralPath $Path
    $value = $null
    if ($exists) { $value = (Get-Item -LiteralPath $Path).GetValue($Name) }
    [PSCustomObject]@{ Exists = $exists; Value = $value }
}

function SameSnapshot($Before, $After) {
    $Before.Exists -eq $After.Exists -and $Before.Value -ceq $After.Value
}

function InstallerCandidates([int]$BootstrapId) {
    @(Get-CimInstance Win32_Process -Filter ('ParentProcessId = ' + $BootstrapId) | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath -match $installerPathPattern -and
        $_.CommandLine -match '(?:^|\s)--install-ui(?:\s|$)'
    })
}

function StopOwnedProcess([Diagnostics.Process]$Process, [string]$ExpectedPath, [int]$ExpectedParent = 0) {
    if ($null -eq $Process) { return }
    $Process.Refresh()
    if ($Process.HasExited) { return }
    $identity = Get-CimInstance Win32_Process -Filter ('ProcessId = ' + $Process.Id)
    if ($null -eq $identity) { return }
    if (-not $identity.ExecutablePath -or [IO.Path]::GetFullPath($identity.ExecutablePath) -ne [IO.Path]::GetFullPath($ExpectedPath)) {
        throw 'Cleanup refused a process whose executable path no longer matches this test.'
    }
    if ($ExpectedParent -ne 0 -and ($identity.ParentProcessId -ne $ExpectedParent -or
        $identity.ExecutablePath -notmatch $installerPathPattern -or $identity.CommandLine -notmatch '(?:^|\s)--install-ui(?:\s|$)')) {
        throw 'Cleanup refused a process whose installer ownership no longer matches this test.'
    }
    # The Process object holds the original process handle; never kill by process name or a reused PID.
    $Process.Kill()
    if (-not $Process.WaitForExit(5000)) { throw 'An owned setup test process did not exit.' }
}

$hostBefore = RegistrySnapshot $realHost
$startupBefore = RegistrySnapshot $realRun 'QuickShot'
if (Test-Path -LiteralPath $testRoot) { throw 'Setup launch smoke directory already exists.' }
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    Copy-Item -LiteralPath $sourceSetup -Destination $ownedSetup
    Check (@(Get-ChildItem -LiteralPath $testRoot -Force).Count -eq 1) 'the launch directory contains only the standalone Setup.exe'
    # --install-ui returns before connection registration; this also prevents registration if that route changes.
    $env:QUICKSHOT_NO_REGISTER = '1'
    try {
        $bootstrap = Start-Process -FilePath $ownedSetup -WorkingDirectory ([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)) -WindowStyle Hidden -PassThru
    }
    finally { $env:QUICKSHOT_NO_REGISTER = $previousNoRegister }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        $bootstrap.Refresh()
        if ($bootstrap.HasExited) { throw ('Setup exited before opening its installation UI. Exit code: ' + $bootstrap.ExitCode) }
        if ($null -eq $installer) {
            $candidates = @(InstallerCandidates $bootstrap.Id)
            if ($candidates.Count -gt 1) { throw 'Setup unexpectedly created multiple installation UI processes.' }
            if ($candidates.Count -eq 1) {
                $candidate = $candidates[0]
                $installerPath = [IO.Path]::GetFullPath($candidate.ExecutablePath)
                if ($installerPath -notmatch $installerPathPattern) { throw 'Unexpected extracted installer path.' }
                $payloadId = $Matches.PayloadId
                $payloadRoot = [IO.Path]::GetFullPath((Join-Path $temporaryParent $payloadId))
                if ((Split-Path -Parent $payloadRoot) -ne $temporaryParent -or (Split-Path -Leaf $payloadRoot) -notmatch '^[a-f0-9]{32}$') { throw 'Extracted payload directory failed its GUID ownership check.' }
                try { $installer = [Diagnostics.Process]::GetProcessById([int]$candidate.ProcessId) }
                catch [ArgumentException] { $installer = $null }
            }
        }
        if ($null -ne $installer) {
            $installer.Refresh()
            if ($installer.HasExited) { throw 'The installation UI exited before becoming visible.' }
            if ($installer.MainWindowHandle -ne [IntPtr]::Zero) { break }
        }
        Start-Sleep -Milliseconds 100
    }
    Check ($null -ne $installer -and $installer.MainWindowHandle -ne [IntPtr]::Zero -and -not $installer.HasExited) 'standalone setup Main opens a live child installation window'
    foreach ($required in 'Install.ps1','app\QuickShot.exe','extension\manifest.json') {
        Check (Test-Path -LiteralPath (Join-Path $payloadRoot $required) -PathType Leaf) ('the temporary payload contains ' + $required)
    }
    Check (-not (Test-Path -LiteralPath (Join-Path $payloadRoot 'quickshot.install.json'))) 'the prepared payload has not been installed'
    $workerChildren = @(Get-CimInstance Win32_Process -Filter ('ParentProcessId = ' + $installer.Id) | Where-Object { $_.Name -ieq 'powershell.exe' })
    Check ($workerChildren.Count -eq 0) 'opening the installation window does not start installation PowerShell'
    Check (SameSnapshot $hostBefore (RegistrySnapshot $realHost)) 'opening setup does not change the real native host registration'
    Check (SameSnapshot $startupBefore (RegistrySnapshot $realRun 'QuickShot')) 'opening setup does not change the real Windows startup value'
    # Close only this exact, unstarted child UI. Do not press its Install button or invoke any install script.
    Check ($installer.CloseMainWindow()) 'the unstarted installation UI accepts a normal window close'
    Check ($installer.WaitForExit(10000)) 'the unstarted child UI exits after being closed'
    Check ($bootstrap.WaitForExit(30000)) 'the setup bootstrap finishes after its child UI closes'
    $bootstrap.Refresh()
    Check ($bootstrap.ExitCode -eq 0) 'standalone setup Main returns success after a cancelled installation'
    Check (-not (Test-Path -LiteralPath $payloadRoot)) 'the bootstrap removes its exact temporary GUID payload directory'
    Check (SameSnapshot $hostBefore (RegistrySnapshot $realHost)) 'the real native host registration remains unchanged'
    Check (SameSnapshot $startupBefore (RegistrySnapshot $realRun 'QuickShot')) 'the real Windows startup value remains unchanged'
}
finally {
    $env:QUICKSHOT_NO_REGISTER = $previousNoRegister
    # Only the known child of our bootstrap, at its validated temporary GUID path, may be stopped.
    if ($null -ne $installer) {
        StopOwnedProcess $installer $installerPath $bootstrap.Id
        $installer.Dispose()
    }
    if ($null -ne $bootstrap) {
        $bootstrap.Refresh()
        if (-not $bootstrap.HasExited) {
            # A failure may occur before the regular wait loop recorded the child process.
            foreach ($candidate in @(InstallerCandidates $bootstrap.Id)) {
                $remaining = $null
                try { $remaining = [Diagnostics.Process]::GetProcessById([int]$candidate.ProcessId) }
                catch [ArgumentException] { }
                if ($null -ne $remaining) {
                    try { StopOwnedProcess $remaining $candidate.ExecutablePath $bootstrap.Id }
                    finally { $remaining.Dispose() }
                }
            }
            # Give the bootstrap time to perform its own cleanup before stopping a failed test instance.
            if (-not $bootstrap.WaitForExit(3000)) { StopOwnedProcess $bootstrap $ownedSetup }
        }
        $bootstrap.Dispose()
    }
    # Never recursively remove the system temporary parent or an unknown payload. The bootstrap owns that cleanup.
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $expectedParent = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
    if ((Split-Path -Parent $resolvedTestRoot) -ne $expectedParent -or (Split-Path -Leaf $resolvedTestRoot) -ne $testName -or $testName -notmatch '^setup-launch-smoke-[a-f0-9]{32}$') { throw 'Unsafe setup launch smoke cleanup directory.' }
    if (Test-Path -LiteralPath $resolvedTestRoot) {
        if ((Get-Item -LiteralPath $resolvedTestRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Setup launch smoke cleanup refuses a linked directory.' }
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
