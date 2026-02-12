# Cortex FX Build & Installer Script

$projectPath = "e:\Code-Setup\Cortex FX"
$releasePath = "$projectPath\Release"
$isccPath = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"

Write-Host "Starting Cortex FX Build Process..." -ForegroundColor Cyan

# 1. Clean previous build
Write-Host "Cleaning previous builds..." -ForegroundColor Yellow
if (Test-Path "bin\Release") { Remove-Item "bin\Release" -Recurse -Force }
if (Test-Path $releasePath) { Remove-Item $releasePath -Recurse -Force }

# 2. Publish .NET App
Write-Host "Publishing .NET Application..." -ForegroundColor Yellow
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:ReadyToRun=true

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build Failed!" -ForegroundColor Red
    exit
}

Write-Host "Build Successful!" -ForegroundColor Green

# 3. Compile Installer
Write-Host "Checking for Inno Setup..." -ForegroundColor Yellow
if (Test-Path $isccPath) {
    Write-Host "Compiling Installer with Inno Setup..." -ForegroundColor Cyan
    & $isccPath "setup.iss"
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Installer Created Successfully!" -ForegroundColor Green
        Invoke-Item $releasePath
    } else {
        Write-Host "Installer Compilation Failed!" -ForegroundColor Red
    }
} else {
    Write-Host "Inno Setup Compiler (ISCC.exe) not found at: $isccPath" -ForegroundColor Red
    Write-Host "Please install Inno Setup 6+" -ForegroundColor Red
}
