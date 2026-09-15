@echo off
setlocal
REM build.bat - NativeAOT low-memory build (WinExe, no console, ~8MB disk, ~10MB RAM)
REM Requires .NET 8 SDK + Windows SDK 10.0.19041+ (Desktop development with C++)
REM Output: bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\

echo Building NativeAOT (WinExe, Size optimized, Trimmed, InvariantGlobalization)...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:PublishSingleFile=false -p:OptimizationPreference=Size
if %ERRORLEVEL% NEQ 0 (
  echo Build failed.
  exit /b 1
)
echo.
echo Publish output:
for %%F in (bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\*.exe) do echo  %%~fF  %%~zF bytes
echo.
echo To install into W:\_programs\AutoDefaultToHeadset:
echo  install.bat
pause
