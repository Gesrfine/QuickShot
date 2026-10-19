param([string]$PortableArchive)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $PortableArchive) { $PortableArchive = Join-Path $repoRoot 'artifacts\ClipboardSender-2.0.0-win-x64.zip' }
function Check([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message }; Write-Output ('PASS: ' + $Message) }
function Read-Hash($Entry) {
    $stream = $Entry.Open()
    try { return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
    finally { $stream.Dispose() }
}
function Read-Text($Entry) {
    $reader = [IO.StreamReader]::new($Entry.Open(), [Text.Encoding]::UTF8)
    try { return $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($PortableArchive))
try {
    $files = @{}
    foreach ($entry in $archive.Entries) {
        if (-not $entry.Name) { continue }
        $name = $entry.FullName.Replace('\', '/')
        if ($name.StartsWith('/') -or $name.Split('/') -contains '..' -or $files.ContainsKey($name)) { throw 'Invalid archive path.' }
        $files[$name] = $entry
    }
    Check ($files.Count -eq 17) 'package contains exactly the executable and 16 extension runtime files'
    $roots = @($files.Keys | ForEach-Object { $_.Split('/')[0] } | Sort-Object -Unique)
    Check ($roots.Count -eq 2 -and $roots -contains 'ClipboardSender.exe' -and $roots -contains 'extension') 'root contains only ClipboardSender.exe and extension'
    foreach ($name in $files.Keys) {
        $source = if ($name -eq 'ClipboardSender.exe') { Join-Path $repoRoot 'artifacts\build\2.0.0\QuickShot.exe' }
            elseif ($name.StartsWith('extension/')) { Join-Path $repoRoot $name }
            else { throw ('Unexpected package file: ' + $name) }
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw ('Missing source: ' + $name) }
        Check ((Read-Hash $files[$name]) -eq (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash) ('package bytes match current source: ' + $name)
    }
    $manifest = (Read-Text $files['extension/manifest.json']) | ConvertFrom-Json
    Check ($manifest.version -eq '2.0.0' -and $manifest.name -eq '剪切板快捷发送') 'extension version and branding are 2.0'
    $digest = [Security.Cryptography.SHA256]::HashData([Convert]::FromBase64String($manifest.key))
    $identity = -join ([Convert]::ToHexString($digest)[0..31] | ForEach-Object { [char](97 + [Convert]::ToInt32([string]$_, 16)) })
    Check ($identity -eq 'kgjahiekhopambbcmenggajjghlmbhel') 'stable extension identity preserves existing bindings'
    $worker = Read-Text $files['extension/background.js']
    Check (-not $worker.Contains('captureShortcut') -and -not $worker.Contains('FastStone')) 'extension contains no dedicated capture configuration'
    Check ((Read-Text $files['extension/shared.mjs']).Contains('PROTOCOL_VERSION = 3')) 'new protocol rejects obsolete programs'
    Check ($files['ClipboardSender.exe'].Length -lt 2MB) 'executable excludes the self-contained runtime payload'
    $runtime = Get-Content -LiteralPath (Join-Path $repoRoot 'src\QuickShot.App\bin\Release\net10.0-windows\win-x64\QuickShot.runtimeconfig.json') -Raw | ConvertFrom-Json
    Check (@($runtime.runtimeOptions.frameworks.name) -contains 'Microsoft.WindowsDesktop.App' -and -not $runtime.runtimeOptions.includedFrameworks) 'program uses the separately installed .NET Desktop Runtime'
    $preview = Join-Path $repoRoot ('artifacts\package-check\' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $preview -Force | Out-Null
    $executable = Join-Path $preview 'ClipboardSender.exe'
    [IO.Compression.ZipFileExtensions]::ExtractToFile($files['ClipboardSender.exe'], $executable)
    Check (([Diagnostics.FileVersionInfo]::GetVersionInfo($executable)).FileVersion -eq '2.0.0.0') 'packaged executable version is 2.0.0.0'
    Check (([Diagnostics.FileVersionInfo]::GetVersionInfo($executable)).ProductVersion -eq '2.0.0') 'product version is the release version without a stale baseline commit suffix'
} finally { $archive.Dispose() }
