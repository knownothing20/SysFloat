@echo off
setlocal
set "ROOT_DIR=%~dp0.."
set "OUTPUT_DIR=%ROOT_DIR%\release\win-x64-self-contained"

echo Publishing SysFloat (Self-Contained Single File)...
dotnet publish "%ROOT_DIR%\src\SysFloat\SysFloat.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:UseAppHost=true -p:SuppressTfmSupportBuildWarnings=true -o "%OUTPUT_DIR%" --force
if errorlevel 1 (
  echo.
  echo Publish failed.
  pause
  exit /b 1
)
echo.
echo Output: %OUTPUT_DIR%\SysFloat.exe
echo Copy it next to this script (repo root) to run in place.
pause
