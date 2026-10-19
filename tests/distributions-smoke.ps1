param(
    [string]$PortableArchive,
    [switch]$IncludeInstaller,
    [string]$InstallerPayloadArchive,
    [string]$SetupExecutable
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $PortableArchive) { $PortableArchive = Join-Path $repoRoot 'artifacts\QuickShot-Portable-win-x64.zip' }
if (-not $InstallerPayloadArchive) { $InstallerPayloadArchive = Join-Path $repoRoot 'artifacts\build\QuickShot-installer-payload.zip' }
if (-not $SetupExecutable) { $SetupExecutable = Join-Path $repoRoot 'artifacts\QuickShot-Setup-win-x64.exe' }
$PortableArchive = [IO.Path]::GetFullPath($PortableArchive)
$InstallerPayloadArchive = [IO.Path]::GetFullPath($InstallerPayloadArchive)
$SetupExecutable = [IO.Path]::GetFullPath($SetupExecutable)
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    Write-Output ('PASS: ' + $Message)
}

function Get-ZipIndex([IO.Compression.ZipArchive]$Archive, [string]$Label) {
    $index = @{}
    foreach ($entry in $Archive.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($name -match '(^/|^[a-zA-Z]:|(?:^|/)\.\.(?:/|$))') { throw ($Label + ' has an unsafe archive entry: ' + $name) }
        if ($name.EndsWith('/')) { continue }
        if ($index.ContainsKey($name)) { throw ($Label + ' has a duplicate file: ' + $name) }
        $index[$name] = $entry
    }
    return $index
}

function Require-Files([hashtable]$Index, [string[]]$Names, [string]$Label) {
    foreach ($name in $Names) {
        if (-not $Index.ContainsKey($name) -or $Index[$name].Length -le 0) { throw ($Label + ' is missing a nonempty file: ' + $name) }
    }
}

function Assert-CleanPackage([IO.Compression.ZipArchive]$Archive, [string[]]$ForbiddenFiles, [string]$Label) {
    foreach ($entry in $Archive.Entries) {
        $parts = $entry.FullName.Replace('\', '/').TrimEnd('/').Split('/')
        $leaf = $parts[$parts.Length - 1]
        if ($parts -contains 'data' -or $leaf -in @('native-host.json', 'quickshot.install.json', 'targets.json', 'diagnostics.log')) {
            throw ($Label + ' contains runtime state: ' + $entry.FullName)
        }
        if ($leaf -in $ForbiddenFiles) { throw ($Label + ' contains a forbidden file: ' + $entry.FullName) }
    }
}

function Get-EntryHash([IO.Compression.ZipArchiveEntry]$Entry) {
    $stream = $Entry.Open()
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

function Read-EntryJson([IO.Compression.ZipArchiveEntry]$Entry) {
    $stream = $Entry.Open()
    $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8, $true)
    try { return ($reader.ReadToEnd() | ConvertFrom-Json) }
    finally { $reader.Dispose(); $stream.Dispose() }
}

function Read-EntryText([IO.Compression.ZipArchiveEntry]$Entry) {
    $stream = $Entry.Open()
    $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8, $true)
    try { return $reader.ReadToEnd() }
    finally { $reader.Dispose(); $stream.Dispose() }
}

