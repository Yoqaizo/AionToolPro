@echo off
chcp 65001 >nul
title AionToolPro - Release Builder
cd /d "%~dp0"
echo ==========================================================
echo   Compiling AionToolPro.exe...
echo ==========================================================
dotnet build "AionToolPro.Desktop\AionToolPro.Desktop.csproj" -c Release
if errorlevel 1 (
    echo.
    echo [ERROR] Build failed.
    pause
    exit /b 1
)
echo.
echo [OK] Build succeeded!
echo Executable path: "%~dp0AionToolPro.Desktop\bin\Release\net8.0-windows\AionToolPro.exe"
explorer "%~dp0AionToolPro.Desktop\bin\Release\net8.0-windows"
pause
