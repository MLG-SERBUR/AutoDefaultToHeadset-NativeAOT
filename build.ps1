# build.ps1 - NativeAOT low-memory build
param([string]$Config = "Release")
Write-Host "Building NativeAOT WinExe..." -ForegroundColor Cyan
dotnet publish -c $Config -r win-x64 --self-contained true -p:PublishAot=true -p:PublishSingleFile=false -p:OptimizationPreference=Size
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed"; exit 1 }
$publish = "bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"
Get-ChildItem $publish -Filter *.exe | ForEach-Object { Write-Host "$($_.FullName) $($_.Length) bytes" }
Write-Host "Done. To install into W:\_programs\AutoDefaultToHeadset: .\install.bat" -ForegroundColor Green
