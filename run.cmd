@echo off
rem Bridle'i derler ve acar.
cd /d "%~dp0src\Bridle"
dotnet build -nologo -v q -c Release || exit /b 1
start "" "bin\Release\net10.0\Bridle.exe"
