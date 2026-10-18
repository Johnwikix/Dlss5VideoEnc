@echo off
setlocal EnableExtensions
for /f "usebackq tokens=*" %%i in (`"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find VC\Auxiliary\Build\vcvars64.bat`) do call "%%i" >nul
if errorlevel 1 exit /b 1
set "HDR_PROBE_BUILD=%~dp0..\..\build\driver-hdr-probe"
if not exist "%HDR_PROBE_BUILD%" mkdir "%HDR_PROBE_BUILD%"
cl /nologo /std:c++17 /O2 /EHsc /MT /Fo"%HDR_PROBE_BUILD%\driver_hdr_probe.obj" "%~dp0driver_hdr_probe.cpp" D3D11.lib DXGI.lib User32.lib /link /OUT:"%HDR_PROBE_BUILD%\driver_hdr_probe.exe"
exit /b %errorlevel%
