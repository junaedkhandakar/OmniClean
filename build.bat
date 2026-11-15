@echo off
setlocal
title Build OmniClean
cd /d "%~dp0"

echo ==============================================
echo Building OmniClean Standalone Native Executable
echo ==============================================

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set WPF_DIR=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF

if not exist "%CSC%" (
    echo [ERROR] C# compiler not found at %CSC%
    exit /b 1
)
if not exist "%WPF_DIR%" (
    echo [ERROR] WPF reference assemblies not found at %WPF_DIR%
    exit /b 1
)

rem The response file keeps the compiler flags in one place, so build.bat,
rem CI and a manual build can never drift apart.
"%CSC%" @build.rsp
set BUILD_EXIT=%errorlevel%

if %BUILD_EXIT% neq 0 (
    echo.
    echo [ERROR] Build failed with exit code %BUILD_EXIT%.
    exit /b %BUILD_EXIT%
)

if not exist "OmniClean.exe" (
    echo.
    echo [ERROR] Build reported success but OmniClean.exe was not created.
    exit /b 1
)

echo.
echo [SUCCESS] OmniClean.exe built successfully!

rem Validate the embedded XAML so a broken UI never ships.
"OmniClean.exe" --validate-xaml
if errorlevel 1 (
    echo [ERROR] Embedded XAML validation failed. See xaml-validation.log
    exit /b 1
)
echo [SUCCESS] Embedded XAML validated.

endlocal
exit /b 0
