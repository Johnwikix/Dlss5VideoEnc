@echo off
setlocal EnableExtensions
for /f "usebackq tokens=*" %%i in (`"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find VC\Auxiliary\Build\vcvars64.bat`) do call "%%i" >nul
if errorlevel 1 exit /b 1
set "PROBE_BUILD=%~dp0..\..\build\driver-probe"
if not exist "%PROBE_BUILD%" mkdir "%PROBE_BUILD%"
cl /nologo /std:c++17 /O2 /EHsc /MT /Fo"%PROBE_BUILD%\driver_probe.obj" "%~dp0driver_probe.cpp" D3D11.lib DXGI.lib User32.lib /link /OUT:"%PROBE_BUILD%\driver_probe.exe"
exit /b %errorlevel%
