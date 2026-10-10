@echo off
rem Registers GanttScheduler as a Windows service and starts it. Options: --port 8080 --remoteip 10.0.0.0/8,192.168.0.0/16
"%~dp0GanttScheduler.exe" install --pause %*
