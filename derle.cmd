@echo off
cd /d "%~dp0"
echo Boyut derleniyor... (pencere kendiliginden kapanir)
powershell -NoProfile -ExecutionPolicy Bypass -Command "& { dotnet --list-sdks; & '.\build.ps1' } *>&1 | Tee-Object -FilePath build.log"
echo EXIT=%ERRORLEVEL%>> build.log
