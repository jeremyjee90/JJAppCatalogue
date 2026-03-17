@echo off
setlocal

cd /d "%~dp0"

set "SDK_FOUND="
for /f "delims=" %%S in ('dotnet --list-sdks 2^>nul') do (
  set "SDK_FOUND=1"
  goto :sdk_found
)
:sdk_found
if not defined SDK_FOUND (
  echo .NET SDK was not found on this machine.
  echo Install .NET 8 SDK, then run publish.bat again.
  pause
  exit /b 1
)

if exist ".\Published\AppCatalogue.exe" del /f /q ".\Published\AppCatalogue.exe"

echo Publishing App Catalogue as a self-contained single-file executable...
dotnet publish ".\AppCatalogue\AppCatalogue.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -o ".\Published"

if errorlevel 1 (
  echo.
  echo Publish failed.
  echo Check errors above, then run this script again.
  pause
  exit /b 1
)

if not exist ".\Published\AppCatalogue.exe" (
  echo.
  echo Publish did not produce .\Published\AppCatalogue.exe.
  echo Check errors above, then run this script again.
  pause
  exit /b 1
)

echo.
echo Publish completed successfully.
echo Output folder: "%cd%\Published"
echo Executable: "%cd%\Published\AppCatalogue.exe"
pause

endlocal
