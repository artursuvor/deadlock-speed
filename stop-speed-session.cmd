@echo off
setlocal
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0stop-speed-session.ps1" %*
if errorlevel 1 pause
