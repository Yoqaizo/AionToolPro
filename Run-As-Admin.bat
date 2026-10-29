@echo off
chcp 65001 >nul
title AionToolPro - Launcher

fltmc >nul 2>&1
if errorlevel 1 (
    echo Requesting Administrator privileges...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"
echo ==========================================================
echo   Launching AionToolPro 0.5...
echo ==========================================================
dotnet run --project "AionToolPro.Desktop\AionToolPro.Desktop.csproj" -c Release
if errorlevel 1 (
    echo.
    echo [ERROR] Build or execution failed.
    pause
)
