param([string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\QuickShot'), [switch]$NoLaunch, [int]$InstallerProcessId = 0)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
$registryPath = 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.quickshot.edge'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
# Smoke tests use an isolated, explicitly named registry namespace and never touch the user's registration.
if ($env:QUICKSHOT_TEST_REGISTRY_ROOT) {
    if ($env:QUICKSHOT_TEST_REGISTRY_ROOT -notmatch '^HKCU:\\Software\\QuickShot\\Tests\\[a-zA-Z0-9-]{10,64}$') { throw 'Invalid test registry namespace.' }
    $registryPath = $env:QUICKSHOT_TEST_REGISTRY_ROOT + '\NativeHost'
    $runKey = $env:QUICKSHOT_TEST_REGISTRY_ROOT + '\Run'
}
$installRoot = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$driveRoot = [IO.Path]::GetPathRoot($installRoot).TrimEnd('\')
if ($installRoot -eq $driveRoot -or $installRoot.Length -lt $driveRoot.Length + 5) { throw 'Choose a dedicated application folder.' }
$sourceRoot = [IO.Path]::GetFullPath($PSScriptRoot)
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'app\QuickShot.exe'))) { throw 'Setup payload is incomplete. Download and run QuickShot-Setup-win-x64.exe again.' }
if ($sourceRoot -eq $installRoot) { throw 'Extract the release separately from the installation directory.' }
if ($installRoot.StartsWith($sourceRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Do not install inside the extracted release folder.' }
if (Test-Path -LiteralPath $installRoot) {
    $existingDirectory = Get-Item -LiteralPath $installRoot
    if (-not $existingDirectory.PSIsContainer -or ($existingDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Choose a normal, dedicated application folder.' }
    $existingMarkerPath = Join-Path $installRoot 'quickshot.install.json'
    if (Test-Path -LiteralPath $existingMarkerPath) {
        $existingMarker = Get-Content -LiteralPath $existingMarkerPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($existingMarker.application -ne 'QuickShot' -or [IO.Path]::GetFullPath($existingMarker.installDirectory).TrimEnd('\') -ne $installRoot) { throw 'Existing installation marker does not match this folder.' }
    }
    elseif (Get-ChildItem -LiteralPath $installRoot -Force | Select-Object -First 1) { throw 'Install folder is not empty and is not a QuickShot installation.' }
}
$exePath = Join-Path $installRoot 'app\QuickShot.exe'
$ownedExecutables = @($exePath)
if (Test-Path -LiteralPath $registryPath) {
    $previousManifestPath = (Get-Item -LiteralPath $registryPath).GetValue('')
    if ($previousManifestPath -and (Test-Path -LiteralPath $previousManifestPath)) {
        try {
            $previousManifest = Get-Content -LiteralPath $previousManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $previousRoot = Split-Path -Parent $previousManifestPath
            $previousExe = Join-Path $previousRoot 'app\QuickShot.exe'
            if ($previousManifest.name -eq 'com.quickshot.edge' -and $previousManifest.path -eq $previousExe -and
                ((Test-Path -LiteralPath (Join-Path $previousRoot 'quickshot.install.json')) -or (Test-Path -LiteralPath (Join-Path $previousRoot 'portable.flag')))) {
                $ownedExecutables += $previousExe
            }
        } catch { Write-Output 'Previous connection could not be inspected; leaving its process unchanged.' }
    }
}
Get-Process -Name QuickShot -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $InstallerProcessId -and $_.Path -in $ownedExecutables } | Stop-Process -Force
New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
foreach ($name in 'app','extension','Uninstall.ps1','Uninstall.cmd','Start.cmd','extension-id.txt','README.md') {
    Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination $installRoot -Recurse -Force
}
# Dedicated installations no longer keep installer entry points or use portable data mode.
foreach ($legacyName in @('Install.cmd','Install.ps1','portable.flag')) {
    $legacyPath = Join-Path $installRoot $legacyName
    if (Test-Path -LiteralPath $legacyPath) { Remove-Item -LiteralPath $legacyPath -Force }
}
# Keep the optional test report when present; the product does not depend on it.
$report = Join-Path $sourceRoot (([char]0x9A8C).ToString() + [char]0x8BC1 + [char]0x8BB0 + [char]0x5F55 + '.md')
if (Test-Path -LiteralPath $report) { Copy-Item -LiteralPath $report -Destination $installRoot -Force }
$extensionId = (Get-Content -LiteralPath (Join-Path $installRoot 'extension-id.txt') -Raw).Trim()
if ($extensionId -notmatch '^[a-p]{32}$') { throw 'Invalid extension ID.' }
$hostManifestPath = Join-Path $installRoot 'native-host.json'
$manifest = @{ name='com.quickshot.edge'; description='QuickShot Edge native bridge'; path=$exePath; type='stdio'; allowed_origins=@("chrome-extension://$extensionId/") } | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText($hostManifestPath, $manifest, (New-Object Text.UTF8Encoding($false)))
New-Item -Path $registryPath -Force | Out-Null
Set-Item -LiteralPath $registryPath -Value $hostManifestPath
$marker = @{ application='QuickShot'; installDirectory=$installRoot; version='1.2.0' } | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $installRoot 'quickshot.install.json'), $marker, (New-Object Text.UTF8Encoding($false)))
if (Test-Path -LiteralPath $runKey) {
    $startup = (Get-Item -LiteralPath $runKey).GetValue('QuickShot')
    if ($ownedExecutables | Where-Object { $startup -eq ('"' + $_ + '" --background') }) {
        Set-ItemProperty -LiteralPath $runKey -Name QuickShot -Value ('"' + $exePath + '" --background')
    }
}
Write-Output "Installed: $installRoot"
Write-Output 'Next: open edge://extensions, enable Developer mode, choose Load unpacked, and select:'
Write-Output (Join-Path $installRoot 'extension')
Write-Output "Expected extension ID: $extensionId"
Write-Output 'Windows login startup is OFF by default; an existing opt-in setting is preserved.'
if (-not $NoLaunch) { Start-Process -FilePath $exePath -WorkingDirectory $installRoot -WindowStyle Hidden }

