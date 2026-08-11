@echo off
setlocal
set "PROJECT_DIR=e:\Code-Setup\Cortex-FX"
echo Starting Cortex FX from: %PROJECT_DIR%
dotnet run --project "%PROJECT_DIR%\CortexFX.csproj"
if %errorlevel% neq 0 (
    echo.
    echo An error occurred.
    pause
)
