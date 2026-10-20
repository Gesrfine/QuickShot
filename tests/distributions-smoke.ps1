param([string]$PortableArchive, [string]$PublishedExecutable)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = ([xml](Get-Content -LiteralPath (Join-Path $repoRoot 'src\QuickShot.App\QuickShot.App.csproj') -Raw)).Project.PropertyGroup.Version
if (-not $PortableArchive) { $PortableArchive = Join-Path $repoRoot ('artifacts\QuickShot-' + $version + '-win-x64.zip') }
if (-not $PublishedExecutable) { $PublishedExecutable = Join-Path $repoRoot ('artifacts\build\' + $version + '\QuickShot.exe') }
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
    Check ($files.Count -eq 18) 'package contains exactly the executable, HTML guide and 16 extension runtime files'
    $roots = @($files.Keys | ForEach-Object { $_.Split('/')[0] } | Sort-Object -Unique)
    Check ($roots.Count -eq 3 -and $roots -contains 'QuickShot.exe' -and $roots -contains 'extension' -and $roots -contains '使用教程.html') 'root contains only QuickShot.exe, extension and the removable HTML guide'
    foreach ($name in $files.Keys) {
        $source = if ($name -eq 'QuickShot.exe') { $PublishedExecutable }
            elseif ($name -eq '使用教程.html') { Join-Path $repoRoot 'docs\使用教程.html' }
            elseif ($name.StartsWith('extension/')) { Join-Path $repoRoot $name }
            else { throw ('Unexpected package file: ' + $name) }
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw ('Missing source: ' + $name) }
        Check ((Read-Hash $files[$name]) -eq (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash) ('package bytes match current source: ' + $name)
    }
    $manifest = (Read-Text $files['extension/manifest.json']) | ConvertFrom-Json
    Check ($manifest.version -eq $version -and $manifest.name -eq 'QuickShot' -and $manifest.action.default_title.StartsWith('QuickShot')) 'extension release version and visible branding match QuickShot'
    $digest = [Security.Cryptography.SHA256]::HashData([Convert]::FromBase64String($manifest.key))
    $identity = -join ([Convert]::ToHexString($digest)[0..31] | ForEach-Object { [char](97 + [Convert]::ToInt32([string]$_, 16)) })
    Check ($identity -eq 'kgjahiekhopambbcmenggajjghlmbhel') 'stable extension identity preserves existing bindings'
    $worker = Read-Text $files['extension/background.js']
    Check (-not $worker.Contains('captureShortcut') -and -not $worker.Contains('FastStone')) 'extension contains no dedicated capture configuration'
    Check ((Read-Text $files['extension/shared.mjs']).Contains('PROTOCOL_VERSION = 3')) 'new protocol rejects obsolete programs'
    $guide = Read-Text $files['使用教程.html']
    Check ($guide.Contains('https://dotnet.microsoft.com/en-us/download/dotnet/10.0') -and $guide.Contains('.NET Desktop Runtime') -and $guide.Contains('x64') -and $guide.Contains('QuickShot.exe')) 'offline guide includes the official .NET 10 Desktop x64 download page and launch instructions'
    Check ($guide -notmatch '<script\b|<iframe\b|<img\b|<link\b') 'HTML guide uses no scripts or external rendering assets'
    Check ($files['QuickShot.exe'].Length -lt 2MB) 'executable excludes the self-contained runtime payload'
    $runtime = Get-Content -LiteralPath (Join-Path $repoRoot 'src\QuickShot.App\bin\Release\net10.0-windows\win-x64\QuickShot.runtimeconfig.json') -Raw | ConvertFrom-Json
    Check (@($runtime.runtimeOptions.frameworks.name) -contains 'Microsoft.WindowsDesktop.App' -and -not $runtime.runtimeOptions.includedFrameworks) 'program uses the separately installed .NET Desktop Runtime'
    $preview = Join-Path $repoRoot ('artifacts\package-check\' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $preview -Force | Out-Null
    $executable = Join-Path $preview 'QuickShot.exe'
    [IO.Compression.ZipFileExtensions]::ExtractToFile($files['QuickShot.exe'], $executable)
    $identity = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
    Check ($identity.FileVersion -eq ($version + '.0')) 'packaged executable version matches the release'
    Check ($identity.ProductVersion -eq $version) 'product version is the release version without a stale baseline commit suffix'
    Check ($identity.FileDescription -eq 'QuickShot' -and $identity.ProductName -eq 'QuickShot' -and $identity.CompanyName -eq 'QuickShot') 'task-manager description and file properties consistently use QuickShot'
} finally { $archive.Dispose() }
