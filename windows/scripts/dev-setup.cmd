@echo off
rem Starts dev-setup.ps1 elevated (one UAC prompt); the window stays open so the result can be read.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell.exe -Verb RunAs -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-NoExit','-File','%~dp0dev-setup.ps1'"
