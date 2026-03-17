@echo off
setlocal

cd /d "%~dp0"
set "APP_EXE=%cd%\Published\AppCatalogue.exe"

if not exist "%APP_EXE%" (
  echo Published executable not found at:
  echo %APP_EXE%
  echo.
  echo Run publish.bat first.
  pause
  exit /b 1
)

start "" "%APP_EXE%"

endlocal
