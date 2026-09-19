@echo off
setlocal

rem One-shot build + publish: builds the full solution with MSBuild, then
rem publishes the server and launcher into dist\DFLegacy.Server.

cd /d "%~dp0"

set "MSBUILD="
where msbuild >nul 2>&1 && set "MSBUILD=msbuild"
if defined MSBUILD goto :have_msbuild

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" goto :no_msbuild
"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" > "%TEMP%\DFLegacy.msbuild.path" 2>nul
set /p MSBUILD=<"%TEMP%\DFLegacy.msbuild.path"
del "%TEMP%\DFLegacy.msbuild.path" 2>nul

:have_msbuild
if not defined MSBUILD goto :no_msbuild
echo [publish] Using MSBuild: %MSBUILD%

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [publish] dotnet CLI was not found on PATH.
    exit /b 1
)

echo [publish] Building DFLegacy.Emulator.slnx ...
"%MSBUILD%" .\DFLegacy.Emulator.slnx /m /restore /p:Configuration=Release
if errorlevel 1 goto :fail

echo.
echo [publish] Cleaning dist ...
if exist "dist" rmdir /s /q "dist"
if exist "dist" (
    echo [publish] Could not remove dist. Stop any process using it and retry.
    goto :fail
)

echo [publish] Publishing server and launcher to dist\DFLegacy.Server ...
dotnet publish .\src\DFLegacy.Server\DFLegacy.Server.csproj -c Release -o .\dist\DFLegacy.Server
if errorlevel 1 goto :fail
dotnet publish .\src\DFLegacy.Launcher\DFLegacy.Launcher.csproj -c Release -o .\dist\DFLegacy.Server
if errorlevel 1 goto :fail

echo.
echo [publish] Done. Output directory: %CD%\dist\DFLegacy.Server
exit /b 0

:no_msbuild
echo [publish] MSBuild was not found. Run from a Visual Studio developer
echo [publish] prompt, or install Visual Studio with the MSBuild component.
exit /b 1

:fail
echo.
echo [publish] FAILED. See the log above for details.
exit /b 1
