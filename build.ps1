#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [ValidateSet('win-x64')][string]$Runtime = 'win-x64',
    [bool]$SelfContained = $false,
    [switch]$CpuOnly,
    [switch]$BundleAudioAi,
    [switch]$SkipInstaller,
    [switch]$CreatePortableZip,
    [switch]$RemoveStagingFolder
)
$ErrorActionPreference = 'Stop'
$ProjectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$ProjectFile = Join-Path $ProjectRoot 'CortexFX.csproj'
$PublishRoot = Join-Path $ProjectRoot 'Publish'

function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Assert-Child([string]$Path, [string]$Root) {
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
    $full = [IO.Path]::GetFullPath($Path)
    if (!$full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Path is outside its intended output directory: $full" }
}
function Assert-Exists([string]$Path) {
    if (!(Test-Path -LiteralPath $Path)) { throw "Required resource missing: $Path" }
}
function Invoke-Dotnet([string[]]$CommandArguments) {
    & dotnet @CommandArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($CommandArguments[0]) failed with exit code $LASTEXITCODE." }
}
function Remove-OwnedStaging {
    if (!$RemoveStagingFolder) { Write-Host "App folder kept: $PublishDir"; return }
    if (!(Test-Path -LiteralPath $InstallerPath) -and !(Test-Path -LiteralPath $ZipPath)) {
        Write-Warning 'No installer or portable ZIP exists; the app folder has been kept.'
        return
    }
    Assert-Child $PublishDir $ReleaseRoot
    Assert-Child $ReleaseRoot $PublishRoot
    if (([IO.Path]::GetFileName($PublishDir) -ne 'app') -or !$StagingCreatedByBuild) { throw 'Unsafe staging cleanup path.' }
    Remove-Item -LiteralPath $PublishDir -Recurse -Force
}

Assert-Exists $ProjectFile
[xml]$projectXml = Get-Content -LiteralPath $ProjectFile
$Version = @($projectXml.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'CortexFX.csproj must contain a numeric release version.' }
$includeAi = $BundleAudioAi -or $CpuOnly
$profile = if ($CpuOnly) { '-cpu' } elseif ($BundleAudioAi) { '-ai' } else { '' }
$ReleaseRoot = Join-Path $PublishRoot "CortexFX_v$Version$profile"
if (Test-Path -LiteralPath $ReleaseRoot) {
    $ReleaseRoot += '_' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6)
}
$PublishDir = Join-Path $ReleaseRoot 'app'
$InstallerPath = Join-Path $ReleaseRoot "CortexFX_Setup_v$Version.exe"
$ZipPath = Join-Path $ReleaseRoot "CortexFX_Portable_v$Version-$Runtime$profile.zip"
Assert-Child $ReleaseRoot $PublishRoot
Assert-Child $PublishDir $ReleaseRoot
$StagingCreatedByBuild = $false

$coreResources = @('ffmpeg.exe','ffmpeg_libs\avcodec-58.dll',
    'ffmpeg_libs\avformat-58.dll','ffmpeg_libs\avutil-56.dll','ffmpeg_libs\swresample-3.dll','ffmpeg_libs\swscale-5.dll')
