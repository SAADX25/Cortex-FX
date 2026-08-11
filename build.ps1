[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [bool]$SelfContained = $true,
    [switch]$SkipInstaller,
    [switch]$CreatePortableZip,
    [switch]$KeepStagingFolder
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Assert-InProject {
    param(
        [string]$Path,
        [string]$ProjectRoot
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRoot = [System.IO.Path]::GetFullPath($ProjectRoot)
    if (!$fullPath.StartsWith($fullRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside project root: $fullPath"
    }
}

function Assert-Exists {
    param(
        [string]$Path,
        [string]$Description
    )

    if (!(Test-Path -LiteralPath $Path)) {
        throw "$Description not found: $Path"
    }
}

function Remove-StagingFolder {
    param(
        [string]$Path,
        [switch]$Keep
    )

    if ($Keep) {
        Write-Host "Keeping staging folder: $Path" -ForegroundColor Yellow
        return
    }

    if (Test-Path -LiteralPath $Path) {
        Write-Step "Removing temporary staging folder"
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

$ProjectRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$ProjectFile = Join-Path $ProjectRoot "CortexFX.csproj"
$InstallerScript = Join-Path $ProjectRoot "setup.iss"
$PublishRoot = Join-Path $ProjectRoot "Publish"

if (!(Test-Path -LiteralPath $ProjectFile)) {
    throw "Project file not found: $ProjectFile"
}

[xml]$ProjectXml = Get-Content -LiteralPath $ProjectFile
$Version = $ProjectXml.Project.PropertyGroup.Version | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = "1.5.0"
}

$PublishDir = Join-Path $PublishRoot "CortexFX_v$Version"
$InstallerPath = Join-Path $PublishRoot "CortexFX_Setup_v$Version.exe"
$ZipPath = Join-Path $PublishRoot "CortexFX_Portable_v$Version-$Runtime.zip"
Assert-InProject -Path $PublishRoot -ProjectRoot $ProjectRoot
Assert-InProject -Path $PublishDir -ProjectRoot $ProjectRoot
Assert-InProject -Path $InstallerPath -ProjectRoot $ProjectRoot
Assert-InProject -Path $ZipPath -ProjectRoot $ProjectRoot

Write-Step "Cortex FX build started"
Write-Host "Project: $ProjectFile"
Write-Host "Configuration: $Configuration"
Write-Host "Runtime: $Runtime"
Write-Host "Version: $Version"

Write-Step "Cleaning publish output"
if (Test-Path -LiteralPath $PublishRoot) {
    Remove-Item -LiteralPath $PublishRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $PublishRoot | Out-Null
New-Item -ItemType Directory -Force -Path $PublishDir | Out-Null

Write-Step "Restoring packages"
dotnet restore $ProjectFile

Write-Step "Building"
dotnet build $ProjectFile -c $Configuration --no-restore

Write-Step "Publishing"
dotnet publish $ProjectFile `
    -c $Configuration `
    -r $Runtime `
    --self-contained $SelfContained `
    -o $PublishDir `
    -p:PublishSingleFile=false `
    -p:ReadyToRun=true

Write-Host "Publish output: $PublishDir" -ForegroundColor Green

$RequiredPublishFiles = @(
    (Join-Path $PublishDir "CortexFX.exe"),
    (Join-Path $PublishDir "Resources"),
    (Join-Path $PublishDir "Resources\ffmpeg.exe"),
    (Join-Path $PublishDir "Resources\magick.exe"),
    (Join-Path $PublishDir "Resources\pdftocairo.exe"),
    (Join-Path $PublishDir "Resources\ffmpeg_libs\avcodec-58.dll"),
    (Join-Path $PublishDir "Resources\ffmpeg_libs\avformat-58.dll"),
    (Join-Path $PublishDir "Resources\ffmpeg_libs\avutil-56.dll"),
    (Join-Path $PublishDir "Resources\ffmpeg_libs\swresample-3.dll"),
    (Join-Path $PublishDir "Resources\ffmpeg_libs\swscale-5.dll")
)

Write-Step "Validating publish output"
foreach ($RequiredFile in $RequiredPublishFiles) {
    Assert-Exists -Path $RequiredFile -Description "Required publish file"
}

if ($CreatePortableZip) {
    if (Test-Path -LiteralPath $ZipPath) {
        Remove-Item -LiteralPath $ZipPath -Force
    }

    Write-Step "Creating portable ZIP"
    Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $ZipPath -Force
    Write-Host "Portable ZIP: $ZipPath" -ForegroundColor Green
}

if ($SkipInstaller) {
    Write-Host "Installer step skipped." -ForegroundColor Yellow
    if ($CreatePortableZip) {
        Remove-StagingFolder -Path $PublishDir -Keep:$KeepStagingFolder
    }
    else {
        Write-Host "Staging folder kept because no installer or portable ZIP was requested." -ForegroundColor Yellow
    }
    exit 0
}

$InnoCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

if ($InnoCandidates.Count -eq 0) {
    Write-Host "Inno Setup 6 was not found. Publish succeeded; installer was not created." -ForegroundColor Yellow
    exit 0
}

if (!(Test-Path -LiteralPath $InstallerScript)) {
    Write-Host "setup.iss was not found. Publish succeeded; installer was not created." -ForegroundColor Yellow
    exit 0
}

Write-Step "Building installer"
$InnoCompiler = @($InnoCandidates)[0]
& $InnoCompiler "/DMyAppVersion=$Version" "/DMyBuildPath=$PublishDir" "/DMyOutputDir=$PublishRoot" $InstallerScript

if ($LASTEXITCODE -ne 0) {
    throw "Installer compilation failed with exit code $LASTEXITCODE."
}

Assert-Exists -Path $InstallerPath -Description "Installer"
Remove-StagingFolder -Path $PublishDir -Keep:$KeepStagingFolder

Write-Host "Build and installer completed successfully." -ForegroundColor Green
