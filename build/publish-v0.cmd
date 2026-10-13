@echo off
rem Developer script: builds the self-contained V0 package for the protocol docs\spikes\V0-hardware.md.
rem Result: publish\win-x64\ (the program + InkLab + 0-mouse.cmd ... 6-inklab.cmd for the sandbox protocol and lesson-*.cmd for the V0.8 playtest) and publish\PenForLang-V0-win-x64.zip.
rem Run from anywhere; requires the .NET SDK from global.json.
setlocal
cd /d "%~dp0.."
if exist publish\win-x64 rmdir /s /q publish\win-x64
dotnet publish src\PenForLang.App.Wpf -c Release -r win-x64 --self-contained -o publish\win-x64
if errorlevel 1 exit /b 1
rem InkLab (V0.9) goes into the same folder (shared runtime and content). It is published to a temporary folder first and merged by
rem build\merge-publish.ps1, which STOPS if any overlapping file differs from the main package (no silent version mix).
if exist publish\inklab-tmp rmdir /s /q publish\inklab-tmp
dotnet publish tools\PenForLang.Tools.InkLab -c Release -r win-x64 --self-contained -o publish\inklab-tmp
if errorlevel 1 exit /b 1
powershell -NoProfile -ExecutionPolicy Bypass -File build\merge-publish.ps1 -Source publish\inklab-tmp -Target publish\win-x64
if errorlevel 1 (
  echo ERROR: InkLab publish conflicts with the main package, see above.
  exit /b 1
)
rmdir /s /q publish\inklab-tmp
if exist publish\PenForLang-V0-win-x64.zip del publish\PenForLang-V0-win-x64.zip
powershell -NoProfile -Command "Compress-Archive -Path 'publish\win-x64\*' -DestinationPath 'publish\PenForLang-V0-win-x64.zip'"
if errorlevel 1 exit /b 1
echo.
echo Done: publish\win-x64 and publish\PenForLang-V0-win-x64.zip
