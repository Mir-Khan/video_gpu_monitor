@echo off
rem Builds GpuMonitor.exe with the C# compiler that ships with Windows
rem (.NET Framework 4.8), so that nothing needs to be installed.
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo Could not find csc.exe at %CSC%
    exit /b 1
)
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /warn:4 ^
  /out:"%~dp0GpuMonitor.exe" ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:System.Management.dll /r:System.Web.Extensions.dll ^
  "%~dp0src\*.cs"
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)
echo Built %~dp0GpuMonitor.exe
