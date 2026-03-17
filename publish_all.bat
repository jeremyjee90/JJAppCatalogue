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
  echo Install .NET 8 SDK, then run publish_all.bat again.
  pause
  exit /b 1
)

set "OUTPUT_ROOT=%cd%\Published"
set "APPCATALOGUE_OUT=%OUTPUT_ROOT%\AppCatalogue"
set "ADMIN_OUT=%OUTPUT_ROOT%\AppCatalogueAdmin"

if exist "%APPCATALOGUE_OUT%" rd /s /q "%APPCATALOGUE_OUT%"
if exist "%ADMIN_OUT%" rd /s /q "%ADMIN_OUT%"

echo Publishing AppCatalogue...
dotnet publish ".\AppCatalogue\AppCatalogue.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -o "%APPCATALOGUE_OUT%"
if errorlevel 1 (
  echo.
  echo AppCatalogue publish failed.
  pause
  exit /b 1
)

if not exist "%APPCATALOGUE_OUT%\AppCatalogue.exe" (
  echo.
  echo AppCatalogue publish did not produce AppCatalogue.exe
  pause
  exit /b 1
)

echo Publishing AppCatalogueAdmin...
dotnet publish ".\AppCatalogueAdmin\AppCatalogueAdmin.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -o "%ADMIN_OUT%"
if errorlevel 1 (
  echo.
  echo AppCatalogueAdmin publish failed.
  pause
  exit /b 1
)

if not exist "%ADMIN_OUT%\AppCatalogueAdmin.exe" (
  echo.
  echo AppCatalogueAdmin publish did not produce AppCatalogueAdmin.exe
  pause
  exit /b 1
)

echo.
echo Publish completed successfully.
echo AppCatalogue EXE: %APPCATALOGUE_OUT%\AppCatalogue.exe
echo AppCatalogueAdmin EXE: %ADMIN_OUT%\AppCatalogueAdmin.exe
pause

endlocal
