@echo off
echo ========================================
echo Building BCI Orchestrator
echo ========================================
echo.

REM Change to the directory where this script is located
cd /d "%~dp0"

REM Check if .NET SDK is installed
dotnet --version >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: .NET SDK not found!
    echo Please install .NET 8 SDK from: https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

echo Building release version...
echo Working directory: %CD%
echo.

dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ./Release

if %errorlevel% equ 0 (
    echo.
    echo ========================================
    echo Build successful!
    echo ========================================
    echo.
    echo Output location: .\Release\BCIOrchestrator.exe
    echo.
    echo To run:
    echo   cd Release
    echo   BCIOrchestrator.exe
    echo.
    echo With keyboard mode:
    echo   BCIOrchestrator.exe --keyboard
    echo.
) else (
    echo.
    echo ========================================
    echo Build failed!
    echo ========================================
    pause
    exit /b 1
)

pause
