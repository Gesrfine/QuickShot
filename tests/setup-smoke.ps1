param([Parameter(Mandatory = $true)][string]$SetupPath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$setup = [IO.Path]::GetFullPath($SetupPath)
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'Setup EXE is missing.' }
$testName = 'setup-smoke-' + [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $repoRoot ('artifacts\' + $testName)
$extractRoot = Join-Path $testRoot 'Extracted payload'
$installedRoot = Join-Path $testRoot ('Custom ' + [char]0x5FEB + [char]0x6295 + ' installed')
$previousRegistry = $env:QUICKSHOT_TEST_REGISTRY_ROOT
$env:QUICKSHOT_TEST_REGISTRY_ROOT = 'HKCU:\Software\QuickShot\Tests\' + [Guid]::NewGuid().ToString('N')
$registry = $env:QUICKSHOT_TEST_REGISTRY_ROOT + '\NativeHost'
$runKey = $env:QUICKSHOT_TEST_REGISTRY_ROOT + '\Run'
$realHost = 'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.quickshot.edge'
$realRun = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
function Value([string]$Path, [string]$Name = '') { if (Test-Path -LiteralPath $Path) { (Get-Item -LiteralPath $Path).GetValue($Name) } }
function Check([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message }; Write-Output ('PASS: ' + $Message) }
$hostBefore = Value $realHost
$startupBefore = Value $realRun 'QuickShot'
$powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (Test-Path -LiteralPath $testRoot) { throw 'Setup smoke directory already exists.' }
New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
try {
    # Loading the assembly bytes reads resources without running Main or locking the setup file.
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($setup))
    $names = $assembly.GetManifestResourceNames()
    Check ($names -contains 'QuickShot.Setup.Payload.zip' -and $names -contains 'QuickShot.Setup.Payload.sha256') 'standalone setup contains its ZIP and SHA-256 resources'
    $hashStream = $assembly.GetManifestResourceStream('QuickShot.Setup.Payload.sha256')
    $reader = New-Object IO.StreamReader($hashStream, [Text.Encoding]::ASCII)
    try { $expected = $reader.ReadToEnd().Trim() } finally { $reader.Dispose() }
    $payloadStream = $assembly.GetManifestResourceStream('QuickShot.Setup.Payload.zip')
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $actual = [BitConverter]::ToString($hash.ComputeHash($payloadStream)).Replace('-', '').ToLowerInvariant() }
    finally { $payloadStream.Dispose(); $hash.Dispose() }
    Check ($actual -eq $expected -and $expected -match '^[a-f0-9]{64}$') 'embedded payload hash matches its bytes'
    $payloadStream = $assembly.GetManifestResourceStream('QuickShot.Setup.Payload.zip')
    $archive = [IO.Compression.ZipArchive]::new($payloadStream, [IO.Compression.ZipArchiveMode]::Read, $false)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/').TrimEnd('/') })
        $files = @($archive.Entries | Where-Object { -not ($_.FullName.EndsWith('/') -or $_.FullName.EndsWith('\')) } | ForEach-Object { $_.FullName.Replace('\', '/') })
        foreach ($required in 'Install.ps1','Uninstall.ps1','Uninstall.cmd','app/QuickShot.exe','extension/manifest.json','extension-id.txt') {
            Check ($files -contains $required) ('setup payload includes ' + $required)
        }
        Check (-not ($entries | Where-Object { $_ -imatch '^(Install\.cmd|portable\.flag|native-host\.json|data(?:/.*)?)$' })) 'setup payload excludes portable flags, installer CMD, data and registered native manifests'
    }
    finally { $archive.Dispose() }
    $flags = [Reflection.BindingFlags]'Static, NonPublic'
    $payloadType = $assembly.GetType('QuickShot.Setup.PayloadArchive', $true)
    $safePath = $payloadType.GetMethod('GetSafeEntryPath', $flags)
    foreach ($unsafe in '../escape.txt','/escape.txt','C:\escape.txt','sub/../../escape.txt','sub\..\escape.txt','sub/file.txt:stream','CON.txt','sub/name. ') {
        $refused = $false
        try { $null = $safePath.Invoke($null, [object[]]@([string]$extractRoot, [string]$unsafe)) }
        catch {
            $failure = $_.Exception
            while ($failure.InnerException) { $failure = $failure.InnerException }
            $refused = $failure -is [IO.InvalidDataException]
        }
        Check $refused ('bootstrap rejects unsafe ZIP path: ' + $unsafe)
    }
    $extract = $payloadType.GetMethod('ExtractTo', $flags)
    try { $null = $extract.Invoke($null, [object[]]@([Reflection.Assembly]$assembly, [string]$extractRoot, $null)) }
    catch {
        $failure = $_.Exception
        while ($failure.InnerException) { $failure = $failure.InnerException }
        throw $failure
    }
    Check ((Test-Path -LiteralPath (Join-Path $extractRoot 'app\QuickShot.exe')) -and (Test-Path -LiteralPath (Join-Path $extractRoot 'Install.ps1'))) 'the actual bootstrap safely extracts its embedded payload'
    & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $extractRoot 'Install.ps1') -InstallDirectory $installedRoot -NoLaunch
    if ($LASTEXITCODE -ne 0) { throw 'Extracted setup payload failed to install in its isolated smoke directory.' }
    $marker = Get-Content -LiteralPath (Join-Path $installedRoot 'quickshot.install.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $manifestPath = Join-Path $installedRoot 'native-host.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Check ($marker.application -eq 'QuickShot' -and $marker.version -eq '1.2.0' -and [IO.Path]::GetFullPath($marker.installDirectory) -eq $installedRoot) 'setup installs version 1.2.0 into a custom Chinese path'
    Check ((Test-Path -LiteralPath (Join-Path $installedRoot 'app\QuickShot.exe')) -and (Test-Path -LiteralPath (Join-Path $installedRoot 'extension\manifest.json'))) 'installed program and Edge extension are present'
    Check (-not (Test-Path -LiteralPath (Join-Path $installedRoot 'Install.ps1')) -and -not (Test-Path -LiteralPath (Join-Path $installedRoot 'Install.cmd')) -and -not (Test-Path -LiteralPath (Join-Path $installedRoot 'portable.flag'))) 'installed copy contains neither setup scripts nor a portable flag'
    Check ($manifest.path -eq (Join-Path $installedRoot 'app\QuickShot.exe') -and (Value $registry) -eq $manifestPath) 'installation registers only the isolated test native host'
    Check ($null -eq (Value $runKey 'QuickShot')) 'fresh setup keeps Windows login startup off'
    & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $installedRoot 'Uninstall.ps1') -InstallDirectory $installedRoot
    if ($LASTEXITCODE -ne 0) { throw 'Installed payload failed to uninstall from its isolated smoke directory.' }
    Check (-not (Test-Path -LiteralPath $installedRoot) -and -not (Test-Path -LiteralPath $registry)) 'the installed uninstaller removes its own files and isolated registration'
    Check ((Value $realHost) -eq $hostBefore -and (Value $realRun 'QuickShot') -eq $startupBefore) 'real user installation registration and startup remain untouched'
}
finally {
    # These exact GUID namespaces belong only to this test; no real QuickShot process is launched.
    if ($env:QUICKSHOT_TEST_REGISTRY_ROOT -notmatch '^HKCU:\\Software\\QuickShot\\Tests\\[a-f0-9]{32}$') { throw 'Unsafe setup smoke registry cleanup.' }
    if (Test-Path -LiteralPath $env:QUICKSHOT_TEST_REGISTRY_ROOT) { Remove-Item -LiteralPath $env:QUICKSHOT_TEST_REGISTRY_ROOT -Recurse -Force }
    $env:QUICKSHOT_TEST_REGISTRY_ROOT = $previousRegistry
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $expectedParent = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
    if ((Split-Path -Parent $resolvedTestRoot) -ne $expectedParent -or (Split-Path -Leaf $resolvedTestRoot) -ne $testName -or $testName -notmatch '^setup-smoke-[a-f0-9]{32}$') { throw 'Unsafe setup smoke filesystem cleanup.' }
    if (Test-Path -LiteralPath $resolvedTestRoot) {
        if ((Get-Item -LiteralPath $resolvedTestRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Setup smoke cleanup refuses a linked directory.' }
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
