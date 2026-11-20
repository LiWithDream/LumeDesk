@echo off
chcp 65001 >nul
setlocal EnableDelayedExpansion

set "ROOT=%~dp0"
set "EXE=%ROOT%dist\LumeDesk.exe"
if not exist "%EXE%" if exist "%ROOT%LumeDesk.exe" set "EXE=%ROOT%LumeDesk.exe"
set "DATA=%ROOT%data"

echo ============================================================
echo   LumeDesk - Adaptive 4K Frosted-Glass Desktop Launcher
echo ============================================================

if not exist "%EXE%" (
    echo [1/3] Compiling LumeDesk.exe via built-in .NET 4.8...
    powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%build.ps1"
    if errorlevel 1 (
        echo [ERROR] Build failed. Please ensure Windows .NET Framework 4.8 is installed.
        pause
        exit /b 1
    )
    set "EXE=%ROOT%dist\LumeDesk.exe"
) else (
    echo [1/3] Found prebuilt %EXE%
)

set "HAS_CAT=0"
if exist "%DATA%" (
    for /d %%D in ("%DATA%\*") do (
        set "NAME=%%~nxD"
        if not "!NAME:~0,1!"=="_" if not "!NAME:~0,1!"=="." set "HAS_CAT=1"
    )
)

if "!HAS_CAT!"=="0" (
    echo [2/3] First run detected - importing Desktop shortcuts non-destructively...
    powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%setup_folders.ps1"
) else (
    echo [2/3] Existing data\ folders detected - skipping auto-import.
)

echo [3/3] Launching LumeDesk...
start "" /D "%ROOT%" "%EXE%"
exit /b 0
