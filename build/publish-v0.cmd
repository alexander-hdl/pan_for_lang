@echo off
rem Developer script: builds the self-contained V0 package for the protocol docs\spikes\V0-hardware.md.
rem Result: publish\win-x64\ (the program + 0-mouse.cmd ... 5-latency.cmd) and publish\PenForLang-V0-win-x64.zip.
rem Run from anywhere; requires the .NET SDK from global.json.
setlocal
cd /d "%~dp0.."
if exist publish\win-x64 rmdir /s /q publish\win-x64
dotnet publish src\PenForLang.App.Wpf -c Release -r win-x64 --self-contained -o publish\win-x64
if errorlevel 1 exit /b 1
if exist publish\PenForLang-V0-win-x64.zip del publish\PenForLang-V0-win-x64.zip
powershell -NoProfile -Command "Compress-Archive -Path 'publish\win-x64\*' -DestinationPath 'publish\PenForLang-V0-win-x64.zip'"
if errorlevel 1 exit /b 1
echo.
echo Done: publish\win-x64 and publish\PenForLang-V0-win-x64.zip
