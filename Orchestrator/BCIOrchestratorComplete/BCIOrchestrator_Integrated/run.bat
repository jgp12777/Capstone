@echo off
echo ========================================
echo BCI Orchestrator Launcher
echo ========================================
echo.
echo Select mode:
echo   1. Normal mode (no keyboard)
echo   2. With keyboard simulation
echo   3. With keyboard + LAN access
echo   4. Debug mode
echo   5. Exit
echo.

set /p choice="Enter choice (1-5): "

if "%choice%"=="1" (
    echo Starting in normal mode...
    BCIOrchestrator.exe
) else if "%choice%"=="2" (
    echo Starting with keyboard simulation...
    BCIOrchestrator.exe --keyboard
) else if "%choice%"=="3" (
    echo Starting with keyboard + LAN...
    BCIOrchestrator.exe --keyboard --allow-lan
) else if "%choice%"=="4" (
    echo Starting in debug mode...
    BCIOrchestrator.exe --keyboard --debug
) else if "%choice%"=="5" (
    exit /b 0
) else (
    echo Invalid choice!
    pause
    exit /b 1
)
