@echo off
rem Registers GanttScheduler as a Windows service and starts it. Options: --port 8080 --remoteip 10.0.0.0/8,192.168.0.0/16
rem Kept ASCII on purpose: cmd reads this file in the console code page.
rem The program shows its own result (in Korean) and waits for Enter, returning 0 or 1. This script only
rem steps in when the program could not run at all (zip not extracted, blocked by security software or policy).
setlocal
if exist "%~dp0GanttScheduler.exe" goto run
echo GanttScheduler.exe was not found next to this file.
echo Extract the whole zip to a folder first (e.g. D:\GanttScheduler) and run this file from that folder.
set rc=1
goto stop
:run
"%~dp0GanttScheduler.exe" install --pause %*
set rc=%errorlevel%
if "%rc%"=="0" goto done
if "%rc%"=="1" goto done
echo GanttScheduler.exe could not run (code %rc%).
echo Check that the zip was fully extracted and that security software or policy did not block it.
:stop
pause
:done
endlocal & exit /b %rc%
