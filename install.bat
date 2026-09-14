@echo off
setlocal
REM install.bat - NativeAOT: install into W:\_programs\AutoDefaultToHeadset, pick friendly name, create Startup shortcut
REM Usage: double-click, or: install.bat [Path\To\Exe]

set "TARGET_DIR=W:\_programs\AutoDefaultToHeadset"
for %%I in ("%TARGET_DIR%") do set "TARGET_DIR=%%~fI"

set "SCRIPT_DIR=%~dp0"
if "%SCRIPT_DIR:~-1%"=="\" set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
for %%I in ("%SCRIPT_DIR%") do set "SCRIPT_DIR=%%~fI"

if "%~1"=="" (
  for %%F in (bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\*.exe) do (
    set "FOUND_EXE=%%~fF"
    goto :found
  )
  for %%F in (bin\Release\net8.0-windows10.0.19041.0\win-x64\*.exe) do (
    set "FOUND_EXE=%%~fF"
    goto :found
  )
  for %%F in ("%~dp0*.exe") do (
    set "FOUND_EXE=%%~fF"
    goto :found
  )
  for %%F in (*.exe) do (
    set "FOUND_EXE=%%~fF"
    goto :found
  )
  echo No .exe found. Build first: build.bat
  pause
  exit /b 1
) else (
  if exist "%~1" (
    for %%I in ("%~1") do set "FOUND_EXE=%%~fI"
  ) else (
    echo Specified exe "%~1" not found.
    pause
    exit /b 1
  )
)

:found
echo Found source binary: "%FOUND_EXE%"
for %%I in ("%FOUND_EXE%") do (
  set "EXE_NAME=%%~nxI"
  set "EXE_SRC_DIR=%%~dpI"
)

echo.
echo Terminating running AutoDefaultToHeadset processes...
taskkill /F /T /IM AutoDefaultToHeadset.exe >nul 2>&1
taskkill /F /T /IM AutoDefaultToHeadset.NativeAOT.exe >nul 2>&1
taskkill /F /T /FI "IMAGENAME eq AutoDefaultToHeadset*" >nul 2>&1
timeout /t 1 /nobreak >nul 2>&1

if exist "%TARGET_DIR%" (
  if /i "%SCRIPT_DIR%"=="%TARGET_DIR%" (
    echo Cleaning existing files in "%TARGET_DIR%"...
    for /f "delims=" %%F in ('dir /b /a-d "%TARGET_DIR%"') do (
      if /i not "%%F"=="install.bat" del /f /q "%TARGET_DIR%\%%F" >nul 2>&1
    )
    for /f "delims=" %%D in ('dir /b /ad "%TARGET_DIR%"') do (
      rd /s /q "%TARGET_DIR%\%%D" >nul 2>&1
    )
  ) else (
    echo Deleting existing "%TARGET_DIR%"...
    rd /s /q "%TARGET_DIR%"
    if exist "%TARGET_DIR%" (
      timeout /t 1 /nobreak >nul 2>&1
      rd /s /q "%TARGET_DIR%"
    )
  )
)

if not exist "%TARGET_DIR%" (
  mkdir "%TARGET_DIR%"
  if errorlevel 1 (
    echo Error: Failed to create "%TARGET_DIR%".
    pause
    exit /b 1
  )
)

echo Installing to "%TARGET_DIR%"...
if /i not "%EXE_SRC_DIR%"=="%TARGET_DIR%\" (
  copy /y "%FOUND_EXE%" "%TARGET_DIR%\" >nul
  if errorlevel 1 (
    echo Error: Failed to copy "%FOUND_EXE%" to "%TARGET_DIR%".
    pause
    exit /b 1
  )
  for %%I in ("%FOUND_EXE%") do (
    if exist "%EXE_SRC_DIR%%%~nI.pdb" copy /y "%EXE_SRC_DIR%%%~nI.pdb" "%TARGET_DIR%\" >nul 2>&1
  )
  if exist "%SCRIPT_DIR%\README.md" copy /y "%SCRIPT_DIR%\README.md" "%TARGET_DIR%\" >nul 2>&1
  if exist "%SCRIPT_DIR%\install.bat" copy /y "%SCRIPT_DIR%\install.bat" "%TARGET_DIR%\" >nul 2>&1
  if exist "%SCRIPT_DIR%\uninstall.bat" copy /y "%SCRIPT_DIR%\uninstall.bat" "%TARGET_DIR%\" >nul 2>&1
)

if /i not "%EXE_NAME%"=="AutoDefaultToHeadset.exe" (
  copy /y "%TARGET_DIR%\%EXE_NAME%" "%TARGET_DIR%\AutoDefaultToHeadset.exe" >nul 2>&1
)

set "INSTALLED_EXE=%TARGET_DIR%\%EXE_NAME%"
echo.
echo Configuring scheduled task and device selection...
"%INSTALLED_EXE%" --install
if %ERRORLEVEL% EQU 0 (
  echo.
  echo Installer completed successfully.
  echo Installed into: "%TARGET_DIR%"
  echo For diagnostics run: "%INSTALLED_EXE%" --verbose --list-devices
) else (
  echo.
  echo Installer failed. Try running as Administrator.
)
pause
