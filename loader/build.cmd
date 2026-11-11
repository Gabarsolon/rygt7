@echo off
rem Builds RyGT7.exe (the loader) with the C# compiler that ships with Windows' .NET Framework 4.x.
rem Usage: build.cmd [output folder]
setlocal
set OUT=%~1
if "%OUT%"=="" set OUT=%~dp0
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize+ /win32icon:"%~dp0ryGT7.ico" /out:"%OUT%\RyGT7.exe" ^
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Xml.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
    "%~dp0Version.cs" "%~dp0RyGTLoader.cs" "%~dp0GT7Setup.cs" "%~dp0Bindings.cs" "%~dp0ControlsPage.cs" "%~dp0PortsForm.cs" "%~dp0Logging.cs" "%~dp0Wheel.cs" "%~dp0DlssDownload.cs" "%~dp0Online.cs" "%~dp0Updater.cs"
