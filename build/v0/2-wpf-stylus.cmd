@echo off
rem Comparison run: the WPF stylus (WISP) stays ENABLED. Same writing scenario as 1-pen.cmd; the report shows what changes.
start "" "%~dp0PenForLang.App.Wpf.exe" --sandbox --wpf-stylus --dev --exit-after 900 %*
