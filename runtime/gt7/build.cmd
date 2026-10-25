@echo off
setlocal
set OUT=%~1
if "%OUT%"=="" exit /b 2
if not exist "%OUT%" mkdir "%OUT%"
for %%I in ("%OUT%") do set OUT=%%~fI
for %%F in ("%~dp0import_*.def") do (
  llvm-lib /nologo /def:"%%F" /machine:x64 /out:"%OUT%\%%~nF.lib"
  if errorlevel 1 exit /b 1
)
clang --target=x86_64-pc-windows-msvc -O2 -ffreestanding -fno-builtin -fno-stack-protector -c "%~dp0gt7_stubs.c" -o "%OUT%\gt7_stubs.obj"
if errorlevel 1 exit /b 1
lld-link /dll /nodefaultlib /entry:DllEntry /def:"%~dp0gt7_stubs.def" /out:"%OUT%\libSceTextToSpeech.prx" "%OUT%\gt7_stubs.obj" kernel32.lib "%OUT%\import_libSceAjm_native_prx.lib" "%OUT%\import_libSceSsl_prx.lib" "%OUT%\import_libSceVideoRecordingP_prx.lib"
if errorlevel 1 exit /b 1
gcc -shared -O2 -s -o "%OUT%\libSceMbus.prx" "%~dp0libSceMbus.c" "%~dp0libSceMbus.def"
exit /b %errorlevel%
