@echo off
chcp 65001 >nul
setlocal
set "ROOT=%~dp0.."
set "REPORT=%TEMP%\sensor-probe-report.txt"

echo === Диагностика датчиков ===
echo Перечисляю всё, что видит библиотека LibreHardwareMonitor.
echo Запуск с правами администратора даст полный список датчиков.
echo.

dotnet publish "%ROOT%\tools\SensorProbe\SensorProbe.csproj" -c Release -o "%ROOT%\tools\SensorProbe\app" --nologo >nul
if errorlevel 1 (
    echo [!] Не удалось собрать диагностическую утилиту. Нужен .NET SDK.
    exit /b 1
)

powershell -NoProfile -Command "Start-Process -FilePath '%ROOT%\tools\SensorProbe\app\SensorProbe.exe' -Verb RunAs -ArgumentList '--seconds','3' -RedirectStandardOutput '%REPORT%' -Wait"
start "" notepad "%REPORT%"
endlocal
