@echo off
chcp 65001 >nul
setlocal
set "ROOT=%~dp0.."
call "%~dp0build-env.bat"
set "TARGET=%ROOT%\app-standalone"

echo === Автономная сборка (не требует установленного .NET на других ПК) ===
echo Размер папки будет примерно 160-200 МБ.
echo.
dotnet publish "%ROOT%\src\PcMonitor\PcMonitor.csproj" -c Release -r win-x64 --self-contained true -o "%TARGET%" --nologo
if errorlevel 1 (
    echo [!] Сборка не удалась.
    exit /b 1
)

call "%~dp0build-rust-core.bat" "%TARGET%"
if errorlevel 1 exit /b 1

echo.
echo [OK] Готово: %TARGET%\PcMonitor.exe
endlocal
