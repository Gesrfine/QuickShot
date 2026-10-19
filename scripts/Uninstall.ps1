param([string]$InstallDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$installRoot = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$driveRoot = [IO.Path]::GetPathRoot($installRoot).TrimEnd('\')
if ($installRoot -eq $driveRoot -or $installRoot.Length -lt $driveRoot.Length + 5) { throw 'Unsafe uninstall path.' }
$markerPath = Join-Path $installRoot 'quickshot.install.json'
$directory = Get-Item -LiteralPath $installRoot
if (-not $directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unsafe uninstall directory.' }
$portable = -not (Test-Path -LiteralPath $markerPath)
if ($portable) {
    if (-not (Test-Path -LiteralPath (Join-Path $installRoot 'portable.flag')) -or
        -not (Test-Path -LiteralPath (Join-Path $installRoot 'app\QuickShot.exe')) -or
        -not (Test-Path -LiteralPath (Join-Path $installRoot 'extension\manifest.json'))) { throw 'Portable package marker is missing. No files have been removed.' }
    $extensionManifest = Get-Content -LiteralPath (Join-Path $installRoot 'extension\manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $extensionKey = [Convert]::FromBase64String($extensionManifest.key)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $digest = $algorithm.ComputeHash($extensionKey) }
    finally { $algorithm.Dispose() }
    $extensionId = -join ($digest[0..15] | ForEach-Object { [char](97 + ($_ -shr 4)); [char](97 + ($_ -band 15)) })
    if ($extensionManifest.manifest_version -ne 3 -or $extensionId -ne 'kgjahiekhopambbcmenggajjghlmbhel') { throw 'Portable extension identity does not match QuickShot. No files have been removed.' }
}
else {
    $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($marker.application -ne 'QuickShot' -or [IO.Path]::GetFullPath($marker.installDirectory).TrimEnd('\') -ne $installRoot) { throw 'Installation marker does not match this directory.' }
}
$exePath = Join-Path $installRoot 'app\QuickShot.exe'
$registryPath = 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.quickshot.edge'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($env:QUICKSHOT_TEST_REGISTRY_ROOT) {
    if ($env:QUICKSHOT_TEST_REGISTRY_ROOT -notmatch '^HKCU:\\Software\\QuickShot\\Tests\\[a-zA-Z0-9-]{10,64}$') { throw 'Invalid test registry namespace.' }
    $registryPath = $env:QUICKSHOT_TEST_REGISTRY_ROOT + '\NativeHost'
    $runKey = $env:QUICKSHOT_TEST_REGISTRY_ROOT + '\Run'
}
$manifestPath = Join-Path $installRoot 'native-host.json'
if (Test-Path -LiteralPath $registryPath) {
    $registered = (Get-Item -LiteralPath $registryPath).GetValue('')
    if ($registered -eq $manifestPath) { Remove-Item -LiteralPath $registryPath }
}
$startupValue = $null
if (Test-Path -LiteralPath $runKey) { $startupValue = (Get-Item -LiteralPath $runKey).GetValue('QuickShot') }
if ($startupValue -eq ('"' + $exePath + '" --background')) { Remove-ItemProperty -LiteralPath $runKey -Name QuickShot }
# Remove the connection first so an old Edge extension cannot relaunch the copy we are stopping.
$ownedProcesses = @(Get-Process -Name QuickShot -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exePath })
foreach ($ownedProcess in $ownedProcesses) {
    try {
        if (-not $ownedProcess.HasExited) { $ownedProcess.Kill() }
        if (-not $ownedProcess.WaitForExit(5000)) { throw 'QuickShot did not exit. Close it and retry uninstall.' }
    }
    finally { $ownedProcess.Dispose() }
}
if ($portable) {
    if (Test-Path -LiteralPath $manifestPath) { Remove-Item -LiteralPath $manifestPath -Force }
    Write-Output 'Portable connection removed. Remove the extension in Edge, then delete the extracted folder when no longer needed.'
    Write-Output 'The extracted folder, portable cache and logs have been kept.'
}
else {
    # Both the PowerShell provider and Win32 process directory must release the installation folder.
    # The parent cmd is moved outside it by Uninstall.cmd before PowerShell starts.
    $uninstallWorkingDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
    Set-Location -LiteralPath $uninstallWorkingDirectory
    [Environment]::CurrentDirectory = $uninstallWorkingDirectory
    # The checked absolute target is the dedicated directory recorded by Install.ps1.
    Remove-Item -LiteralPath $installRoot -Recurse -Force
    Write-Output 'QuickShot removed. Remove the extension from edge://extensions to finish.'
    Write-Output 'Your target cache and diagnostic log remain in %LOCALAPPDATA%\QuickShot.'
}
