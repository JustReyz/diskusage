@echo off
setlocal

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Could not find csc.exe. Install .NET Framework 4.8.
  exit /b 1
)

cd /d "%~dp0"

echo Building diskusage.exe ^(console^)...
"%CSC%" /nologo /optimize+ /target:exe /platform:anycpu /win32manifest:app.manifest /out:diskusage.exe DiskUsage.cs
if errorlevel 1 goto fail

echo Building diskusage-gui.exe ^(window^)...
"%CSC%" /nologo /optimize+ /target:winexe /platform:anycpu /win32manifest:app.manifest /out:diskusage-gui.exe Scanner.cs GuiTheme.cs Picker.cs DiskUsageGui.cs
if errorlevel 1 goto fail

echo Done.
exit /b 0

:fail
echo Build failed.
exit /b 1
