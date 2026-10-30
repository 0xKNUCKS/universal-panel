@echo off
rem Builds a Release copy of the panel. Output: src\UniversalPanel\bin\Release\net10.0-windows\win-x64\
cd /d "%~dp0"
taskkill /IM UniversalPanel.exe /F >nul 2>&1
dotnet build -c Release --nologo
pause
