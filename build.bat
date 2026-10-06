@echo off
setlocal
pushd "%~dp0"

set "APP_PATH=%~dp0bin\Release\net9.0-windows\NeonMon.exe"

tasklist /FI "IMAGENAME eq NeonMon.exe" | find /I "NeonMon.exe" >nul
if errorlevel 1 goto build

set "CLOSE_APP="
set /p "CLOSE_APP=NeonMon is running and will block the build. Close it? [Y/n]: "
if /i "%CLOSE_APP%"=="N" goto build

echo Closing NeonMon...
if exist "%APP_PATH%" "%APP_PATH%" --exit
for /L %%i in (1,1,5) do (
    tasklist /FI "IMAGENAME eq NeonMon.exe" | find /I "NeonMon.exe" >nul || goto closed
    timeout /t 1 /nobreak >nul
)
echo NeonMon did not exit in time; forcing it to close.
taskkill /IM NeonMon.exe /F >nul 2>&1

:closed
echo NeonMon closed.
echo.

:build
echo Building NeonMon - Release...
dotnet build NeonMon.csproj -c Release --no-restore -p:TargetPlatformDisplayName=Windows
set "BUILD_EXIT=%ERRORLEVEL%"

echo.
if not "%BUILD_EXIT%"=="0" goto build_failed

echo Build succeeded.
echo App: %APP_PATH%
echo.
set "RUN_APP="
set /p "RUN_APP=Run NeonMon now? [Y/n]: "
if /i not "%RUN_APP%"=="N" start "" "%APP_PATH%"
goto finish

:build_failed
echo Build failed with exit code %BUILD_EXIT%.

:finish
echo.
pause
popd
exit /b %BUILD_EXIT%
