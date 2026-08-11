@echo off
setlocal
cd /d "%~dp0"
echo Current directory: %CD%
echo Starting Cortex FX...
dotnet run --project "CortexFX.csproj"
if %errorlevel% neq 0 (
    echo.
    echo An error occurred.
    pause
)
