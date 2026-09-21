@echo off
setlocal
cd /d "%~dp0"

echo [ShelfRow] Launching...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
