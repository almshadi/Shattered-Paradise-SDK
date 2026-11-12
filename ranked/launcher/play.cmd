@echo off
title Shattered Paradise

rem Shattered Paradise needs the .NET 6 runtime (x64).
where dotnet >nul 2>&1
if errorlevel 1 goto nodotnet
dotnet --list-runtimes 2>nul | findstr /c:"Microsoft.NETCore.App 6." >nul
if errorlevel 1 goto nodotnet

cd /d "%~dp0engine"
dotnet bin\OpenRA.dll Game.Mod=sp Engine.EngineDir=".." Engine.LaunchPath="%~dp0play.cmd" Engine.ModSearchPaths="%~dp0mods" Graphics.DisableGLDebugMessageCallback=True %*
if errorlevel 1 (
  echo ----------------------------------------
  echo Shattered Paradise crashed. Logs: %APPDATA%\OpenRA\Logs
  pause
)
goto :eof

:nodotnet
echo ----------------------------------------
echo Shattered Paradise needs the .NET 6 runtime, which was not found.
echo.
echo Download and install ".NET Desktop Runtime 6.0" (x64) from:
echo   https://dotnet.microsoft.com/download/dotnet/6.0
echo.
echo Then run play.cmd again.
echo ----------------------------------------
pause
exit /b 1
