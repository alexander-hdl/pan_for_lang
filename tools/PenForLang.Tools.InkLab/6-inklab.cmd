@echo off
rem InkLab (V0.9): recording adult reference writing, replay and annotation. See InkLab-README.md next to this file.
rem Pen mode by default (WM_POINTER). For work without a tablet add --mouse. Records go to %LOCALAPPDATA%\PenForLang\corpus (change: --corpus-dir <folder>).
start "" "%~dp0PenForLang.Tools.InkLab.exe" %*
