@echo off
setlocal
pushd "%~dp0" || exit /b 1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -CreatePortableZip %*
set "CORTEX_BUILD_RESULT=%ERRORLEVEL%"
popd
if not "%CORTEX_BUILD_RESULT%"=="0" echo Cortex FX release build failed.
exit /b %CORTEX_BUILD_RESULT%