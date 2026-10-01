@echo off
setlocal
pushd "%~dp0"

echo Building NeonMon - Release...
dotnet build NeonMon.csproj -c Release --no-restore -p:TargetPlatformDisplayName=Windows
set "BUILD_EXIT=%ERRORLEVEL%"

echo.
if not "%BUILD_EXIT%"=="0" (
    echo Build failed with exit code %BUILD_EXIT%.
) else (
    echo Build succeeded.
    echo App: %~dp0bin\Release\net9.0-windows\NeonMon.exe
)

echo.
pause
popd
exit /b %BUILD_EXIT%
