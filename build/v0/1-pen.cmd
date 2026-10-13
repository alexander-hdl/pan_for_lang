@echo off
rem Main check: the PEN through WM_POINTER (WPF stylus is off). Fullscreen window on the pen monitor.
rem Esc closes the window; the report is saved on the Desktop. Wrong monitor? Press Esc, then run 1-pen-monitor-1.cmd / -2 / -3.
start "" "%~dp0PenForLang.App.Wpf.exe" --exit-after 900 %*
