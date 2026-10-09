@echo off
rem Registers GanttScheduler as a Windows service and starts it. Options: --port 8080
"%~dp0GanttScheduler.exe" install --pause %*
