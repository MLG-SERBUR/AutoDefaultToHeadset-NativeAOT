@echo off
setlocal
REM install.bat - NativeAOT: pick exact friendly name (persistent), create Startup shortcut (no console window)
REM Usage: double-click, or: install.bat [Path\To\Exe]

if "%~1"=="" (
  for %%F in (*.exe) do (
    set "FOUND_EXE=%%~fF"
    goto :found
  )
  for %%F in (bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\*.exe) do (
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
echo Using "%FOUND_EXE%"
"%FOUND_EXE%" --install
if %ERRORLEVEL% EQU 0 (
  echo Installer completed. Shortcut uses exact name match (no ID).
  echo For diagnostics run: "%FOUND_EXE%" --verbose --list-devices
) else (
  echo Installer failed. Try running as Administrator.
)
pause
