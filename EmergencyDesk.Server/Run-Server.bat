@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo .NET 8 SDK was not found. Install the .NET 8 SDK, then run this file again.
    pause
    exit /b 1
)

echo Starting EmergencyDesk Server.
echo Make sure MySQL is running in XAMPP. On first run, the server creates emergency_db and its tables.
echo Desktop API: http://localhost:5000
echo Android emulator API: http://10.0.2.2:5000
echo Starter desktop login: admin / EmergencyDesk123!
echo.

dotnet run --project "%~dp0EmergencyDesk.Server.csproj"

echo.
echo The server has stopped.
pause
