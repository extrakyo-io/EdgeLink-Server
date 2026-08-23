@echo off
rem ---------------------------------------------------------------------------
rem run-platform-sim.bat -- local end-to-end rig for the platform TCP link.
rem
rem   [Test Console] --KV--> EdgeLink :47900 --binary--> [Simulator :47802]
rem   [Status Watcher] tails the decoded status stream (prints only on change).
rem
rem Double-click, or run with no argument, to open all three windows.
rem Needs EdgeLink already running with docs/PlatformTcp.settings.json imported.
rem
rem ASCII only on purpose: cmd reads .bat in the system ANSI codepage, so UTF-8
rem Chinese in this file gets mis-decoded and eats adjacent characters (it will
rem swallow things like the "set " in "set VAR=..."). All Chinese output is
rem printed by python, which handles UTF-8 correctly once chcp 65001 is active.
rem ---------------------------------------------------------------------------
setlocal
set DOCS=%~dp0
set PYTHONIOENCODING=utf-8
chcp 65001 >nul

if "%~1"=="sim"     goto sim
if "%~1"=="watch"   goto watch
if "%~1"=="console" goto console

echo Opening: Simulator / Status Watcher / Test Console
start "" cmd /k ""%~f0" sim"
timeout /t 3 >nul
start "" cmd /k ""%~f0" watch"
timeout /t 1 >nul
start "" cmd /k ""%~f0" console"
exit /b

:sim
title Platform Simulator (Unity 47802)
cd /d "%DOCS%"
python PlatformTcpSimulator.py --hz 2 --verbose
goto end

:watch
title Platform Status Watcher (change only)
cd /d "%DOCS%"
python PlatformTcpConsole.py --port 47900 --watch 0
goto end

:console
title Downstream Test Console (EdgeLink 47900)
cd /d "%DOCS%"
python PlatformTcpConsole.py --port 47900
goto end

:end
echo.
echo (process ended -- close this window or press a key to exit)
pause >nul
