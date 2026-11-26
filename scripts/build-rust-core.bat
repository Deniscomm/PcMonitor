@echo off
chcp 65001 >nul
setlocal
set "ROOT=%~dp0.."
set "TARGET=%~1"
if not defined TARGET set "TARGET=%ROOT%\app"
if exist "%ROOT%\.work\tools\cargo\bin\cargo.exe" (
    set "RUSTUP_HOME=%ROOT%\.work\tools\rustup"
    set "CARGO_HOME=%ROOT%\.work\tools\cargo"
    set "PATH=%ROOT%\.work\tools\cargo\bin;%PATH%"
)

where cargo >nul 2>&1
if errorlevel 1 (
    echo [!] Rust stable/MSVC не найден. Установите Rust и повторите сборку.
    exit /b 1
)

cargo build --manifest-path "%ROOT%\src-rust\pcmonitor-core\Cargo.toml" --release
if errorlevel 1 exit /b 1

if not exist "%TARGET%" mkdir "%TARGET%"
copy /Y "%ROOT%\src-rust\pcmonitor-core\target\release\pcmonitor-native.exe" "%TARGET%\pcmonitor-native.exe" >nul
if errorlevel 1 exit /b 1

echo [OK] Rust monitor sidecar built in %TARGET%\
endlocal
