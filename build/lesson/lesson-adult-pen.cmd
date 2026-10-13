@echo off
rem Lesson V0.8 (playtest, step 1): an ADULT plays the scenario with the PEN on the pen monitor. The dev recording is ON
rem (every attempt goes to %LOCALAPPDATA%\PenForLang\dev\penlogs). Wrong monitor? Add --monitor 1 / 2 / 3. Esc closes the window.
start "" "%~dp0PenForLang.App.Wpf.exe" --dev --exit-after 1800 %*
