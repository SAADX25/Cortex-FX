@echo off
setlocal
cd /d "%~dp0"
echo Starting Cortex FX...
dotnet run --project "%~dp0CortexFX.csproj"
pause
