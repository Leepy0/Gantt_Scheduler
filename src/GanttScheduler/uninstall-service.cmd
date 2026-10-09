@echo off
rem Stops and removes the GanttScheduler Windows service. Files and data are kept.
"%~dp0GanttScheduler.exe" uninstall --pause %*
