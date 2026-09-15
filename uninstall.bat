@echo off
setlocal

set "TARGET_DIR=%~dp0"
if "%TARGET_DIR:~-1%"=="\" set "TARGET_DIR=%TARGET_DIR:~0,-1%"

echo Stopping AutoDefaultToHeadset...
schtasks /Delete /TN "AutoDefaultToHeadset.NativeAOT" /F >nul 2>&1
taskkill /F /T /IM AutoDefaultToHeadset.exe >nul 2>&1
taskkill /F /T /IM AutoDefaultToHeadset.NativeAOT.exe >nul 2>&1

echo Removing Add/Remove Programs entry...
reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AutoDefaultToHeadset.NativeAOT" /f >nul 2>&1

echo Removing "%TARGET_DIR%"...
start "" /b cmd.exe /d /c "timeout /t 2 /nobreak >nul & rd /s /q ""%TARGET_DIR%"""
exit /b 0
