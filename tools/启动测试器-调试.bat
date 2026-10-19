@echo off
rem Debug launcher: keeps the console open so tracebacks are readable.
chcp 65001 >nul
cd /d "%~dp0"

py -3 "buff_gui.py"
echo.
echo exit code: %errorlevel%
pause
