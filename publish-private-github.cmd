@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-private-github.ps1" %*
if errorlevel 1 pause
