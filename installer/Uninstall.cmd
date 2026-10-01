@echo off
setlocal
set "DIR=%LOCALAPPDATA%\ClaudeEdgeBridge"
set "LOG=%DIR%\install.log"

echo === Claude Edge Bridge - uninstall ===

echo [1/3] Stopping the bridge...
taskkill /f /im ClaudeEdgeBridge.exe >nul 2>&1
ping -n 2 127.0.0.1 >nul

echo [2/3] Removing the Startup shortcut...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Remove-Item -LiteralPath (Join-Path ([Environment]::GetFolderPath('Startup')) 'Claude Edge Bridge.lnk') -Force -ErrorAction SilentlyContinue; exit 0"
if errorlevel 1 set "ERR=Could not remove the Startup shortcut." & goto :fail

echo [3/3] Deleting "%DIR%"...
if exist "%DIR%" rmdir /s /q "%DIR%"
if exist "%DIR%" set "ERR=Could not delete the install folder. Is something still using it?" & goto :fail

echo.
echo Done. Your logs in "%APPDATA%\ClaudeEdgeBridge\logs" were left alone; delete that folder yourself if you want.
echo.
echo Remember: remove the Claude widget from iCUE yourself (XENEON EDGE dashboard).
echo.
pause
exit /b 0

:fail
echo.
echo FAILED: %ERR%
if not exist "%DIR%" mkdir "%DIR%" >nul 2>&1
echo %date% %time% Uninstall failed: %ERR%>>"%LOG%"
echo Details were written to "%LOG%"
pause
exit /b 1
