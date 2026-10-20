#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$ProjectRoot = '',
    [switch]$IncludeGpu,
    [switch]$VerifyOnly,
    [switch]$Offline,
    [string]$CacheDirectory = (Join-Path $env:LOCALAPPDATA 'CortexFX\DependencyCache')
)
$ErrorActionPreference = 'Stop'
if ($IncludeGpu -and ![Environment]::Is64BitProcess) { throw 'Use 64-bit PowerShell to restore NVIDIA GPU resources.' }
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) { $ProjectRoot = Split-Path -Parent $PSScriptRoot }
$ProjectRoot = [IO.Path]::GetFullPath($ProjectRoot)
if (!(Test-Path -LiteralPath (Join-Path $ProjectRoot 'CortexFX.csproj'))) { throw 'ProjectRoot must contain CortexFX.csproj.' }
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'audio-resources.lock.json') -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1) { throw 'Unsupported audio dependency lock format.' }

function Child-Path([string]$Root, [string]$Relative) {
    if ([IO.Path]::IsPathRooted($Relative)) { throw "Expected a relative path: $Relative" }
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $full = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (!$full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Path escapes its root: $Relative" }
    return $full
}
$resources = Child-Path $ProjectRoot $lock.resourceDirectory
$CacheDirectory = [IO.Path]::GetFullPath($CacheDirectory)
function Test-LockedFile([string]$Path, $File) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $item = Get-Item -LiteralPath $Path
    return $item.Length -eq $File.bytes -and (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $File.sha256
}
function Test-MicrosoftRuntime([string]$Path, $File) {
    if (Test-LockedFile $Path $File) { return $true }
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    if ($version.FileMajorPart -lt 14 -or ($version.FileMajorPart -eq 14 -and $version.FileMinorPart -lt 44)) { return $false }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    return $signature.Status -eq 'Valid' -and $signature.SignerCertificate.Subject -match 'Microsoft Corporation'
}
function Install-File([string]$Source, [string]$Target) {
    $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Target)
    if ((Test-Path -LiteralPath $Target) -and ((Get-Item -LiteralPath $Target).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to replace a linked resource: $Target"
    }
    $temporary = $Target + '.install-' + [Guid]::NewGuid().ToString('N')
    try {
        [IO.File]::Copy($Source, $temporary, $false)
        if ((Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash) { throw 'Resource copy verification failed.' }
        if (Test-Path -LiteralPath $Target) { [IO.File]::Replace($temporary, $Target, [System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($temporary, $Target) }
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}
function Get-Package($Package) {
    $suffix = if ($Package.kind -eq 'file') { 'bin' } else { $Package.kind }
    $path = Child-Path $CacheDirectory ($Package.id + '.' + $suffix)
    if ((Test-Path -LiteralPath $path) -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $Package.sha256) { return $path }
    if ($Offline) { throw "Verified cache file is missing: $path. Run once without -Offline." }
    if (!$Package.url.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) { throw 'Dependency downloads require HTTPS.' }
    $null = New-Item -ItemType Directory -Force -Path $CacheDirectory
    $temporary = $path + '.' + [Guid]::NewGuid().ToString('N') + '.partial'
    try {
        Write-Host "Downloading $($Package.id)..."
        $ProgressPreference = 'SilentlyContinue'
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $Package.url -OutFile $temporary -UseBasicParsing -TimeoutSec 1800
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $Package.sha256) { throw "SHA256 verification failed: $($Package.id)" }
        if (Test-Path -LiteralPath $path) { [IO.File]::Replace($temporary, $path, [System.Management.Automation.Language.NullString]::Value) }
        else { [IO.File]::Move($temporary, $path) }
        return $path
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}

$missing = [Collections.Generic.List[string]]::new()
$licenseFiles = @($lock.licenses)
if ($IncludeGpu) { $licenseFiles += @($lock.gpuLicenses) }
foreach ($license in $licenseFiles) {
    if (!(Test-Path -LiteralPath (Child-Path $resources $license))) { throw "Repository notice/license is missing: $license. Restore the source checkout first." }
}
foreach ($package in $lock.packages) {
    if ($package.gpu -and !$IncludeGpu) { continue }
    $invalid = @($package.files | Where-Object { !(Test-LockedFile (Child-Path $resources $_.target) $_) })
    if (!$invalid.Count) { Write-Host "Verified $($package.id)"; continue }
    if ($VerifyOnly) { foreach ($file in $invalid) { $missing.Add($file.target) }; continue }
    $archive = Get-Package $package
    $null = New-Item -ItemType Directory -Force -Path $resources
    $work = Child-Path $resources ('.restore-audio-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $work
    try {
        if ($package.kind -eq 'tar.bz2') {
            $paths = @($invalid | ForEach-Object { $_.archivePath })
            & tar.exe -xjf $archive -C $work -- @paths
            if ($LASTEXITCODE -ne 0) { throw "Cannot extract $($package.id). Windows tar.exe is required." }
        }
        elseif ($package.kind -eq 'zip') {
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            $zip = [IO.Compression.ZipFile]::OpenRead($archive)
            try {
                foreach ($file in $invalid) {
                    $entry = $zip.GetEntry($file.archivePath)
                    if ($null -eq $entry) { throw "Missing archive entry: $($file.archivePath)" }
                    $destination = Child-Path $work $file.archivePath
                    $null = New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination)
                    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $false)
                }
            }
            finally { $zip.Dispose() }
        }
        elseif ($package.kind -ne 'file') { throw "Unsupported archive type: $($package.kind)" }
        foreach ($file in $invalid) {
            $source = if ($package.kind -eq 'file') { $archive } else { Child-Path $work $file.archivePath }
            if (!(Test-LockedFile $source $file)) { throw "Extracted file SHA256 mismatch: $($file.target)" }
            Install-File $source (Child-Path $resources $file.target)
        }
        Write-Host "Installed $($package.id)"
    }
    finally {
        # Delete only this invocation's generated staging child, after checking its absolute scope.
        $checkedWork = Child-Path $resources (Split-Path -Leaf $work)
        if ($checkedWork -ne $work -or (Split-Path -Leaf $work) -notlike '.restore-audio-*') { throw 'Unsafe staging cleanup path.' }
        if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
    }
}
if ($IncludeGpu) {
    foreach ($file in $lock.microsoftRuntime) {
        $destination = Child-Path $resources $file.target
        if (Test-MicrosoftRuntime $destination $file) { continue }
        if (!$VerifyOnly) {
            $name = [IO.Path]::GetFileName($file.target)
            $candidates = @((Join-Path $ProjectRoot ('Resources\' + $name)), (Join-Path ([Environment]::SystemDirectory) $name))
            foreach ($candidate in $candidates) {
                if (Test-MicrosoftRuntime $candidate $file) { Install-File $candidate $destination; break }
            }
        }
        if (!(Test-MicrosoftRuntime $destination $file)) { $missing.Add($file.target + ' (requires Microsoft Visual C++ x64 2015-2022 runtime)') }
    }
}
if ($missing.Count) {
    throw ("Missing or invalid audio resources:`n" + ($missing -join "`n") + "`nRun this script without -VerifyOnly to restore. For missing Microsoft runtime files, use https://aka.ms/vs/17/release/vc_redist.x64.exe and rerun.")
}
Write-Host 'Audio resources verified. No system installation or PATH changes were performed.' -ForegroundColor Green