@echo off
rem Builds RyGTServer.exe with the C# compiler that ships with Windows' .NET Framework 4.x.
rem Usage: build.cmd [output folder]
setlocal
set OUT=%~1
if "%OUT%"=="" set OUT=%~dp0
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:exe /platform:x64 /optimize+ /win32icon:"%~dp0..\loader\ryGT.ico" /out:"%OUT%\RyGTServer.exe" ^
    /r:System.dll /r:System.Core.dll /r:System.Web.Extensions.dll ^
    "%~dp0RyGTServer.cs" "%~dp0Gt7.cs"
