@echo off
setlocal
set "PROJECT_DIR=e:\Code-Setup\Cortex-FX"
set "VERSION=1.6.0"
set "PUBLISH_DIR=%PROJECT_DIR%\Publish\CortexFX_v%VERSION%"
set "OUTPUT_DIR=%PROJECT_DIR%\Publish"
set "INNO=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"

echo.
echo ==========================================
echo   Cortex FX - Build Release v%VERSION%
echo ==========================================
echo.

echo [1/5] Cleaning old Publish folder...
if exist "%OUTPUT_DIR%" (
    rmdir /s /q "%OUTPUT_DIR%"
)
mkdir "%OUTPUT_DIR%"
mkdir "%PUBLISH_DIR%"

echo [2/5] Restoring packages...
dotnet restore "%PROJECT_DIR%\CortexFX.csproj"
if %errorlevel% neq 0 goto :error

echo [3/5] Publishing (self-contained)...
dotnet publish "%PROJECT_DIR%\CortexFX.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=false ^
    -p:ReadyToRun=true ^
    -o "%PUBLISH_DIR%"
if %errorlevel% neq 0 goto :error

echo [4/5] Creating ZIP portable...
powershell -Command "Compress-Archive -Path '%PUBLISH_DIR%\*' -DestinationPath '%OUTPUT_DIR%\CortexFX_Portable_v%VERSION%.zip' -Force"

echo [5/5] Building Installer (Inno Setup)...
if not exist "%INNO%" (
    echo WARNING: Inno Setup 6 not found, skipping installer.
    goto :done
)
"%INNO%" "/DMyAppVersion=%VERSION%" "/DMyBuildPath=%PUBLISH_DIR%" "/DMyOutputDir=%OUTPUT_DIR%" "%PROJECT_DIR%\setup.iss"
if %errorlevel% neq 0 goto :error

:done
echo.
echo ==========================================
echo   Done! Output files:
echo ==========================================
echo.
if exist "%PUBLISH_DIR%\CortexFX.exe" (
    for %%F in ("%PUBLISH_DIR%\CortexFX.exe") do echo   EXE      : %%~nxF  [%%~zF bytes]
)
if exist "%OUTPUT_DIR%\CortexFX_Portable_v%VERSION%.zip" (
    for %%F in ("%OUTPUT_DIR%\CortexFX_Portable_v%VERSION%.zip") do echo   ZIP      : %%~nxF  [%%~zF bytes]
)
if exist "%OUTPUT_DIR%\CortexFX_Setup_v%VERSION%.exe" (
    for %%F in ("%OUTPUT_DIR%\CortexFX_Setup_v%VERSION%.exe") do echo   INSTALLER: %%~nxF  [%%~zF bytes]
)
echo.

explorer "%OUTPUT_DIR%"
goto :end

:error
echo.
echo !! Build failed with error %errorlevel%
pause
exit /b %errorlevel%

:end
pause
