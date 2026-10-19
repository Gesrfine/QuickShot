param(
    [Parameter(Mandatory = $true)][string]$PayloadArchive,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $repoRoot 'src\QuickShot.Setup\Bootstrap.cs'
$archive = [IO.Path]::GetFullPath($PayloadArchive)
$output = [IO.Path]::GetFullPath($OutputPath)
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) { throw 'Setup payload ZIP is missing.' }
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'Setup bootstrap source is missing.' }
if ($archive -eq $output) { throw 'Setup output must not overwrite the payload ZIP.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework C# compiler is unavailable.' }
$outputDirectory = Split-Path -Parent $output
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$buildName = '.quickshot-setup-build-' + [Guid]::NewGuid().ToString('N')
$buildDirectory = Join-Path $outputDirectory $buildName
if (Test-Path -LiteralPath $buildDirectory) { throw 'Setup build directory already exists.' }
New-Item -ItemType Directory -Path $buildDirectory | Out-Null
$previousLocation = Get-Location
try {
    Copy-Item -LiteralPath $archive -Destination (Join-Path $buildDirectory 'payload.zip')
    $stream = [IO.File]::OpenRead((Join-Path $buildDirectory 'payload.zip'))
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $checksum = [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $hash.Dispose() }
    [IO.File]::WriteAllText((Join-Path $buildDirectory 'payload.sha256'), $checksum, [Text.Encoding]::ASCII)
    [IO.File]::WriteAllText((Join-Path $buildDirectory 'setup.manifest'), @'
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.2.0.0" name="QuickShot.Setup" processorArchitecture="amd64" type="win32"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="asInvoker" uiAccess="false"/></requestedPrivileges></security></trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"><application><supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/></application></compatibility>
</assembly>
'@, (New-Object Text.UTF8Encoding($false)))
    # Relative resource names also work when the repository path contains commas or spaces.
    Set-Location -LiteralPath $buildDirectory
    & $compiler '/nologo' '/target:winexe' '/platform:x64' '/langversion:5' '/codepage:65001' '/optimize+' '/debug-' '/out:QuickShot-Setup.exe' '/win32manifest:setup.manifest' '/reference:System.dll' '/reference:System.Core.dll' '/reference:System.Drawing.dll' '/reference:System.Windows.Forms.dll' '/reference:System.IO.Compression.dll' '/resource:payload.zip,QuickShot.Setup.Payload.zip' '/resource:payload.sha256,QuickShot.Setup.Payload.sha256' $source
    if ($LASTEXITCODE -ne 0) { throw 'Setup bootstrap compilation failed.' }
    Copy-Item -LiteralPath (Join-Path $buildDirectory 'QuickShot-Setup.exe') -Destination $output -Force
    Write-Output "Ready: $output"
}
finally {
    Set-Location -LiteralPath $previousLocation.Path
    # Remove only the exact GUID-named directory created by this build, never an output parent.
    $resolvedBuild = [IO.Path]::GetFullPath($buildDirectory)
    if ((Split-Path -Parent $resolvedBuild) -ne $outputDirectory -or (Split-Path -Leaf $resolvedBuild) -ne $buildName -or $buildName -notmatch '^\.quickshot-setup-build-[a-f0-9]{32}$') { throw 'Unsafe setup build cleanup path.' }
    if (Test-Path -LiteralPath $resolvedBuild) {
        if ((Get-Item -LiteralPath $resolvedBuild).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Setup build cleanup refuses a linked directory.' }
        Remove-Item -LiteralPath $resolvedBuild -Recurse -Force
    }
}
