@echo off
setlocal

cd /d "%~dp0"
set "APP_EXE=%cd%\Published\AppCatalogue\AppCatalogue.exe"

if not exist "%APP_EXE%" (
  echo Published AppCatalogue executable not found:
  echo %APP_EXE%
  echo.
  echo Run publish_all.bat first.
  pause
  exit /b 1
)

start "" "%APP_EXE%"
endlocal
