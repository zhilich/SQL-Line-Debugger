@echo off
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
cl /nologo /LD /EHsc /W3 /Fo:bin\NativeHost.obj /Fe:bin\NativeHost.dll NativeHost.cpp ole32.lib uuid.lib
