param(
    [Parameter(Mandatory = $true)][string]$AppPath,
    [Parameter(Mandatory = $true)][string]$ResultPath
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$executable = [IO.Path]::GetFullPath($AppPath)
$result = [IO.Path]::GetFullPath($ResultPath)
$testName = 'resource-smoke-' + [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $repoRoot ('artifacts\' + $testName)
$process = $null
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Measured executable is missing.' }
if (-not $result.StartsWith($repoRoot + '\artifacts\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Result must stay in workspace artifacts.' }
if (Test-Path -LiteralPath $testRoot) { throw 'Resource test directory already exists.' }
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $start = [Diagnostics.ProcessStartInfo]::new($executable, '--background')
    $start.UseShellExecute = $false
    $start.WorkingDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
    $start.EnvironmentVariables['QUICKSHOT_NO_REGISTER'] = '1'
    $start.EnvironmentVariables['QUICKSHOT_INSTANCE_ID'] = [Guid]::NewGuid().ToString('N')
    $start.EnvironmentVariables['QUICKSHOT_DATA_DIR'] = $testRoot
    $process = [Diagnostics.Process]::Start($start)
    Start-Sleep -Seconds 2
    if ($process.HasExited) { throw 'Isolated tray exited before measurement.' }
    $process.Refresh()
    $cpuStart = $process.TotalProcessorTime.TotalMilliseconds
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds 8
    $process.Refresh()
    if ($process.HasExited) { throw 'Isolated tray exited during measurement.' }
    $cpuMs = $process.TotalProcessorTime.TotalMilliseconds - $cpuStart
    $record = [ordered]@{
        app = $executable
        executableBytes = (Get-Item -LiteralPath $executable).Length
        observationSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 2)
        idleCpuMilliseconds = [Math]::Round($cpuMs, 2)
        idleOneCorePercent = [Math]::Round($cpuMs / $watch.Elapsed.TotalMilliseconds * 100, 3)
        privateBytes = $process.PrivateMemorySize64
        workingSetBytes = $process.WorkingSet64
        handles = $process.HandleCount
        threads = $process.Threads.Count
        registrationDisabled = $true
    }
    $record | ConvertTo-Json | Set-Content -LiteralPath $result -Encoding UTF8
    $record | ConvertTo-Json
}
finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
    $checkedRoot = [IO.Path]::GetFullPath($testRoot)
    if ((Split-Path -Parent $checkedRoot) -ne (Join-Path $repoRoot 'artifacts') -or
        (Split-Path -Leaf $checkedRoot) -ne $testName -or $testName -notmatch '^resource-smoke-[a-f0-9]{32}$') { throw 'Unsafe resource test cleanup path.' }
    if (Test-Path -LiteralPath $checkedRoot) {
        if ((Get-Item -LiteralPath $checkedRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Resource cleanup refuses a linked directory.' }
        Remove-Item -LiteralPath $checkedRoot -Recurse -Force
    }
}
