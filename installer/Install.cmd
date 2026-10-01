@echo off
setlocal
set "SRC=%~dp0ClaudeEdgeBridge.exe"
set "DIR=%LOCALAPPDATA%\ClaudeEdgeBridge"
set "EXE=%DIR%\ClaudeEdgeBridge.exe"
set "LOG=%DIR%\install.log"

echo === Claude Edge Bridge - install ===
if not exist "%SRC%" set "ERR=ClaudeEdgeBridge.exe not found next to Install.cmd. Extract the whole zip first, then run this again." & goto :fail

echo [1/4] Stopping any running bridge...
taskkill /f /im ClaudeEdgeBridge.exe >nul 2>&1
ping -n 2 127.0.0.1 >nul

echo [2/4] Copying to "%DIR%"...
if not exist "%DIR%" mkdir "%DIR%"
if errorlevel 1 set "ERR=Could not create the install folder." & goto :fail
copy /y "%SRC%" "%EXE%" >nul
if errorlevel 1 set "ERR=Could not copy ClaudeEdgeBridge.exe." & goto :fail

echo [3/4] Adding to Windows Startup...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Unblock-File -LiteralPath $env:EXE; $s=(New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath('Startup')) 'Claude Edge Bridge.lnk')); $s.TargetPath=$env:EXE; $s.WorkingDirectory=$env:DIR; $s.Save()"
if errorlevel 1 set "ERR=Could not create the Startup shortcut." & goto :fail

echo [4/4] Starting the bridge...
start "" "%EXE%"
if errorlevel 1 set "ERR=Could not start ClaudeEdgeBridge.exe." & goto :fail

echo.
echo Done. The bridge runs in the background (no window) and starts with Windows.
echo.
echo Next step: Open iCUE, go to your XENEON EDGE dashboard, and import the .icuewidget file from this folder.
echo.
pause
exit /b 0

:fail
echo.
echo FAILED: %ERR%
if not exist "%DIR%" mkdir "%DIR%" >nul 2>&1
echo %date% %time% Install failed: %ERR%>>"%LOG%"
echo Details were written to "%LOG%"
pause
exit /b 1
