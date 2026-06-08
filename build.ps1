[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [bool]$SelfContained = $true,
    [switch]$SkipInstaller,
    [switch]$CreatePortableZip
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
    $Version = "0.0.0"
}

$PublishDir = Join-Path $PublishRoot "CortexFX_v$Version"
Assert-InProject -Path $PublishRoot -ProjectRoot $ProjectRoot
Assert-InProject -Path $PublishDir -ProjectRoot $ProjectRoot

Write-Step "Cortex FX build started"
Write-Host "Project: $ProjectFile"
Write-Host "Configuration: $Configuration"
Write-Host "Runtime: $Runtime"
Write-Host "Version: $Version"

Write-Step "Cleaning publish directory"
if (Test-Path -LiteralPath $PublishDir) {
    Remove-Item -LiteralPath $PublishDir -Recurse -Force
}
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

if ($CreatePortableZip) {
    $ZipPath = Join-Path $PublishRoot "CortexFX_Portable_v$Version-$Runtime.zip"
    Assert-InProject -Path $ZipPath -ProjectRoot $ProjectRoot

    if (Test-Path -LiteralPath $ZipPath) {
        Remove-Item -LiteralPath $ZipPath -Force
    }

    Write-Step "Creating portable ZIP"
    Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $ZipPath -Force
    Write-Host "Portable ZIP: $ZipPath" -ForegroundColor Green
}

if ($SkipInstaller) {
    Write-Host "Installer step skipped." -ForegroundColor Yellow
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
& $InnoCandidates[0] "/DMyBuildPath=$PublishDir" "/DMyOutputDir=$PublishRoot" $InstallerScript

if ($LASTEXITCODE -ne 0) {
    throw "Installer compilation failed with exit code $LASTEXITCODE."
}

Write-Host "Build and installer completed successfully." -ForegroundColor Green
