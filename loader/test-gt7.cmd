@echo off
setlocal
set OUT=%~1
if "%OUT%"=="" exit /b 2
if not exist "%OUT%" mkdir "%OUT%"
for %%I in ("%OUT%") do set OUT=%%~fI
set TEMP=%OUT%\temp
set TMP=%TEMP%
if not exist "%TEMP%" mkdir "%TEMP%"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /target:exe /main:RyGT.GT7SetupTests /out:"%OUT%\GT7SetupTests.exe" ^
 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Xml.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll ^
 "%~dp0Version.cs" "%~dp0RyGTLoader.cs" "%~dp0GT7Setup.cs" "%~dp0GT7SetupTests.cs" "%~dp0Bindings.cs" "%~dp0ControlsPage.cs" "%~dp0PortsForm.cs" "%~dp0Logging.cs" "%~dp0Wheel.cs" "%~dp0DlssDownload.cs" "%~dp0Online.cs" "%~dp0Updater.cs"
if errorlevel 1 exit /b 1
"%OUT%\GT7SetupTests.exe" %2 %3 %4 %5 %6 %7
if errorlevel 1 exit /b 1
call "%~dp0build.cmd" "%OUT%"
if errorlevel 1 exit /b 1
"%CSC%" /nologo /target:exe /out:"%OUT%\LoggingTests.exe" "%~dp0Logging.cs" "%~dp0LoggingTests.cs"
if errorlevel 1 exit /b 1
"%OUT%\LoggingTests.exe"
exit /b %errorlevel%
