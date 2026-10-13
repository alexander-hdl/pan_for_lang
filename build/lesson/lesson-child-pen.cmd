@echo off
rem Lesson V0.8 (playtest, step 2): a CHILD with a parent next to them. The PEN on the pen monitor. NOTHING is recorded:
rem no --dev, so no penlog and no overlay (a child recording needs consent, ARCHITECTURE.md 3.4). Esc closes the window.
start "" "%~dp0PenForLang.App.Wpf.exe" --exit-after 1800 %*
