@echo off
rem OPTIONAL run: the WPF stylus on WM_POINTER (Switch.System.Windows.Input.Stylus.EnablePointerSupport=true).
start "" "%~dp0PenForLang.App.Wpf.exe" --wpf-pointer --exit-after 900 %*
