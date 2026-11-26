@echo off
rem Called by build scripts: sets process-local tool paths only.
if not defined ROOT set "ROOT=%~dp0.."
if exist "%ROOT%\.work\tools\dotnet\dotnet.exe" (
    set "DOTNET_ROOT=%ROOT%\.work\tools\dotnet"
    set "DOTNET_CLI_HOME=%ROOT%\.work\tools\dotnet-cli-home"
    set "NUGET_PACKAGES=%ROOT%\.work\tools\nuget"
    set "NUGET_HTTP_CACHE_PATH=%ROOT%\.work\tools\nuget-http-cache"
    set "PATH=%ROOT%\.work\tools\dotnet;%PATH%"
)
if not defined ProgramFiles set "ProgramFiles=C:\Program Files"
if not defined ProgramFiles(x86) set "ProgramFiles(x86)=C:\Program Files (x86)"
if not defined ProgramW6432 set "ProgramW6432=C:\Program Files"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
exit /b 0
