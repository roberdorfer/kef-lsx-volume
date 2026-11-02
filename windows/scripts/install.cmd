@echo off
rem Starter for install.ps1 (docs/DESIGN.md 10.2): install.cmd [arguments of install.ps1]. Double-click works too.
rem The execution policy is bypassed for this one process; Set-ExecutionPolicy is never called. install.ps1 itself runs
rem without administrator rights and asks for them once (one UAC prompt) for its admin part; started by double-click it
rem keeps the window open at the end so the result can be read.
setlocal
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
rem From a 32-bit cmd.exe, System32 is redirected; Sysnative reaches the 64-bit PowerShell.
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "PS=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
exit /b %ERRORLEVEL%