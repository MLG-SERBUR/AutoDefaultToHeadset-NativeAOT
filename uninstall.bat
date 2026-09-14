@echo off
setlocal
echo Uninstalling AutoDefaultToHeadset (NativeAOT)...

REM Remove Scheduled Task
schtasks /Delete /TN "AutoDefaultToHeadset.NativeAOT" /F >nul 2>&1

REM Terminate process if running
taskkill /F /T /IM AutoDefaultToHeadset.exe >nul 2>&1
taskkill /F /T /IM AutoDefaultToHeadset.NativeAOT.exe >nul 2>&1
timeout /t 1 /nobreak >nul 2>&1

REM Remove Add/Remove Programs entry
reg delete "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AutoDefaultToHeadset.NativeAOT" /f >nul 2>&1

REM Self-delete installed directory (leave uninstall.bat to self-destruct if possible, or just delete everything else)
set "TARGET_DIR=W:\_programs\AutoDefaultToHeadset"
if exist "%TARGET_DIR%" (
  echo Removing files from %TARGET_DIR%...
  del /f /q "%TARGET_DIR%\*.exe" >nul 2>&1
  del /f /q "%TARGET_DIR%\*.pdb" >nul 2>&1
  del /f /q "%TARGET_DIR%\*.md" >nul 2>&1
  del /f /q "%TARGET_DIR%\install.bat" >nul 2>&1
  REM Remove startup shortcut if any existed previously
  del /f /q "%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\AutoDefaultToHeadset.lnk" >nul 2>&1
  
  echo Uninstall complete.
  REM Delete script itself and folder
  (goto) 2>nul & del "%~f0" & rmdir "%TARGET_DIR%"
) else (
  echo Uninstall complete.
  pause
)
