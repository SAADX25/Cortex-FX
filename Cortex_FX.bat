@echo off
setlocal
pushd "%~dp0" || exit /b 1
echo Starting Cortex FX from: %CD%
dotnet run --project "%~dp0CortexFX.csproj"
set "CORTEX_RESULT=%ERRORLEVEL%"
popd
if not "%CORTEX_RESULT%"=="0" (
    echo Cortex FX could not start. Check the .NET SDK and local Resources.
    pause
)
exit /b %CORTEX_RESULT%