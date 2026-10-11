@echo off
setlocal
if not exist "%~dp0bin" mkdir "%~dp0bin"
for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VSROOT=%%i"
if not defined VSROOT exit /b 1
call "%VSROOT%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b 1
cl /nologo /LD /MD /std:c++17 /EHsc /W4 /O2 /Zi /Fd:"%~dp0bin\SqlDebugBridge.pdb" /Fo:"%~dp0bin\SqlDebugBridge.obj" /Fe:"%~dp0bin\SqlDebugBridge.dll" "%~dp0SqlDebugBridge.cpp" ole32.lib uuid.lib /link /DEBUG /OPT:REF,ICF /INCREMENTAL:NO /MAP:"%~dp0bin\SqlDebugBridge.map"
exit /b %errorlevel%
