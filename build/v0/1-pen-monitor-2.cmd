@echo off
rem Same as 1-pen.cmd, but the window is forced onto monitor number 2 (numbers are listed in the panel and in the report).
start "" "%~dp0PenForLang.App.Wpf.exe" --monitor 2 --dev --exit-after 900 %*
