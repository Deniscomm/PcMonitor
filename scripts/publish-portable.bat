@echo off
chcp 65001 >nul
setlocal
set "ROOT=%~dp0.."
call "%~dp0build-env.bat"
set "TARGET=%~1"
if not defined TARGET set "TARGET=%ROOT%\app"

echo === Переносимая сборка «Мониторинг ПК» ===
echo Папка назначения: %TARGET%
echo В сборку войдёт среда .NET: на другом компьютере ничего устанавливать не нужно.
echo.

if exist "%TARGET%\PcMonitor.exe" (
    tasklist /FI "IMAGENAME eq PcMonitor.exe" 2>nul | find /I "PcMonitor.exe" >nul
    if not errorlevel 1 (
        echo [!] Приложение запущено. Закройте его и повторите сборку.
        pause
        exit /b 1
    )
)

rem Чистим промежуточные файлы: иначе в сборку может попасть манифест другой конфигурации.
dotnet clean "%ROOT%\src\PcMonitor\PcMonitor.csproj" -c Release -r win-x64 --nologo >nul

echo [1/4] Сборка сжатой автономной версии (около 70 МБ плюс системные компоненты)...
dotnet publish "%ROOT%\src\PcMonitor\PcMonitor.csproj" -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "%TARGET%" --nologo
if errorlevel 1 (
    echo [!] Сборка не удалась.
    pause
    exit /b 1
)

call "%~dp0build-rust-core.bat" "%TARGET%"
if errorlevel 1 (
    echo [!] Сборка Rust-мониторинга не удалась.
    pause
    exit /b 1
)

echo [2/4] Копируем системные библиотеки, нужные для рендеров...
rem d3dcompiler_47.dll нужен для компиляции шейдеров Direct3D: на чистой Windows его может не быть.
if exist "%SystemRoot%\System32\d3dcompiler_47.dll" (
    copy /Y "%SystemRoot%\System32\d3dcompiler_47.dll" "%TARGET%\d3dcompiler_47.dll" >nul
    echo     d3dcompiler_47.dll скопирована
) else (
    echo     [!] d3dcompiler_47.dll не найдена в системе
)

echo [3/4] Копируем установщик драйвера датчиков PawnIO (если найден)...
set "PAWNIO="
if exist "%ProgramFiles%\PawnIO\PawnIO_setup.exe" set "PAWNIO=%ProgramFiles%\PawnIO\PawnIO_setup.exe"
if exist "%ROOT%\tools\PawnIO_setup.exe" set "PAWNIO=%ROOT%\tools\PawnIO_setup.exe"
if defined PAWNIO (
    copy /Y "%PAWNIO%" "%TARGET%\PawnIO_setup.exe" >nul
    echo     PawnIO_setup.exe скопирован — на новом ПК запустите его один раз
) else (
    echo     [!] Установщик PawnIO не найден. Без него часть датчиков недоступна.
)

echo [4/4] Записываем памятку...
> "%TARGET%\ПЕРВЫЙ-ЗАПУСК.txt" echo Мониторинг ПК — переносимая сборка
>> "%TARGET%\ПЕРВЫЙ-ЗАПУСК.txt" echo.
>> "%TARGET%\ПЕРВЫЙ-ЗАПУСК.txt" echo 1. Скопируйте всю эту папку на другой компьютер (можно на флешку).
>> "%TARGET%\ПЕРВЫЙ-ЗАПУСК.txt" echo 2. Запустите PcMonitor.exe — среда .NET уже внутри, устанавливать ничего не нужно.
>> "%TARGET%\ПЕРВЫЙ-ЗАПУСК.txt" echo 3. Если температур процессора и платы нет — один раз запустите PawnIO_setup.exe
>> "%TARGET%\ПЕРВЫЙ-ЗАПУСК.txt" echo    (драйвер датчиков, нужны права администратора) и перезапустите программу.
>> "%TARGET%\ПЕРВЫЙ-ЗАПУСК.txt" echo 4. Для управления вентиляторами, разгона и полного набора датчиков
>> "%TARGET%\ПЕРВЫЙ-ЗАПУСК.txt" echo    приложение запрашивает права администратора — это нормально.
>> "%TARGET%\ПЕРВЫЙ-ЗАПУСК.txt" echo 5. Настройки, журналы и отчёты лежат в подпапке data рядом с программой,
>> "%TARGET%\ПЕРВЫЙ-ЗАПУСК.txt" echo    поэтому папку можно свободно переносить.

echo.
echo === Готово ===
echo Папка: %TARGET%
echo Размер: 
powershell -NoProfile -Command "$s=(Get-ChildItem -Recurse '%TARGET%' | Measure-Object -Property Length -Sum).Sum; '{0:0} МБ' -f ($s/1MB)"
echo.
echo Проверьте на этом ПК: %TARGET%\PcMonitor.exe
pause
