@echo off
rem Builds dlssnr_host_v2.dll from the upstream DLSS5Tool host source.
rem Source: https://github.com/banbanzhige/DLSS5Tool (MIT) - native/host_v2
setlocal EnableExtensions EnableDelayedExpansion

set "ROOT=%~dp0.."
set "SRC=%ROOT%\_ref\DLSS5Tool\native\host_v2\dlssnr_host_v2.cpp"
set "OUTPUT=%ROOT%\runtime"
set "INTERMEDIATE=%ROOT%\build\native\host_v2"
set "NGX_INCLUDE=%ROOT%\third_party\NVIDIA-DLSS\include"
set "NGX_LIB=%ROOT%\third_party\NVIDIA-DLSS\lib\Windows_x86_64\x64\nvsdk_ngx_s.lib"

where cl >nul 2>nul
if errorlevel 1 (
  set "VCVARS=I:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat"
  if not exist "!VCVARS!" (
    set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
    if not exist "!VSWHERE!" set "VSWHERE=C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
    if not exist "!VSWHERE!" (
      echo [error] vswhere.exe not found.
      exit /b 1
    )
    for /f "usebackq tokens=*" %%i in (`"!VSWHERE!" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find VC\Auxiliary\Build\vcvars64.bat`) do set "VCVARS=%%i"
  )
  if not defined VCVARS (
    echo [error] Visual C++ x64 build environment not found.
    exit /b 1
  )
  call "!VCVARS!" >nul
  if errorlevel 1 exit /b 1
)

if not exist "%SRC%" (
  echo [error] host source not found: %SRC%
  exit /b 1
)
if not exist "%NGX_INCLUDE%\nvsdk_ngx.h" (
  echo [error] NVIDIA NGX SDK headers not found at %NGX_INCLUDE%
  exit /b 1
)
if not exist "%NGX_LIB%" (
  echo [error] NVIDIA NGX import library not found at %NGX_LIB%
  exit /b 1
)

if not exist "%OUTPUT%" mkdir "%OUTPUT%"
if not exist "%INTERMEDIATE%" mkdir "%INTERMEDIATE%"

cl /nologo /std:c++17 /O2 /EHsc /MT /LD /I"%NGX_INCLUDE%" /Fo"%INTERMEDIATE%\dlssnr_host_v2.obj" ^
  "%SRC%" "%NGX_LIB%" D3D12.lib DXGI.lib Advapi32.lib User32.lib ^
  /link /OUT:"%OUTPUT%\dlssnr_host_v2.dll" /PDB:"%INTERMEDIATE%\dlssnr_host_v2.pdb"
exit /b %errorlevel%
