@echo off
rem OPTIONAL: latency measurement pen -> pixel with a 240 fps camera (docs\spikes\V0-hardware.md, section 9).
rem The pen through WM_POINTER, fullscreen on the pen monitor, white background, a big pink marker under the pen, a frame counter in the corner.
rem L leaves the mode; Esc closes the window; the report on the Desktop contains the internal latency (point -> Rendering).
start "" "%~dp0PenForLang.App.Wpf.exe" --latency --no-dev --exit-after 900 %*
