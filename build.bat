@echo off
chcp 65001 >nul
title LiquidCloud - build
cd /d "%~dp0src\LiquidCloud"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET 8 SDK is not installed.
  echo Download it here: https://dotnet.microsoft.com/download/dotnet/8.0  ^(SDK, Windows x64^)
  start "" https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)

echo Building LiquidCloud... the first time takes a few minutes.
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o "%~dp0LiquidCloud"
if errorlevel 1 (
  echo.
  echo Build failed. Copy the red text above and send it to whoever helps you.
  pause
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Desktop')+'\LiquidCloud.lnk'); $s.TargetPath='%~dp0LiquidCloud\LiquidCloud.exe'; $s.WorkingDirectory='%~dp0LiquidCloud'; $s.IconLocation='%~dp0LiquidCloud\LiquidCloud.exe,0'; $s.Save()"

echo.
echo Done! LiquidCloud is in the "LiquidCloud" folder and there is a shortcut on your desktop.
start "" "%~dp0LiquidCloud\LiquidCloud.exe"
timeout /t 4 >nul
