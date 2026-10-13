@echo off
rem Lesson V0.8 (playtest, step 1): an ADULT checks the scenario with the MOUSE as a pen. Windowed; the dev recording is ON.
rem Every attempt is written to %LOCALAPPDATA%\PenForLang\dev\penlogs (adult only, no child data). Esc closes the window.
start "" "%~dp0PenForLang.App.Wpf.exe" --mouse --windowed --dev --exit-after 1800 %*