function Assert-ExtensionResources([hashtable]$Index, [object]$Manifest, [string]$Label) {
    # These files are the complete extension runtime, including every icon size declared by its manifest.
    $runtimeFiles = @('background.js','connect.html','connect.js','content.js','manifest.json','options.html','options.js','popup.html','popup.js','shared.mjs','shortcut-input.mjs','ui.css','icons/16.png','icons/32.png','icons/48.png','icons/128.png')
    Require-Files $Index @($runtimeFiles | ForEach-Object { 'extension/' + $_ }) $Label
    $unexpected = @($Index.Keys | Where-Object { $_.StartsWith('extension/') -and $_.Substring(10) -notin $runtimeFiles })
    Check ($unexpected.Count -eq 0) ($Label + ' extension contains only its complete runtime files')
    $references = @($Manifest.background.service_worker, $Manifest.action.default_popup, $Manifest.options_page)
    $references += @($Manifest.icons.PSObject.Properties | ForEach-Object { $_.Value })
    $references += @($Manifest.action.default_icon.PSObject.Properties | ForEach-Object { $_.Value })
    foreach ($resource in $references) {
        if (-not $resource -or -not $Index.ContainsKey('extension/' + $resource)) { throw ($Label + ' has a missing manifest resource: ' + $resource) }
    }
    foreach ($file in $runtimeFiles) {
        if ($file -notmatch '\.(?:html|js|mjs|css)$') { continue }
        $content = Read-EntryText $Index['extension/' + $file]
        $patterns = @(
            '(?i)(?:src|href)\s*=\s*["'']([^"'']+)["'']',
            '(?:\bfrom\s*|\bimport\s*)["''](\.[^"'']+)["'']',
            '\bgetURL\(\s*["'']([^"'']+)["'']\s*\)',
            '\bfiles\s*:\s*\[\s*["'']([^"'']+)["'']\s*\]'
        )
        foreach ($pattern in $patterns) {
            foreach ($match in [Regex]::Matches($content, $pattern)) {
                $reference = $match.Groups[1].Value
                if ($reference -match '^(?:[a-zA-Z][a-zA-Z0-9+.-]*:|#|//)') { continue }
                $baseUri = [Uri]('https://quickshot.invalid/' + $file)
                $resolved = New-Object Uri($baseUri, $reference)
                $relative = [Uri]::UnescapeDataString($resolved.AbsolutePath).TrimStart('/')
                if (-not $Index.ContainsKey('extension/' + $relative)) { throw ($Label + ' has a missing extension reference: ' + $file + ' -> ' + $reference) }
            }
        }
    }
    Check $true ($Label + ' manifest, HTML and JavaScript references resolve to included resources')
}

function Get-ExtensionIdentity([string]$PublicKey) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $hash = $algorithm.ComputeHash([Convert]::FromBase64String($PublicKey)) }
    finally { $algorithm.Dispose() }
    $id = New-Object Text.StringBuilder
    for ($i = 0; $i -lt 16; $i++) {
        [void]$id.Append([char](97 + ($hash[$i] -shr 4)))
        [void]$id.Append([char](97 + ($hash[$i] -band 15)))
    }
    return $id.ToString()
}

function Check-StagedApp([string]$DirectoryName, [string]$ExpectedHash) {
    $relativeExecutable = if ($DirectoryName -eq 'portable') { 'QuickShot.exe' } else { 'app\QuickShot.exe' }
    $executable = Join-Path $repoRoot ('artifacts\staging\' + $DirectoryName + '\' + $relativeExecutable)
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw ('Staged app does not exist: ' + $executable) }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
    $actualVersion = '{0}.{1}.{2}' -f $version.FileMajorPart, $version.FileMinorPart, $version.FileBuildPart
    Check ($actualVersion -eq '1.3.6') ($DirectoryName + ' app version is 1.3.6')
    Check ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant() -eq $ExpectedHash) ($DirectoryName + ' ZIP app matches the staged executable whose version was checked')
}

