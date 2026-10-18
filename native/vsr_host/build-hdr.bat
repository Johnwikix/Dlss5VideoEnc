@echo off
setlocal EnableExtensions
for /f "usebackq tokens=*" %%i in (`"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find VC\Auxiliary\Build\vcvars64.bat`) do call "%%i" >nul
if errorlevel 1 exit /b 1
set "HDR_BUILD=%~dp0..\..\build\native\driver_hdr"
set "HDR_RUNTIME=%~dp0..\..\runtime"
if not exist "%HDR_BUILD%" mkdir "%HDR_BUILD%"
if not exist "%HDR_RUNTIME%" mkdir "%HDR_RUNTIME%"
cl /nologo /std:c++17 /O2 /EHsc /MT /LD /Fo"%HDR_BUILD%\driver_hdr_host.obj" "%~dp0driver_hdr_host.cpp" D3D11.lib DXGI.lib /link /OUT:"%HDR_RUNTIME%\driver_hdr_host.dll" /PDB:"%HDR_BUILD%\driver_hdr_host.pdb" /IMPLIB:"%HDR_BUILD%\driver_hdr_host.lib"
exit /b %errorlevel%
