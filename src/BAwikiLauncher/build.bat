@echo off
setlocal
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul
if errorlevel 1 (echo MSVC not found & exit /b 1)
cd /d "E:\BAwiki Web\app\src\BAwikiLauncher"
cl /nologo /utf-8 /std:c++17 /O2 /EHsc /MT launcher.cpp /Fe:"E:\BAwiki Web\app\BAwikiLauncher.exe" /link /SUBSYSTEM:CONSOLE
if errorlevel 1 exit /b 1
cl /nologo /utf-8 /std:c++17 /O2 /EHsc /MT /LD gpu.cpp /Fe:"E:\BAwiki Web\app\BAwikiGpu.dll"
if errorlevel 1 exit /b 1
echo BUILD_OK