# Read archives in place. This test never extracts, runs the app or installer, or accesses the registry.
$portableZip = $null
$installerZip = $null
try {
    $portableZip = [IO.Compression.ZipFile]::OpenRead($PortableArchive)
    $portable = Get-ZipIndex $portableZip 'Portable ZIP'
    Require-Files $portable @('QuickShot.exe', 'extension/manifest.json', '使用说明.html') 'Portable ZIP'
    Assert-CleanPackage $portableZip @('Install.ps1', 'Install.cmd', 'Uninstall.ps1', 'Uninstall.cmd', 'Start.cmd', 'portable.flag', 'QuickShot-Setup-win-x64.exe', 'QuickShot-installer-payload.zip', 'README.md', '验证记录.md', 'extension-id.txt') 'Portable ZIP'
    foreach ($entry in $portableZip.Entries) {
        $name = $entry.FullName.Replace('\', '/').TrimEnd('/')
        if ($name -in @('extension','extension/icons')) { continue }
        if ($name -in @('QuickShot.exe','使用说明.html') -or $name.StartsWith('extension/')) { continue }
        throw ('Portable ZIP contains a nonessential entry: ' + $name)
    }
    $portableRootEntries = @($portable.Keys | ForEach-Object { $_.Split('/')[0] } | Sort-Object -Unique)
    Check ($portableRootEntries.Count -eq 3 -and $portableRootEntries -contains 'QuickShot.exe' -and $portableRootEntries -contains 'extension' -and $portableRootEntries -contains '使用说明.html') 'portable ZIP root has exactly QuickShot.exe, extension and the optional help file'
    Check $true 'portable ZIP contains no app wrapper, scripts, portable flag, extra documents, tests or runtime data'
    # Help is required only in the delivered archive. Runtime tests separately verify it can be removed.
    Check ((Read-EntryText $portable['使用说明.html']).Contains('1.3.6')) 'portable ZIP includes the standalone version 1.3.6 help file'
    $portableManifest = Read-EntryJson $portable['extension/manifest.json']
    Check ($portableManifest.version -eq '1.3.6') 'portable extension manifest declares version 1.3.6'
    Check ((Get-ExtensionIdentity $portableManifest.key) -eq 'kgjahiekhopambbcmenggajjghlmbhel') 'portable extension retains its stable extension identity'
    Assert-ExtensionResources $portable $portableManifest 'Portable ZIP'
    Check ($portable.Count -eq 18) 'portable ZIP contains 17 runtime files and one optional help file'

    $portableHash = Get-EntryHash $portable['QuickShot.exe']
    Check-StagedApp 'portable' $portableHash

    Check (-not (Test-Path -LiteralPath (Join-Path $repoRoot 'artifacts\QuickShot-win-x64.zip'))) 'the obsolete mixed distribution archive has been removed'
    if (-not $IncludeInstaller) {
        Check $true 'portable-only validation does not require or inspect an installer executable or payload'
        return
    }

    $installerZip = [IO.Compression.ZipFile]::OpenRead($InstallerPayloadArchive)
    $installer = Get-ZipIndex $installerZip 'Installer payload'
    Require-Files $installer @('app/QuickShot.exe', 'extension/manifest.json', 'Install.ps1', 'Uninstall.cmd', 'Uninstall.ps1') 'Installer payload'
    Assert-CleanPackage $installerZip @('Install.cmd', 'portable.flag') 'Installer payload'
    Check $true 'installer payload has its installation script, with no batch installer, portable flag or runtime state'
    $installerManifest = Read-EntryJson $installer['extension/manifest.json']
    Check ($installerManifest.version -eq '1.3.6') 'installer extension manifest declares version 1.3.6'
    Assert-ExtensionResources $installer $installerManifest 'Installer payload'
    Check ($portableManifest.key -eq $installerManifest.key) 'both distributions use the same extension identity'
    $installerHash = Get-EntryHash $installer['app/QuickShot.exe']
    Check ($portableHash -eq $installerHash) 'portable and installer payload app SHA256 values match'
    Check-StagedApp 'installer' $installerHash

    $setupStream = [IO.File]::OpenRead($SetupExecutable)
    try {
        $header = New-Object byte[] 64
        if ($setupStream.Read($header, 0, $header.Length) -ne $header.Length) { throw 'Setup executable is too short for a PE header.' }
        Check ($header[0] -eq 0x4D -and $header[1] -eq 0x5A) 'setup executable has an MZ signature'
        $peOffset = [BitConverter]::ToInt32($header, 60)
        if ($peOffset -lt 64 -or $peOffset -gt $setupStream.Length - 26) { throw 'Setup executable has an invalid PE header offset.' }
        [void]$setupStream.Seek($peOffset, [IO.SeekOrigin]::Begin)
        $peHeader = New-Object byte[] 26
        if ($setupStream.Read($peHeader, 0, $peHeader.Length) -ne $peHeader.Length) { throw 'Setup executable has a truncated PE header.' }
        Check ($peHeader[0] -eq 0x50 -and $peHeader[1] -eq 0x45 -and $peHeader[2] -eq 0 -and $peHeader[3] -eq 0) 'setup executable has a PE signature'
        Check ([BitConverter]::ToUInt16($peHeader, 4) -eq 0x8664 -and [BitConverter]::ToUInt16($peHeader, 24) -eq 0x20B) 'setup executable is a Windows x64 PE image'
        $payloadLength = (Get-Item -LiteralPath $InstallerPayloadArchive).Length
        $overhead = $setupStream.Length - $payloadLength
        Check ($overhead -gt 4096 -and $overhead -lt 512MB) 'setup size accommodates the full payload and a reasonable self-contained launcher'
    }
    finally { $setupStream.Dispose() }

}
finally {
    if ($installerZip) { $installerZip.Dispose() }
    if ($portableZip) { $portableZip.Dispose() }
}
