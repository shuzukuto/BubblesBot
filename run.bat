@echo off
:: Kiem tra quyen Admin
net session >nul 2>&1
if %errorLevel% == 0 (
    goto :run
) else (
    echo Dang yeu cau quyen Administrator...
    powershell -Command "Start-Process cmd -ArgumentList '/c \"\"%~dpnx0\"\" %*' -Verb RunAs"
    exit /b
)

:run
REM Launch BubblesBot from this script's directory regardless of where it's invoked from.
cd /d "%~dp0"
dotnet run --project src/BubblesBot.Bot %*
pause
