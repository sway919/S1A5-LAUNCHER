@echo off
rem ==============================================================
rem  S1A5 launcher - build the SHIPPED exe.
rem
rem  Shipped build = net48, a single ~1.7 MB exe (Costura embeds every
rem  managed dependency; .NET Framework 4.8 comes with Windows).
rem
rem  Want the fast-but-fat build instead? It is one command:
rem      dotnet publish S1a5-Launcher.csproj -f net10.0-windows -c Release
rem  which produces a ~74 MB self-contained single file. Its startup
rem  advantage is real: net48 needs 2.4~2.8s just to put the first WPF
rem  window on screen (JIT floor), net10 does it in ~1.2s.
rem
rem  The old copy-into-package step is OPTIONAL now that this is a
rem  standalone repo: if a sibling folder holding server\*.jar exists
rem  (the game package), the exe is copied there; otherwise it is left
rem  in bin\Release\net48\ and this script exits 0.
rem ==============================================================
setlocal
cd /d "%~dp0"

dotnet build S1a5-Launcher.csproj -f net48 -c Release
if errorlevel 1 goto :fail

set OUT=bin\Release\net48\S1a5-Launcher.exe
if not exist "%OUT%" (
  echo [build] no exe produced - see the errors above
  exit /b 1
)

rem The package folder has a Chinese name and a .cmd file must stay pure
rem ASCII, so find it by owner file (server\*.jar) instead of writing
rem the name out.
set DEST=
for /d %%d in ("..\*") do (
  if exist "%%~fd\server\*.jar" if not defined DEST set DEST=%%~fd
)
if not defined DEST (
  echo [build] no package folder found next to this repo - exe left in bin\
  for %%f in ("%OUT%") do echo [build] OK  %%~ff  (%%~zf bytes)
  exit /b 0
)

copy /Y "%OUT%" "%DEST%\S1a5-Launcher.exe" >nul
if errorlevel 1 goto :fail
for %%f in ("%DEST%\S1a5-Launcher.exe") do echo [build] OK  %%f  (%%~zf bytes)
exit /b 0

:fail
echo [build] FAILED
exit /b 1
