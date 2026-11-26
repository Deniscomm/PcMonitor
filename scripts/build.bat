@echo off
chcp 65001 >nul
setlocal
set "ROOT=%~dp0.."
call "%~dp0build-env.bat"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [!] Не найден .NET SDK. Установите .NET 10 SDK: https://dotnet.microsoft.com/download
    exit /b 1
)

tasklist /FI "IMAGENAME eq PcMonitor.exe" 2>nul | find /I "PcMonitor.exe" >nul
if not errorlevel 1 (
    echo [!] Приложение запущено. Закройте окно «Мониторинг ПК» и повторите сборку.
    exit /b 1
)

echo === Сборка приложения «Мониторинг ПК» ===
rem Чистим промежуточные файлы: иначе может остаться манифест другой сборки.
dotnet clean "%ROOT%\src\PcMonitor\PcMonitor.csproj" -c Release -r win-x64 --nologo >nul
dotnet publish "%ROOT%\src\PcMonitor\PcMonitor.csproj" -c Release -r win-x64 --self-contained false -o "%ROOT%\app" --nologo
if errorlevel 1 (
    echo [!] Сборка не удалась.
    exit /b 1
)

call "%~dp0build-rust-core.bat"
if errorlevel 1 (
    echo [!] Сборка Rust-мониторинга не удалась.
    exit /b 1
)

echo.
echo [OK] Готово: %ROOT%\app\PcMonitor.exe
echo      Запуск: scripts\run.bat  (потребуются права администратора)
endlocal
