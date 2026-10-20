@echo off
title Build OmniClean
echo ==============================================
echo Building OmniClean Standalone Native Executable
echo ==============================================

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set WPF_DIR=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF

if not exist "%CSC%" (
    echo Error: C# compiler not found at %CSC%
    pause
    exit /b 1
)

"%CSC%" /target:winexe /platform:anycpu /optimize+ /lib:"%WPF_DIR%" /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll /r:System.dll /r:System.Core.dll /win32icon:"app.ico" /win32manifest:"app.manifest" /out:"OmniClean.exe" "OmniClean.cs" "OmniClean.Engine.cs" "OmniClean.Analysis.cs"

if %errorlevel% equ 0 (
    echo.
    echo [SUCCESS] OmniClean.exe built successfully!
) else (
    echo.
    echo [ERROR] Build failed.
)

pause
