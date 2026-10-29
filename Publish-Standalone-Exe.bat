@echo off
chcp 65001 >nul
title AionToolPro - Standalone Single-File Publisher
cd /d "%~dp0"
echo ==========================================================
echo   Publishing Standalone Single-File AionToolPro.exe...
echo ==========================================================
dotnet publish "AionToolPro.Desktop\AionToolPro.Desktop.csproj" -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:DebugType=None /p:DebugSymbols=false
if errorlevel 1 (
    echo.
    echo [ERROR] Publish failed.
    pause
    exit /b 1
)
echo.
echo [OK] Standalone AionToolPro.exe created successfully!
explorer "%~dp0AionToolPro.Desktop\bin\Release\net8.0-windows\win-x64\publish"
pause
