@echo off
rem Check 0: the program itself. The MOUSE acts as a pen (no tablet needed). A window opens, hold the left button and draw.
rem Esc closes the window; the report is saved on the Desktop. Extra arguments are passed through.
start "" "%~dp0PenForLang.App.Wpf.exe" --sandbox --mouse --windowed --dev --exit-after 900 %*
