@echo off
setlocal
pushd "%~dp0"

echo Building NeonMon - Release...
dotnet build NeonMon.csproj -c Release --no-restore -p:TargetPlatformDisplayName=Windows
set "BUILD_EXIT=%ERRORLEVEL%"
set "APP_PATH=%~dp0bin\Release\net9.0-windows\NeonMon.exe"

echo.
if not "%BUILD_EXIT%"=="0" goto build_failed

echo Build succeeded.
echo App: %APP_PATH%
echo.
set "RUN_APP="
set /p "RUN_APP=Run NeonMon now? [y/N]: "
if /i "%RUN_APP%"=="Y" start "" "%APP_PATH%"
goto finish

:build_failed
echo Build failed with exit code %BUILD_EXIT%.

:finish
echo.
pause
popd
exit /b %BUILD_EXIT%
