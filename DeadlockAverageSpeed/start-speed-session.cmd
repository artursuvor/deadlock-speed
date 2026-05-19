@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\start-speed-session.ps1" %*
if errorlevel 1 pause
