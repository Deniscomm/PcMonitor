@echo off
chcp 65001 >nul
setlocal
set "ROOT=%~dp0.."
set "EXE=%ROOT%\app\PcMonitor.exe"
if exist "%ROOT%\outputs\PCMonitorPortable\PcMonitor.exe" set "EXE=%ROOT%\outputs\PCMonitorPortable\PcMonitor.exe"
if exist "%ROOT%\outputs\PCMonitorPortable-Release\PcMonitor.exe" set "EXE=%ROOT%\outputs\PCMonitorPortable-Release\PcMonitor.exe"

if not exist "%EXE%" (
    echo Приложение ещё не собрано — запускаю сборку...
    call "%~dp0build.bat"
    if errorlevel 1 exit /b 1
)

echo Запуск мониторинга (подтвердите запрос прав администратора)...
start "" "%EXE%" %*
endlocal