foreach ($relative in $coreResources) { Assert-Exists (Join-Path $ProjectRoot ('Resources\' + $relative)) }
Assert-Exists (Join-Path $ProjectRoot 'Assets\logo.ico')
$audioLockPath = Join-Path $ProjectRoot 'tools\audio-resources.lock.json'
Assert-Exists $audioLockPath
$audioLock = Get-Content -LiteralPath $audioLockPath -Raw | ConvertFrom-Json
$audioResources = Join-Path $ProjectRoot $audioLock.resourceDirectory
foreach ($package in $audioLock.packages) {
    if (!$includeAi) { continue }
    if ($package.gpu -and $CpuOnly) { continue }
    foreach ($file in $package.files) { Assert-Exists (Join-Path $audioResources $file.target) }
}
foreach ($license in $audioLock.licenses) { Assert-Exists (Join-Path $audioResources $license) }
if (!$CpuOnly -and $includeAi) {
    foreach ($file in $audioLock.microsoftRuntime) { Assert-Exists (Join-Path $audioResources $file.target) }
    foreach ($license in $audioLock.gpuLicenses) { Assert-Exists (Join-Path $audioResources $license) }
}

$runtimeMode = if ($SelfContained) { 'self-contained' } else { 'framework-dependent (.NET 10 Desktop Runtime)' }
Write-Step "Building Cortex FX $Version ($Runtime$profile, $runtimeMode)"
# Each invocation owns a new directory. Existing publish folders and artifacts are preserved.
$null = New-Item -ItemType Directory -Force -Path $ReleaseRoot
$null = New-Item -ItemType Directory -Path $PublishDir
$StagingCreatedByBuild = $true
$gpuProperty = '-p:IncludeAudioGpu=' + ($includeAi -and !$CpuOnly).ToString().ToLowerInvariant()
$aiProperty = '-p:IncludeAudioAi=' + $includeAi.ToString().ToLowerInvariant()
Invoke-Dotnet -CommandArguments @('restore',$ProjectFile,'-r',$Runtime)
# Framework-dependent publish omits the ~160 MB .NET 10 runtime; ReadyToRun & PDBs stay disabled for lean size.
Invoke-Dotnet -CommandArguments @('publish',$ProjectFile,'-c',$Configuration,'-r',$Runtime,'--self-contained',
    $SelfContained.ToString().ToLowerInvariant(),'-o',$PublishDir,'-p:PublishSingleFile=false','-p:PublishReadyToRun=false',
    '-p:DebugType=none','-p:DebugSymbols=false','-p:SatelliteResourceLanguages=en',$gpuProperty,$aiProperty)

# Remove any stray debug symbols or NuGet IntelliSense XML docs from publish output
Get-ChildItem -LiteralPath $PublishDir -Recurse -File -Include *.pdb,*.xml |
    Where-Object { $_.FullName -notlike (Join-Path $PublishDir 'Resources\*') } |
    Remove-Item -Force -ErrorAction SilentlyContinue

Assert-Exists (Join-Path $PublishDir 'CortexFX.exe')
foreach ($relative in $coreResources) { Assert-Exists (Join-Path $PublishDir ('Resources\' + $relative)) }
$hasMagickNative = (Test-Path -LiteralPath (Join-Path $PublishDir 'Magick.Native-Q8-x64.dll')) -or
    (Test-Path -LiteralPath (Join-Path $PublishDir 'runtimes\win-x64\native\Magick.Native-Q8-x64.dll'))
if (!$hasMagickNative) { throw 'Required ImageMagick native library missing: Magick.Native-Q8-x64.dll' }
$hasPdfium = (Test-Path -LiteralPath (Join-Path $PublishDir 'x64\pdfium.dll')) -or
    (Test-Path -LiteralPath (Join-Path $PublishDir 'pdfium.dll'))
if (!$hasPdfium) { throw 'Required PDFium native library missing: pdfium.dll' }
foreach ($package in $audioLock.packages) {
    if (!$includeAi) { continue }
    if ($package.gpu -and $CpuOnly) { continue }
    foreach ($file in $package.files) { Assert-Exists (Join-Path $PublishDir ($audioLock.resourceDirectory + '/' + $file.target)) }
}
if ((!$includeAi -or $CpuOnly) -and (Test-Path -LiteralPath (Join-Path $PublishDir 'Resources\AudioSeparation\Gpu\onnxruntime_providers_cuda.dll'))) { throw 'Lightweight publish unexpectedly contains the GPU engine.' }

$appSizeMb = [math]::Round(((Get-ChildItem -LiteralPath $PublishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 2)
Write-Host "Published app size: $appSizeMb MB" -ForegroundColor Yellow

if ($CreatePortableZip) {
    Write-Step 'Creating portable ZIP'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($PublishDir,$ZipPath,[IO.Compression.CompressionLevel]::Optimal,$false)
    $zipSizeMb = [math]::Round(((Get-Item -LiteralPath $ZipPath).Length / 1MB), 2)
    Write-Host "Portable ZIP: $ZipPath ($zipSizeMb MB)" -ForegroundColor Green
}
if ($SkipInstaller) { Write-Host 'Installer step skipped.'; Remove-OwnedStaging; return }
$innoLocations = @(${env:ProgramFiles(x86)},$env:ProgramFiles,(Join-Path $env:LOCALAPPDATA 'Programs')) | Where-Object { $_ }
$inno = @($innoLocations | ForEach-Object { Join-Path $_ 'Inno Setup 6\ISCC.exe' } | Where-Object { Test-Path -LiteralPath $_ })
if (!$inno.Count) { Write-Warning "Inno Setup 6 was not found. App folder kept: $PublishDir"; return }
$installerScript = Join-Path $ProjectRoot 'setup.iss'
Assert-Exists $installerScript
Write-Step 'Building installer'
$selfContainedFlag = if ($SelfContained) { '1' } else { '0' }
& $inno[0] "/DMyAppVersion=$Version" "/DMySelfContained=$selfContainedFlag" "/DMyBuildPath=$PublishDir" "/DMyOutputDir=$ReleaseRoot" $installerScript
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed with exit code $LASTEXITCODE." }
Assert-Exists $InstallerPath
$installerSizeMb = [math]::Round(((Get-Item -LiteralPath $InstallerPath).Length / 1MB), 2)
Remove-OwnedStaging
Write-Host "Installer: $InstallerPath ($installerSizeMb MB)" -ForegroundColor Green
