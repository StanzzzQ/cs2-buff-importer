@echo off
rem ASCII-only on purpose: cmd may parse this file with a non-UTF8 code page.
chcp 65001 >nul
cd /d "%~dp0"

rem Use the py launcher for the *check* (console app, so errorlevel is reliable).
rem NOTE: "python" in WindowsApps is a Microsoft Store stub and does not count.
py -3 -c "import tkinter" >nul 2>nul
if %errorlevel%==0 goto :launch

rem Fallback: pythonw.exe from a normal install
set "PYW="
for %%D in ("%LOCALAPPDATA%\Programs\Python" "%ProgramFiles%" "%ProgramFiles(x86)%") do (
    if exist "%%~D" (
        for /d %%V in ("%%~D\Python3*") do (
            if not defined PYW if exist "%%~V\pythonw.exe" set "PYW=%%~V\pythonw.exe"
        )
    )
)
if defined PYW (
    start "" "%PYW%" "buff_gui.py"
    exit /b
)

echo.
echo Python 3.10+ with tkinter was not found.
echo Install it from https://www.python.org/downloads/ and tick "Add python.exe to PATH".
echo.
pause
exit /b 1

:launch
rem pyw = windowless launcher; fall back to py (console stays, errors stay visible)
where pyw >nul 2>nul
if errorlevel 1 (
    start "" py -3 "buff_gui.py"
) else (
    start "" pyw -3 "buff_gui.py"
)
exit /b 0
