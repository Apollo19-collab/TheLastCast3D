@echo off
rem Double-click to build The Last Cast (first person) into Game\TheLastCast.exe.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Make-TheLastCast-Exe.ps1" %*
echo.
pause
