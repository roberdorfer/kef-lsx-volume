@echo off
rem Equivalent of the Makefile: make.cmd [-s] [target ...]   (targets: see make.ps1; docs/DESIGN.md 10.5)
rem The execution policy is bypassed for this one process; Set-ExecutionPolicy is never called.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0make.ps1" %*
exit /b %ERRORLEVEL%
