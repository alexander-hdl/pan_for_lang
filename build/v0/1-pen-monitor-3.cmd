@echo off
rem Same as 1-pen.cmd, but the window is forced onto monitor number 3 (numbers are listed in the panel and in the report).
start "" "%~dp0PenForLang.App.Wpf.exe" --sandbox --monitor 3 --dev --exit-after 900 %*
