@echo off
rem Windowed run (not fullscreen, not topmost): needed to check strokes that leave the canvas and touches outside the window.
start "" "%~dp0PenForLang.App.Wpf.exe" --windowed --exit-after 900 %*
